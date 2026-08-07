use crate::CoreError;
use clipboard_crypto::{DerivedKey, ObjectCipher};
use clipboard_domain::{FileBundle, FileBundleCache, FileEntry, FileEntryKind};
use serde::{Deserialize, Serialize};
use std::fs::{self, File, OpenOptions};
use std::io::{Read, Write};
use std::path::{Path, PathBuf};
use std::time::{Duration, SystemTime, UNIX_EPOCH};
use uuid::Uuid;

const CHUNK_SIZE: usize = 1024 * 1024;
const CACHE_FORMAT_VERSION: u8 = 1;
const STAGING_MAX_AGE_MS: i64 = 60 * 60 * 1000;
const STAGING_STARTUP_MAX_AGE_MS: i64 = 24 * 60 * 60 * 1000;

#[derive(Debug, Serialize, Deserialize)]
struct CacheManifest {
    version: u8,
    roots: Vec<CacheRoot>,
    files: Vec<CacheFile>,
}

#[derive(Debug, Serialize, Deserialize)]
struct CacheRoot {
    name: String,
    kind: FileEntryKind,
    size: u64,
    modified_ms: i64,
}

#[derive(Debug, Serialize, Deserialize)]
struct CacheFile {
    index: usize,
    root: usize,
    relative_path: String,
    size: u64,
    modified_ms: i64,
    chunks: u32,
}

struct SourceFile {
    root: usize,
    relative_path: String,
    source_path: PathBuf,
    size: u64,
    modified_ms: i64,
}

pub(crate) struct FileCache {
    root: PathBuf,
    pending: PathBuf,
    staging: PathBuf,
    vault_id: Uuid,
    key: DerivedKey,
}

impl FileCache {
    pub(crate) fn open(
        data_dir: &Path,
        vault_id: Uuid,
        key: clipboard_crypto::DerivedKey,
    ) -> Result<Self, CoreError> {
        let root = data_dir.join("objects").join("file-cache");
        let pending = root.join(".pending");
        let staging = data_dir.join("staging");
        let cache = Self {
            root,
            pending,
            staging,
            vault_id,
            key,
        };
        cache.cleanup_pending()?;
        cache.cleanup_staging(STAGING_STARTUP_MAX_AGE_MS)?;
        cache.start_staging_cleanup();
        Ok(cache)
    }

    pub(crate) fn cache_bundle(
        &self,
        item_id: Uuid,
        bundle: &FileBundle,
        max_bytes: u64,
    ) -> Result<FileBundleCache, CoreError> {
        let (manifest, sources, total_bytes) = scan_bundle(bundle)?;
        if total_bytes > max_bytes {
            return Err(CoreError::FileCacheTooLarge {
                actual: total_bytes,
                maximum: max_bytes,
            });
        }

        fs::create_dir_all(&self.pending)?;
        let pending_item = self.pending.join(item_id.to_string());
        let final_item = self.root.join(item_id.to_string());
        let result = (|| -> Result<(), CoreError> {
            if pending_item.exists() {
                fs::remove_dir_all(&pending_item)?;
            }
            fs::create_dir_all(&pending_item)?;
            for (index, source) in sources.iter().enumerate() {
                write_chunks(
                    &self.key,
                    self.vault_id,
                    item_id,
                    index,
                    source,
                    &pending_item,
                )?;
            }
            let manifest_bytes = serde_json::to_vec(&manifest)?;
            let sealed = self
                .object_cipher(&manifest_aad(self.vault_id, item_id))?
                .seal(&manifest_bytes, &manifest_aad(self.vault_id, item_id))?;
            let manifest_path = pending_item.join("manifest.clipobj");
            let mut manifest_file = File::create(manifest_path)?;
            manifest_file.write_all(&sealed)?;
            manifest_file.sync_all()?;
            drop(manifest_file);
            if final_item.exists() {
                fs::remove_dir_all(&final_item)?;
            }
            fs::rename(&pending_item, &final_item)?;
            Ok(())
        })();
        if result.is_err() {
            let _ = fs::remove_dir_all(&pending_item);
        }
        result.map(|()| FileBundleCache {
            total_bytes,
            cached_at_ms: now_ms(),
        })
    }

