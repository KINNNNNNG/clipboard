use thiserror::Error;

#[derive(Debug, Error, PartialEq, Eq)]
pub enum CryptoError {
    #[error("key derivation failed")]
    KeyDerivation,
    #[error("ciphertext authentication failed")]
    Authentication,
    #[error("ciphertext is malformed")]
    InvalidCiphertext,
    #[error("unsupported ciphertext version: {0}")]
    UnsupportedVersion(u8),
}
