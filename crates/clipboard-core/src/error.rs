use thiserror::Error;

#[derive(Debug, Error)]
pub enum CoreError {
    #[error("unsupported API version: {0}")]
    UnsupportedApiVersion(u32),
    #[error(transparent)]
    Storage(#[from] clipboard_storage::StorageError),
    #[error(transparent)]
    Outbox(#[from] clipboard_storage::OutboxError),
    #[error(transparent)]
    Search(#[from] clipboard_search::SearchError),
    #[error(transparent)]
    Crypto(#[from] clipboard_crypto::CryptoError),
    #[error(transparent)]
    Sync(#[from] clipboard_sync::SyncError),
    #[error(transparent)]
    FileBundle(#[from] clipboard_domain::FileBundleError),
    #[error(transparent)]
    Io(#[from] std::io::Error),
    #[error(transparent)]
    Serialization(#[from] serde_json::Error),
    #[error("clipboard item was not found: {0}")]
    ItemNotFound(uuid::Uuid),
    #[error("clipboard item is not an image: {0}")]
    NotImage(uuid::Uuid),
    #[error("clipboard item is not a file bundle: {0}")]
    NotFileBundle(uuid::Uuid),
    #[error("file bundle cache is not available: {0}")]
    FileCacheMissing(uuid::Uuid),
    #[error("file bundle cache exceeds the configured limit: {actual} > {maximum}")]
    FileCacheTooLarge { actual: u64, maximum: u64 },
    #[error("file bundle source is unavailable: {0}")]
    FileSourceUnavailable(String),
    #[error("image payload is empty or its dimensions are invalid")]
    InvalidImage,
    #[error("image payload is {actual} bytes; maximum is {maximum} bytes")]
    ImageTooLarge { actual: usize, maximum: usize },
    #[error("invalid command: {0}")]
    InvalidCommand(String),
    #[error("update check failed: {0}")]
    UpdateCheck(crate::update::UpdateError),
    #[error("update download failed: {0}")]
    UpdateDownload(crate::update::UpdateError),
}