    pub(crate) fn materialize(
        &self,
        item_id: Uuid,
        bundle: &FileBundle,
    ) -> Result<Vec<FileEntry>, CoreError> {
        self.cleanup_staging(STAGING_MAX_AGE_MS)?;
        let cache_dir = self.root.join(item_id.to_string());
        let manifest = self.read_manifest(item_id, &cache_dir)?;
        let staging_dir = self.staging.join(item_id.to_string());
        ensure_directory(&self.staging)?;
        if staging_dir.exists() {
            reject_reparse(&staging_dir, &fs::symlink_metadata(&staging_dir)?)?;
            fs::remove_dir_all(&staging_dir)?;
        }
        ensure_directory(&staging_dir)?;

        for (root_index, root) in manifest.roots.iter().enumerate() {
            let root_parent = staging_dir.join(root_index.to_string());
            ensure_directory(&root_parent)?;
            let output_root = root_parent.join(&root.name);
            match root.kind {
                FileEntryKind::File => {
                    let file = manifest
                        .files
                        .iter()
                        .find(|file| file.root == root_index && file.relative_path.is_empty())
                        .ok_or_else(|| {
                            CoreError::InvalidCommand("file cache manifest is incomplete".into())
                        })?;
                    restore_file(
                        &self.key,
                        self.vault_id,
                        item_id,
                        file,
                        &cache_dir,
                        &output_root,
                    )?;
                }
                FileEntryKind::Directory => {
                    ensure_directory(&output_root)?;
                    for file in manifest.files.iter().filter(|file| file.root == root_index) {
                        let output = safe_join(&output_root, &file.relative_path)?;
                        if let Some(parent) = output.parent() {
                            ensure_directory(parent)?;
                        }
                        restore_file(&self.key, self.vault_id, item_id, file, &cache_dir, &output)?;
                    }
                }
            }
        }

        Ok(bundle
            .entries
            .iter()
            .zip(manifest.roots.iter())
            .enumerate()
            .map(|(root_index, (entry, root))| FileEntry {
                path: staging_dir
                    .join(root_index.to_string())
                    .join(&root.name)
                    .to_string_lossy()
                    .into_owned(),
                kind: entry.kind.clone(),
                size: entry.size,
                modified_ms: entry.modified_ms,
            })
            .collect())
    }

    pub(crate) fn remove_bundle(&self, item_id: Uuid) -> Result<(), CoreError> {
        let cache_dir = self.root.join(item_id.to_string());
        if cache_dir.exists() {
            fs::remove_dir_all(cache_dir)?;
        }
        let staging_dir = self.staging.join(item_id.to_string());
        if staging_dir.exists() {
            fs::remove_dir_all(staging_dir)?;
        }
        Ok(())
    }

    fn read_manifest(&self, item_id: Uuid, cache_dir: &Path) -> Result<CacheManifest, CoreError> {
        let sealed = fs::read(cache_dir.join("manifest.clipobj"))?;
        let bytes = self
            .object_cipher(&manifest_aad(self.vault_id, item_id))?
            .open(&sealed, &manifest_aad(self.vault_id, item_id))?;
        let manifest: CacheManifest = serde_json::from_slice(&bytes)?;
        if manifest.version != CACHE_FORMAT_VERSION {
            return Err(CoreError::InvalidCommand(
                "unsupported file cache version".into(),
            ));
        }
        Ok(manifest)
    }

    fn cleanup_staging(&self, max_age_ms: i64) -> Result<(), CoreError> {
        cleanup_staging_dir(&self.staging, max_age_ms)
    }

    fn start_staging_cleanup(&self) {
        let staging = self.staging.clone();
        let _ = std::thread::Builder::new()
            .name("clipboard-staging-cleanup".into())
            .spawn(move || {
                loop {
                    std::thread::sleep(Duration::from_millis(STAGING_MAX_AGE_MS as u64));
                    let _ = cleanup_staging_dir(&staging, STAGING_MAX_AGE_MS);
                }
            });
    }

    fn cleanup_pending(&self) -> Result<(), CoreError> {
        match fs::symlink_metadata(&self.pending) {
            Ok(metadata) => {
                reject_reparse(&self.pending, &metadata)?;
                if !metadata.is_dir() {
                    return Err(CoreError::InvalidCommand(
                        "invalid file cache pending directory".into(),
                    ));
                }
            }
            Err(error) if error.kind() == std::io::ErrorKind::NotFound => return Ok(()),
            Err(error) => return Err(error.into()),
        }
        for entry in fs::read_dir(&self.pending)? {
            let path = entry?.path();
            let metadata = fs::symlink_metadata(&path)?;
            reject_reparse(&path, &metadata)?;
            if metadata.is_dir() {
                fs::remove_dir_all(path)?;
            } else {
                fs::remove_file(path)?;
            }
        }
        fs::remove_dir(&self.pending)?;
        Ok(())
    }

