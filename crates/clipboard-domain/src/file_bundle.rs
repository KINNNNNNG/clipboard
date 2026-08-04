use serde::{Deserialize, Serialize};
use thiserror::Error;

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum FileEntryKind {
    File,
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
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct FileBundle {
    pub entries: Vec<FileEntry>,
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

        Ok(Self { entries })
    }
}

pub fn normalize_windows_path(path: &str) -> Result<String, FileBundleError> {
    if path.trim().is_empty() {
        return Err(FileBundleError::BlankPath);
    }

    let mut normalized = path.replace('/', "\\").to_ascii_lowercase();
    if !is_absolute_windows_path(&normalized) {
        return Err(FileBundleError::RelativePath(path.into()));
    }

    let root_len = if normalized.as_bytes().get(1) == Some(&b':') {
        3
    } else {
        2
    };
    while normalized.len() > root_len && normalized.ends_with('\\') {
        normalized.pop();
    }
    Ok(normalized)
}

fn is_absolute_windows_path(path: &str) -> bool {
    matches!(path.as_bytes(), [drive, b':', b'\\', ..] if drive.is_ascii_alphabetic())
        || path.starts_with("\\\\")
}
