#![forbid(unsafe_code)]

//! Local key derivation and authenticated object encryption.

mod error;
mod keys;
mod object_cipher;

pub use error::CryptoError;
pub use keys::{DerivedKey, KeyPurpose, VaultKey};
pub use object_cipher::ObjectCipher;

pub const CRATE_READY: bool = true;