    fn object_cipher(&self, context: &[u8]) -> Result<ObjectCipher, CoreError> {
        Ok(ObjectCipher::new(self.key.derive_scoped(context)?))
    }
}

fn cleanup_staging_dir(staging: &Path, max_age_ms: i64) -> Result<(), CoreError> {
    match fs::symlink_metadata(staging) {
        Ok(metadata) => {
            reject_reparse(staging, &metadata)?;
            if !metadata.is_dir() {
                return Err(CoreError::InvalidCommand(
                    "invalid staging directory".into(),
                ));
            }
        }
        Err(error) if error.kind() == std::io::ErrorKind::NotFound => return Ok(()),
        Err(error) => return Err(error.into()),
    }
    let cutoff = now_ms().saturating_sub(max_age_ms);
    for entry in fs::read_dir(staging)? {
        let entry = entry?;
        let path = entry.path();
        let metadata = fs::symlink_metadata(&path)?;
        if is_reparse_point(&metadata) {
            continue;
        }
        let modified = metadata.modified().ok().and_then(to_ms).unwrap_or(i64::MIN);
        if modified < cutoff {
            if metadata.is_dir() {
                fs::remove_dir_all(path)?;
            } else {
                fs::remove_file(path)?;
            }
        }
    }
    Ok(())
}

fn scan_bundle(bundle: &FileBundle) -> Result<(CacheManifest, Vec<SourceFile>, u64), CoreError> {
    let mut roots = Vec::with_capacity(bundle.entries.len());
    let mut sources = Vec::new();
    let mut total_bytes = 0_u64;
    for (root_index, entry) in bundle.entries.iter().enumerate() {
        let source = PathBuf::from(&entry.path);
        let metadata = fs::symlink_metadata(&source)
            .map_err(|_| CoreError::FileSourceUnavailable(entry.path.clone()))?;
        reject_reparse(&source, &metadata)?;
        let actual_kind = if metadata.is_dir() {
            FileEntryKind::Directory
        } else if metadata.is_file() {
            FileEntryKind::File
        } else {
            return Err(CoreError::FileSourceUnavailable(entry.path.clone()));
        };
        if actual_kind != entry.kind {
            return Err(CoreError::FileSourceUnavailable(entry.path.clone()));
        }
        let root_name = source
            .file_name()
            .map(|name| name.to_string_lossy().into_owned())
            .filter(|name| !name.is_empty())
            .ok_or_else(|| CoreError::FileSourceUnavailable(entry.path.clone()))?;
        roots.push(CacheRoot {
            name: root_name,
            kind: entry.kind.clone(),
            size: entry.size,
            modified_ms: entry.modified_ms,
        });
        match entry.kind {
            FileEntryKind::File => {
                let size = metadata.len();
                total_bytes =
                    total_bytes
                        .checked_add(size)
                        .ok_or(CoreError::FileCacheTooLarge {
                            actual: u64::MAX,
                            maximum: u64::MAX,
                        })?;
                sources.push(SourceFile {
                    root: root_index,
                    relative_path: String::new(),
                    source_path: source,
                    size,
                    modified_ms: metadata.modified().ok().and_then(to_ms).unwrap_or(0),
                });
            }
            FileEntryKind::Directory => scan_directory(
                root_index,
                &source,
                Path::new(""),
                &mut sources,
                &mut total_bytes,
            )?,
        }
    }
    let files = sources
        .iter()
        .enumerate()
        .map(|(index, source)| CacheFile {
            index,
            root: source.root,
            relative_path: source.relative_path.clone(),
            size: source.size,
            modified_ms: source.modified_ms,
            chunks: source.size.div_ceil(CHUNK_SIZE as u64) as u32,
        })
        .collect();
    Ok((
        CacheManifest {
            version: CACHE_FORMAT_VERSION,
            roots,
            files,
        },
        sources,
        total_bytes,
    ))
}

