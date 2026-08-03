use thiserror::Error;

#[derive(Debug, Error)]
pub enum StorageError {
    #[error(transparent)]
    Sqlite(#[from] rusqlite::Error),
    #[error(transparent)]
    Serialization(#[from] serde_json::Error),
    #[error("SQLCipher is unavailable")]
    CipherUnavailable,
    #[error("local-only clipboard items cannot enter the sync outbox")]
    LocalOnly,
}

#[derive(Debug, Error)]
pub enum OutboxError {
    #[error("local-only clipboard items cannot enter the sync outbox")]
    LocalOnly,
    #[error(transparent)]
    Sqlite(#[from] rusqlite::Error),
    #[error(transparent)]
    Serialization(#[from] serde_json::Error),
}
