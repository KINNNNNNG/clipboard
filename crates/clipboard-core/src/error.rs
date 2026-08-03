use thiserror::Error;

#[derive(Debug, Error)]
pub enum CoreError {
    #[error("unsupported API version: {0}")]
    UnsupportedApiVersion(u32),
    #[error(transparent)]
    Storage(#[from] clipboard_storage::StorageError),
    #[error(transparent)]
    Search(#[from] clipboard_search::SearchError),
    #[error(transparent)]
    Crypto(#[from] clipboard_crypto::CryptoError),
    #[error(transparent)]
    Io(#[from] std::io::Error),
    #[error("clipboard item was not found: {0}")]
    ItemNotFound(uuid::Uuid),
    #[error("invalid command: {0}")]
    InvalidCommand(String),
}