fn scan_directory(
    root: usize,
    directory: &Path,
    relative: &Path,
    sources: &mut Vec<SourceFile>,
    total_bytes: &mut u64,
) -> Result<(), CoreError> {
    let mut entries = fs::read_dir(directory)?.collect::<Result<Vec<_>, _>>()?;
    entries.sort_by_key(|entry| entry.file_name());
    for entry in entries {
        let path = entry.path();
        let metadata = fs::symlink_metadata(&path)?;
        reject_reparse(&path, &metadata)?;
        let child_relative = relative.join(entry.file_name());
        if metadata.is_dir() {
            scan_directory(root, &path, &child_relative, sources, total_bytes)?;
        } else if metadata.is_file() {
            let size = metadata.len();
            *total_bytes = total_bytes
                .checked_add(size)
                .ok_or(CoreError::FileCacheTooLarge {
                    actual: u64::MAX,
                    maximum: u64::MAX,
                })?;
            sources.push(SourceFile {
                root,
                relative_path: child_relative.to_string_lossy().replace('/', "\\"),
                source_path: path,
                size,
                modified_ms: metadata.modified().ok().and_then(to_ms).unwrap_or(0),
            });
        } else {
            return Err(CoreError::FileSourceUnavailable(
                path.to_string_lossy().into_owned(),
            ));
        }
    }
    Ok(())
}

fn write_chunks(
    key: &DerivedKey,
    vault_id: Uuid,
    item_id: Uuid,
    index: usize,
    source: &SourceFile,
    pending_item: &Path,
) -> Result<(), CoreError> {
    let chunk_dir = pending_item.join(index.to_string());
    fs::create_dir_all(&chunk_dir)?;
    let mut input = open_checked_source(source)?;
    let mut buffer = vec![0_u8; CHUNK_SIZE];
    let mut chunk_index = 0_u32;
    let mut remaining = source.size;
    while remaining > 0 {
        let read = input.read(&mut buffer[..remaining.min(CHUNK_SIZE as u64) as usize])?;
        if read == 0 {
            return Err(CoreError::FileSourceUnavailable(
                source.source_path.to_string_lossy().into_owned(),
            ));
        }
        let aad = chunk_aad(
            vault_id,
            item_id,
            index as u32,
            &source.relative_path,
            chunk_index,
        );
        let sealed = ObjectCipher::new(key.derive_scoped(&aad)?).seal(&buffer[..read], &aad)?;
        let mut output = File::create(chunk_dir.join(format!("{chunk_index:08}.clipobj")))?;
        output.write_all(&sealed)?;
        output.sync_all()?;
        chunk_index = chunk_index.saturating_add(1);
        remaining -= read as u64;
    }
    let mut extra = [0_u8; 1];
    if input.read(&mut extra)? != 0 {
        return Err(CoreError::FileSourceUnavailable(
            source.source_path.to_string_lossy().into_owned(),
        ));
    }
    Ok(())
}

fn open_checked_source(source: &SourceFile) -> Result<File, CoreError> {
    let metadata = fs::symlink_metadata(&source.source_path).map_err(|_| {
        CoreError::FileSourceUnavailable(source.source_path.to_string_lossy().into_owned())
    })?;
    reject_reparse(&source.source_path, &metadata)?;
    if !metadata.is_file() || metadata.len() != source.size {
        return Err(CoreError::FileSourceUnavailable(
            source.source_path.to_string_lossy().into_owned(),
        ));
    }

    #[cfg(windows)]
    let input = {
        use std::os::windows::fs::OpenOptionsExt;
        const FILE_FLAG_OPEN_REPARSE_POINT: u32 = 0x0020_0000;
        OpenOptions::new()
            .read(true)
            .custom_flags(FILE_FLAG_OPEN_REPARSE_POINT)
            .open(&source.source_path)?
    };
    #[cfg(not(windows))]
    let input = OpenOptions::new().read(true).open(&source.source_path)?;

    let opened_metadata = input.metadata()?;
    reject_reparse(&source.source_path, &opened_metadata)?;
    if !opened_metadata.is_file() || opened_metadata.len() != source.size {
        return Err(CoreError::FileSourceUnavailable(
            source.source_path.to_string_lossy().into_owned(),
        ));
    }
    Ok(input)
}

