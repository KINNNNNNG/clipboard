use thiserror::Error;

#[derive(Debug, Error, PartialEq, Eq)]
pub enum SyncError {
    #[error("invalid recovery code")]
    InvalidRecoveryCode,
    #[error("unsupported recovery code version")]
    UnsupportedRecoveryCodeVersion(u8),
    #[error("invalid recovery code checksum")]
    InvalidRecoveryCodeChecksum,
    #[error("unsupported sync protocol version")]
    UnsupportedProtocolVersion(u8),
    #[error("local-only clipboard item rejected")]
    LocalOnlyRejected,
    #[error("encrypted sync segment is invalid")]
    InvalidSegment,
    #[error("sync transport failed")]
    Transport,
    #[error("remote authentication failed")]
    Authentication,
    #[error("remote sync conflict")]
    Conflict,
    #[error("remote sync rate limited")]
    RateLimited,
    #[error("remote sync unavailable")]
    RemoteUnavailable,
    #[error(transparent)]
    Crypto(#[from] clipboard_crypto::CryptoError),
}
