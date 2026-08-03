use serde::{Deserialize, Serialize};
use thiserror::Error;

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
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
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct FileBundle {
    pub entries: Vec<FileEntry>,
}

impl FileBundle {
    pub fn new(entries: Vec<FileEntry>) -> Result<Self, FileBundleError> {
        if entries.is_empty() {
            return Err(FileBundleError::Empty);
        }

        Ok(Self { entries })
    }
}
