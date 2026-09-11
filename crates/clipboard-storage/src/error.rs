use thiserror::Error;

#[derive(Debug, Error)]
pub enum StorageError {
    #[error(transparent)]
    Sqlite(rusqlite::Error),
    #[error(transparent)]
    Serialization(#[from] serde_json::Error),
    #[error(transparent)]
    Io(#[from] std::io::Error),
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
    #[error("clipboard history database is locked by another connection")]
    Locked,
    #[error("clipboard history database could not be decrypted")]
    Unreadable,
    #[error("clipboard history database is damaged")]
    Corrupt,
    #[error("the on-disk vault marker does not match the requested vault")]
    VaultMismatch,
    #[error("the on-disk vault marker is not a valid vault identifier")]
    VaultMarkerInvalid,
}

/// Maps SQLite and SQLCipher failures onto the categories the UI and diagnostics can act on.
///
/// `NotADatabase` is what SQLCipher reports for a wrong key and for a damaged first page alike, so
/// it stays `Unreadable` here; the vault scoped open resolves it with the on-disk vault marker.
impl From<rusqlite::Error> for StorageError {
    fn from(error: rusqlite::Error) -> Self {
        let classified = match &error {
            rusqlite::Error::SqliteFailure(code, _) => match code.code {
                rusqlite::ErrorCode::DatabaseBusy | rusqlite::ErrorCode::DatabaseLocked => {
                    Some(StorageError::Locked)
                }
                rusqlite::ErrorCode::NotADatabase => Some(StorageError::Unreadable),
                rusqlite::ErrorCode::DatabaseCorrupt => Some(StorageError::Corrupt),
                _ => None,
            },
            _ => None,
        };
        classified.unwrap_or(StorageError::Sqlite(error))
    }
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
