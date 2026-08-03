#![forbid(unsafe_code)]

//! Shared clipboard domain types.

mod file_bundle;
mod hlc;
mod item;
mod retention;
mod state;

pub use file_bundle::{FileBundle, FileBundleError, FileEntry, FileEntryKind};
pub use hlc::Hlc;
pub use item::{ClipboardContent, ClipboardItem, SyncScope};
pub use retention::{RetentionCandidate, RetentionPlan, RetentionPolicy, plan_retention};
pub use state::{DeleteState, FavoriteState};

pub const CRATE_READY: bool = true;
