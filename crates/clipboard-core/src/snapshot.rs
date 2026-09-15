//! Verified local snapshots that keep clipboard history recoverable.

use clipboard_storage::{Database, HISTORY_FILE_NAME, VAULT_MARKER_FILE_NAME};
use serde::{Deserialize, Serialize};
use sha2::{Digest, Sha256};
use std::fs::{self, File};
use std::io::{Read, Write};
use std::path::{Path, PathBuf};
use thiserror::Error;
use uuid::Uuid;

/// Layout version of a snapshot directory. Bump when the layout changes.
pub const SNAPSHOT_FORMAT_VERSION: u8 = 1;
/// Prefix of every snapshot directory.
pub const SNAPSHOT_DIRECTORY_PREFIX: &str = "snapshot-";
/// Manifest file written last, after verification succeeds.
pub const SNAPSHOT_MANIFEST_FILE_NAME: &str = "manifest.json";
/// Directory that holds encrypted image and file cache objects.
pub const OBJECTS_DIRECTORY_NAME: &str = "objects";
/// Default number of snapshots to keep.
pub const DEFAULT_SNAPSHOT_KEEP: usize = 3;
/// Upper bound for the retained snapshot count.
pub const MAX_SNAPSHOT_KEEP: usize = 10;

#[derive(Debug, Error)]
pub enum SnapshotError {
    #[error("snapshot is invalid: {0}")]
    Invalid(String),
    #[error("the live history database is not readable")]
    DatabaseUnreadable,
    #[error("no verified snapshot is available in {0}")]
    NoSnapshot(String),
    #[error(transparent)]
    Storage(#[from] clipboard_storage::StorageError),
    #[error(transparent)]
    Io(#[from] std::io::Error),
    #[error(transparent)]
    Serialization(#[from] serde_json::Error),
}

/// Recorded content of one file inside a snapshot.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct SnapshotEntry {
    pub path: String,
    pub size: u64,
    pub sha256: String,
}

/// Written only after the snapshot passed verification.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct SnapshotManifest {
    pub version: u8,
    pub created_ms: i64,
    pub entries: Vec<SnapshotEntry>,
}

/// Identifies one verified snapshot.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct SnapshotSummary {
    pub directory: PathBuf,
    pub created_ms: i64,
    pub file_count: usize,
    pub total_bytes: u64,
}

/// Normalizes a keep count into the supported range.
pub fn clamp_keep(keep: usize) -> usize {
    keep.clamp(1, MAX_SNAPSHOT_KEEP)
}

