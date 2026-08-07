use serde::{Deserialize, Serialize};
use thiserror::Error;

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum FileEntryKind {
    #[serde(alias = "File")]
    File,
    #[serde(alias = "Directory")]
    Directory,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct FileEntry {
    pub path: String,
    pub kind: FileEntryKind,
    pub size: u64,
    pub modified_ms: i64,
}

impl FileEntry {
    pub fn file(path: String, size: u64, modified_ms: i64) -> Self {
        Self {
            path,
            kind: FileEntryKind::File,
            size,
            modified_ms,
        }
    }

    pub fn directory(path: String, modified_ms: i64) -> Self {
        Self {
            path,
            kind: FileEntryKind::Directory,
            size: 0,
            modified_ms,
        }
    }
}

#[derive(Debug, Error, PartialEq, Eq)]
pub enum FileBundleError {
    #[error("file bundle cannot be empty")]
    Empty,
    #[error("file bundle path cannot be blank")]
    BlankPath,
    #[error("file bundle path must be an absolute Windows path: {0}")]
    RelativePath(String),
    #[error("file bundle contains duplicate path: {0}")]
    DuplicatePath(String),
    #[error("file bundle path is invalid: {0}")]
    InvalidPath(String),
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct FileBundle {
    pub entries: Vec<FileEntry>,
    #[serde(default)]
    pub cache: Option<FileBundleCache>,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct FileBundleCache {
    pub total_bytes: u64,
    pub cached_at_ms: i64,
}

impl FileBundle {
    pub fn new(mut entries: Vec<FileEntry>) -> Result<Self, FileBundleError> {
        if entries.is_empty() {
            return Err(FileBundleError::Empty);
        }

        let mut paths = std::collections::HashSet::with_capacity(entries.len());
        for entry in &mut entries {
            let path = normalize_windows_path(&entry.path)?;
            if !paths.insert(path.clone()) {
                return Err(FileBundleError::DuplicatePath(path));
            }
            entry.path = path;
        }

        Ok(Self {
            entries,
            cache: None,
        })
    }
}

pub fn normalize_windows_path(path: &str) -> Result<String, FileBundleError> {
    if path.trim().is_empty() {
        return Err(FileBundleError::BlankPath);
    }

    let normalized = path.replace('/', "\\").to_ascii_lowercase();
    if is_device_namespace(&normalized) {
        return Err(FileBundleError::InvalidPath(path.into()));
    }
    if matches!(normalized.as_bytes(), [drive, b':', b'\\', ..] if drive.is_ascii_alphabetic()) {
        return normalize_from_root(&normalized[..3], &normalized[3..], path, false);
    }
    if let Some(unc_path) = normalized.strip_prefix("\\\\") {
        return normalize_unc_path(unc_path, path);
    }
    Err(FileBundleError::RelativePath(path.into()))
}

fn is_device_namespace(path: &str) -> bool {
    path.starts_with("\\\\?\\") || path.starts_with("\\\\.\\") || path.starts_with("\\\\??\\")
}

fn normalize_unc_path(path: &str, original: &str) -> Result<String, FileBundleError> {
    let components = path
        .split('\\')
        .filter(|component| !component.is_empty())
        .collect::<Vec<_>>();
    let Some((server, remaining)) = components.split_first() else {
        return Err(FileBundleError::InvalidPath(original.into()));
    };
    let Some((share, remaining)) = remaining.split_first() else {
        return Err(FileBundleError::InvalidPath(original.into()));
    };
    if matches!(*server, "." | "..") || matches!(*share, "." | "..") {
        return Err(FileBundleError::InvalidPath(original.into()));
    }
    normalize_from_root(
        &format!("\\\\{server}\\{share}"),
        &remaining.join("\\"),
        original,
        true,
    )
}

fn normalize_from_root(
    root: &str,
    suffix: &str,
    original: &str,
    unc: bool,
) -> Result<String, FileBundleError> {
    let mut components = Vec::new();
    for component in suffix.split('\\').filter(|component| !component.is_empty()) {
        match component {
            "." => {}
            ".." => {
                if components.pop().is_none() {
                    return Err(FileBundleError::InvalidPath(original.into()));
                }
            }
            _ => components.push(component),
        }
    }
    if components.is_empty() {
        return Ok(root.into());
    }
    if unc {
        Ok(format!("{root}\\{}", components.join("\\")))
    } else {
        Ok(format!("{root}{}", components.join("\\")))
    }
}
