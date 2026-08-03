#![forbid(unsafe_code)]

//! Encrypted local persistence and synchronization outbox.

mod database;
mod error;
mod item_repository;
mod outbox;

pub use database::Database;
pub use error::{OutboxError, StorageError};
pub use item_repository::ItemRepository;
pub use outbox::OutboxRepository;

pub const CRATE_READY: bool = true;
