use crate::CryptoError;
use hkdf::Hkdf;
use sha2::Sha256;
use zeroize::{Zeroize, ZeroizeOnDrop};

const KEY_CONTEXT: &[u8] = b"clipboard-vault-key-v2";

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum KeyPurpose {
    Database,
    Journal,
    Image,
    FileCache,
    Fingerprint,
}

impl KeyPurpose {
    const fn label(self) -> &'static [u8] {
        match self {
            Self::Database => b"database-v2",
            Self::Journal => b"journal-v2",
            Self::Image => b"image-v2",
            Self::FileCache => b"file-cache-v2",
            Self::Fingerprint => b"fingerprint-v2",
        }
    }
}

#[derive(Zeroize, ZeroizeOnDrop)]
pub struct VaultKey([u8; 32]);

impl VaultKey {
    pub fn from_bytes(bytes: [u8; 32]) -> Self {
        Self(bytes)
    }

    pub fn derive(&self, purpose: KeyPurpose) -> Result<DerivedKey, CryptoError> {
        let hkdf = Hkdf::<Sha256>::new(Some(KEY_CONTEXT), &self.0);
        let mut output = [0_u8; 32];
        hkdf.expand(purpose.label(), &mut output)
            .map_err(|_| CryptoError::KeyDerivation)?;
        Ok(DerivedKey(output))
    }
}

#[derive(Zeroize, ZeroizeOnDrop)]
pub struct DerivedKey([u8; 32]);

impl DerivedKey {
    pub fn derive_scoped(&self, context: &[u8]) -> Result<Self, CryptoError> {
        let hkdf = Hkdf::<Sha256>::new(Some(b"clipboard-scoped-key-v2"), &self.0);
        let mut output = [0_u8; 32];
        hkdf.expand(context, &mut output)
            .map_err(|_| CryptoError::KeyDerivation)?;
        Ok(Self(output))
    }

    pub fn keyed_hash(&self, content: &[u8]) -> [u8; 32] {
        *blake3::keyed_hash(&self.0, content).as_bytes()
    }

    pub(crate) fn as_bytes(&self) -> &[u8; 32] {
        &self.0
    }
}