fn restore_file(
    key: &DerivedKey,
    vault_id: Uuid,
    item_id: Uuid,
    file: &CacheFile,
    cache_dir: &Path,
    output: &Path,
) -> Result<(), CoreError> {
    let mut destination = OpenOptions::new()
        .write(true)
        .create_new(true)
        .open(output)?;
    for chunk_index in 0..file.chunks {
        let sealed = fs::read(
            cache_dir
                .join(file.index.to_string())
                .join(format!("{chunk_index:08}.clipobj")),
        )?;
        let aad = chunk_aad(
            vault_id,
            item_id,
            file.index as u32,
            &file.relative_path,
            chunk_index,
        );
        let bytes = ObjectCipher::new(key.derive_scoped(&aad)?).open(&sealed, &aad)?;
        destination.write_all(&bytes)?;
    }
    destination.sync_all()?;
    Ok(())
}

fn safe_join(root: &Path, relative: &str) -> Result<PathBuf, CoreError> {
    let path = PathBuf::from(relative);
    if path.is_absolute()
        || path
            .components()
            .any(|component| matches!(component, std::path::Component::ParentDir))
    {
        return Err(CoreError::InvalidCommand(
            "invalid file cache relative path".into(),
        ));
    }
    Ok(root.join(path))
}

fn ensure_directory(path: &Path) -> Result<(), CoreError> {
    match fs::symlink_metadata(path) {
        Ok(metadata) => {
            reject_reparse(path, &metadata)?;
            if !metadata.is_dir() {
                return Err(CoreError::InvalidCommand(
                    "invalid staging directory".into(),
                ));
            }
        }
        Err(error) if error.kind() == std::io::ErrorKind::NotFound => {
            fs::create_dir_all(path)?;
            let metadata = fs::symlink_metadata(path)?;
            reject_reparse(path, &metadata)?;
            if !metadata.is_dir() {
                return Err(CoreError::InvalidCommand(
                    "invalid staging directory".into(),
                ));
            }
        }
        Err(error) => return Err(error.into()),
    }
    Ok(())
}

fn reject_reparse(path: &Path, metadata: &fs::Metadata) -> Result<(), CoreError> {
    if is_reparse_point(metadata) {
        return Err(CoreError::FileSourceUnavailable(
            path.to_string_lossy().into_owned(),
        ));
    }
    Ok(())
}

fn is_reparse_point(metadata: &fs::Metadata) -> bool {
    #[cfg(windows)]
    {
        use std::os::windows::fs::MetadataExt;
        metadata.file_attributes() & 0x400 != 0
    }
    #[cfg(not(windows))]
    {
        metadata.file_type().is_symlink()
    }
}

fn manifest_aad(vault_id: Uuid, item_id: Uuid) -> Vec<u8> {
    aad(vault_id, item_id, b"manifest", 0, b"", 0)
}

fn chunk_aad(
    vault_id: Uuid,
    item_id: Uuid,
    file_index: u32,
    relative_path: &str,
    chunk_index: u32,
) -> Vec<u8> {
    aad(
        vault_id,
        item_id,
        b"chunk",
        file_index,
        relative_path.as_bytes(),
        chunk_index,
    )
}

fn aad(
    vault_id: Uuid,
    item_id: Uuid,
    kind: &[u8],
    file_index: u32,
    relative_path: &[u8],
    chunk_index: u32,
) -> Vec<u8> {
    let mut bytes = Vec::with_capacity(64 + relative_path.len());
    bytes.extend_from_slice(b"clipboard-file-cache\0");
    bytes.push(CACHE_FORMAT_VERSION);
    bytes.extend_from_slice(vault_id.as_bytes());
    bytes.extend_from_slice(item_id.as_bytes());
    bytes.extend_from_slice(kind);
    bytes.extend_from_slice(&file_index.to_le_bytes());
    bytes.extend_from_slice(&(relative_path.len() as u32).to_le_bytes());
    bytes.extend_from_slice(relative_path);
    bytes.extend_from_slice(&chunk_index.to_le_bytes());
    bytes
}

fn now_ms() -> i64 {
    SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .ok()
        .and_then(|duration| i64::try_from(duration.as_millis()).ok())
        .unwrap_or(0)
}

fn to_ms(time: SystemTime) -> Option<i64> {
    time.duration_since(UNIX_EPOCH)
        .ok()
        .and_then(|duration| i64::try_from(duration.as_millis()).ok())
}