const TIMESTAMP_FORMAT: &[time::format_description::FormatItem<'static>] =
    time::macros::format_description!("[year][month][day]-[hour][minute][second]");

fn snapshot_directory_name(created_ms: i64) -> Option<String> {
    let timestamp =
        time::OffsetDateTime::from_unix_timestamp_nanos(i128::from(created_ms) * 1_000_000).ok()?;
    Some(format!(
        "{SNAPSHOT_DIRECTORY_PREFIX}{}",
        timestamp.format(TIMESTAMP_FORMAT).ok()?
    ))
}

fn hash_file(path: &Path) -> Result<(u64, String), SnapshotError> {
    let mut file = File::open(path)?;
    let mut hasher = Sha256::new();
    let mut buffer = [0u8; 64 * 1024];
    let mut size = 0u64;
    loop {
        let read = file.read(&mut buffer)?;
        if read == 0 {
            break;
        }
        size += read as u64;
        hasher.update(&buffer[..read]);
    }
    Ok((size, hex::encode(hasher.finalize())))
}

/// Copies a file, preferring a hard link so same-volume snapshots stay small.
fn link_or_copy(source: &Path, destination: &Path) -> Result<(), SnapshotError> {
    if let Some(parent) = destination.parent() {
        fs::create_dir_all(parent)?;
    }
    if fs::hard_link(source, destination).is_ok() {
        return Ok(());
    }
    fs::copy(source, destination)?;
    Ok(())
}

/// Collects every file under `root`, skipping SQLite scratch files.
fn collect_files(root: &Path) -> Result<Vec<PathBuf>, SnapshotError> {
    let mut files = Vec::new();
    if !root.exists() {
        return Ok(files);
    }
    let mut stack = vec![root.to_path_buf()];
    while let Some(current) = stack.pop() {
        for entry in fs::read_dir(&current)? {
            let entry = entry?;
            if entry.file_name() == ".pending" {
                continue;
            }
            if entry.metadata()?.is_dir() {
                stack.push(entry.path());
            } else {
                files.push(entry.path());
            }
        }
    }
    files.sort();
    Ok(files)
}

/// Moves a damaged database and its sidecars aside, keeping them for manual recovery.
fn quarantine_database(data_dir: &Path, created_ms: i64) -> Result<(), SnapshotError> {
    let stamp = snapshot_directory_name(created_ms)
        .map(|name| {
            name.trim_start_matches(SNAPSHOT_DIRECTORY_PREFIX)
                .to_owned()
        })
        .ok_or_else(|| SnapshotError::Invalid("timestamp".to_owned()))?;
    for suffix in ["", "-wal", "-shm"] {
        let source = data_dir.join(format!("{HISTORY_FILE_NAME}{suffix}"));
        if !source.exists() {
            continue;
        }
        let mut index = 0;
        let destination = loop {
            let candidate = if index == 0 {
                data_dir.join(format!("{HISTORY_FILE_NAME}.corrupt-{stamp}{suffix}"))
            } else {
                data_dir.join(format!(
                    "{HISTORY_FILE_NAME}.corrupt-{stamp}-{index}{suffix}"
                ))
            };
            if !candidate.exists() {
                break candidate;
            }
            index += 1;
        };
        fs::rename(source, destination)?;
    }
    Ok(())
}

fn summary(directory: &Path, manifest: &SnapshotManifest) -> SnapshotSummary {
    SnapshotSummary {
        directory: directory.to_path_buf(),
        created_ms: manifest.created_ms,
        file_count: manifest.entries.len(),
        total_bytes: manifest.entries.iter().map(|entry| entry.size).sum(),
    }
}

/// Writes a consistent, verified snapshot of the live database and object store.
pub fn create(
    database: &Database,
    data_dir: &Path,
    vault_id: Uuid,
    key: &[u8; 32],
    target_root: &Path,
    created_ms: i64,
    keep: usize,
) -> Result<SnapshotSummary, SnapshotError> {
    // Probe a fresh connection instead of the live handle: the guard must describe what is on
    // disk, and the live handle may still answer from its page cache.
    let probe = Database::open_vault(data_dir, vault_id, key)
        .map_err(|_| SnapshotError::DatabaseUnreadable)?;
    probe
        .verify_integrity()
        .map_err(|_| SnapshotError::DatabaseUnreadable)?;
    drop(probe);

    let directory_name = snapshot_directory_name(created_ms)
        .ok_or_else(|| SnapshotError::Invalid("timestamp".to_owned()))?;
    let directory = target_root.join(directory_name);
    fs::create_dir_all(&directory)?;
    database.backup_to(&directory.join(HISTORY_FILE_NAME))?;

    let marker = data_dir.join(VAULT_MARKER_FILE_NAME);
    if marker.exists() {
        fs::copy(&marker, directory.join(VAULT_MARKER_FILE_NAME))?;
    }

    let objects_root = data_dir.join(OBJECTS_DIRECTORY_NAME);
    let mut entries = Vec::new();
    for source in collect_files(&objects_root)? {
        let relative = source
            .strip_prefix(&objects_root)
            .map_err(|error| SnapshotError::Invalid(error.to_string()))?;
        let destination = directory.join(OBJECTS_DIRECTORY_NAME).join(relative);
        link_or_copy(&source, &destination)?;
        let (size, sha256) = hash_file(&destination)?;
        entries.push(SnapshotEntry {
            path: format!(
                "{OBJECTS_DIRECTORY_NAME}/{}",
                relative.to_string_lossy().replace('\\', "/")
            ),
            size,
            sha256,
        });
    }

    let manifest = SnapshotManifest {
        version: SNAPSHOT_FORMAT_VERSION,
        created_ms,
        entries,
    };
    verify_contents(&directory, vault_id, key, &manifest)?;
    let mut manifest_file = File::create(directory.join(SNAPSHOT_MANIFEST_FILE_NAME))?;
    manifest_file.write_all(&serde_json::to_vec_pretty(&manifest)?)?;
    manifest_file.flush()?;
    drop(manifest_file);

    prune(target_root, keep)?;
    Ok(summary(&directory, &manifest))
}

/// Checks that the snapshot database opens with the vault key and that objects match the manifest.
fn verify_contents(
    directory: &Path,
    vault_id: Uuid,
    key: &[u8; 32],
    manifest: &SnapshotManifest,
) -> Result<(), SnapshotError> {
    if manifest.version != SNAPSHOT_FORMAT_VERSION {
        return Err(SnapshotError::Invalid(format!(
            "unsupported snapshot version {}",
            manifest.version
        )));
    }
    let database = Database::open_vault(directory, vault_id, key)?;
    database.verify_integrity()?;
    drop(database);

    for entry in &manifest.entries {
        let path = directory.join(entry.path.replace('/', std::path::MAIN_SEPARATOR_STR));
        if !path.exists() {
            return Err(SnapshotError::Invalid(format!(
                "snapshot is missing {}",
                entry.path
            )));
        }
        let (size, sha256) = hash_file(&path)?;
        if size != entry.size || sha256 != entry.sha256 {
            return Err(SnapshotError::Invalid(format!(
                "snapshot entry {} does not match the manifest",
                entry.path
            )));
        }
    }
    Ok(())
}

/// Verifies a snapshot directory and returns its manifest.
pub fn verify(
    snapshot_dir: &Path,
    vault_id: Uuid,
    key: &[u8; 32],
) -> Result<SnapshotManifest, SnapshotError> {
    let manifest_path = snapshot_dir.join(SNAPSHOT_MANIFEST_FILE_NAME);
    if !manifest_path.exists() {
        return Err(SnapshotError::Invalid(format!(
            "{} has no manifest",
            snapshot_dir.display()
        )));
    }
    let manifest: SnapshotManifest = serde_json::from_slice(&fs::read(&manifest_path)?)?;
    verify_contents(snapshot_dir, vault_id, key, &manifest)?;
    Ok(manifest)
}

/// Lists verified snapshots, newest first. Unverified directories are skipped.
pub fn list(
    root: &Path,
    vault_id: Uuid,
    key: &[u8; 32],
) -> Result<Vec<SnapshotSummary>, SnapshotError> {
    let mut snapshots = Vec::new();
    if !root.exists() {
        return Ok(snapshots);
    }
    for entry in fs::read_dir(root)? {
        let entry = entry?;
        if !entry.metadata()?.is_dir()
            || !entry
                .file_name()
                .to_string_lossy()
                .starts_with(SNAPSHOT_DIRECTORY_PREFIX)
        {
            continue;
        }
        if let Ok(manifest) = verify(&entry.path(), vault_id, key) {
            snapshots.push(summary(&entry.path(), &manifest));
        }
    }
    snapshots.sort_by(|left, right| right.created_ms.cmp(&left.created_ms));
    Ok(snapshots)
}

/// Restores a verified snapshot over the live data directory, quarantining what is there.
pub fn restore(
    snapshot_dir: &Path,
    data_dir: &Path,
    vault_id: Uuid,
    key: &[u8; 32],
    created_ms: i64,
) -> Result<SnapshotSummary, SnapshotError> {
    let manifest = verify(snapshot_dir, vault_id, key)?;
    fs::create_dir_all(data_dir)?;
    quarantine_database(data_dir, created_ms)?;
    fs::copy(
        snapshot_dir.join(HISTORY_FILE_NAME),
        data_dir.join(HISTORY_FILE_NAME),
    )?;

    let marker = snapshot_dir.join(VAULT_MARKER_FILE_NAME);
    if marker.exists() {
        fs::copy(marker, data_dir.join(VAULT_MARKER_FILE_NAME))?;
    }

    let objects_source = snapshot_dir.join(OBJECTS_DIRECTORY_NAME);
    for source in collect_files(&objects_source)? {
        let relative = source
            .strip_prefix(&objects_source)
            .map_err(|error| SnapshotError::Invalid(error.to_string()))?;
        let destination = data_dir.join(OBJECTS_DIRECTORY_NAME).join(relative);
        if destination.exists() {
            continue;
        }
        link_or_copy(&source, &destination)?;
    }

    Ok(summary(snapshot_dir, &manifest))
}

/// Removes incomplete directories and the oldest verified snapshots beyond `keep`.
pub fn prune(root: &Path, keep: usize) -> Result<usize, SnapshotError> {
    let keep = clamp_keep(keep);
    if !root.exists() {
        return Ok(0);
    }
    let mut verified = Vec::new();
    let mut removed = 0usize;
    for entry in fs::read_dir(root)? {
        let entry = entry?;
        if !entry.metadata()?.is_dir()
            || !entry
                .file_name()
                .to_string_lossy()
                .starts_with(SNAPSHOT_DIRECTORY_PREFIX)
        {
            continue;
        }
        if entry.path().join(SNAPSHOT_MANIFEST_FILE_NAME).exists() {
            verified.push((
                entry.file_name().to_string_lossy().into_owned(),
                entry.path(),
            ));
        } else {
            fs::remove_dir_all(entry.path())?;
            removed += 1;
        }
    }

    verified.sort_by(|left, right| right.0.cmp(&left.0));
    for (_, path) in verified.into_iter().skip(keep) {
        fs::remove_dir_all(&path)?;
        removed += 1;
    }
    Ok(removed)
}
