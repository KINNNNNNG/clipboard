#![forbid(unsafe_code)]

//! Shared clipboard domain types.

mod file_bundle;
mod item;

pub use file_bundle::{FileBundle, FileBundleError, FileEntry, FileEntryKind};
pub use item::{ClipboardContent, ClipboardItem, SyncScope};

pub const CRATE_READY: bool = true;
