#![forbid(unsafe_code)]

//! Encrypted local persistence and synchronization outbox.

mod database;
mod error;
mod item_repository;
mod outbox;
mod vault_marker;

pub use database::Database;
pub use database::HISTORY_FILE_NAME;
pub use error::{OutboxError, StorageError};
pub use item_repository::{CleanupResult, ItemRepository};
pub use outbox::{OutboxEntry, OutboxRepository};
pub use vault_marker::VAULT_MARKER_FILE_NAME;

pub const CRATE_READY: bool = true;
