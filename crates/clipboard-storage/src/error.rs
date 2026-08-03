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
    #[error("clipboard content fingerprint must be exactly 32 bytes")]
    InvalidFingerprintLength,
    #[error("clipboard item was not found: {0}")]
    ItemNotFound(uuid::Uuid),
    #[error("database schema version {found} is newer than supported version {supported}")]
    UnsupportedSchemaVersion { found: i64, supported: i64 },
    #[error("database migration history is incomplete or out of order")]
    InvalidMigrationHistory,
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
