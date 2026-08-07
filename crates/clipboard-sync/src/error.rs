use thiserror::Error;

#[derive(Clone, Debug, Error, PartialEq, Eq)]
pub enum SyncError {
    #[error("invalid recovery code")]
    InvalidRecoveryCode,
    #[error("unsupported recovery code version")]
    UnsupportedRecoveryCodeVersion(u8),
    #[error("invalid recovery code checksum")]
    InvalidRecoveryCodeChecksum,
}
