use crate::CryptoError;
use hkdf::Hkdf;
use sha2::Sha256;
use uuid::Uuid;
use zeroize::{Zeroize, ZeroizeOnDrop};

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
            Self::Database => b"database-v1",
            Self::Journal => b"journal-v1",
            Self::Image => b"image-v1",
            Self::FileCache => b"file-cache-v1",
            Self::Fingerprint => b"fingerprint-v1",
        }
    }
}

#[derive(Zeroize, ZeroizeOnDrop)]
pub struct VaultKey([u8; 32]);

impl VaultKey {
    pub fn from_bytes(bytes: [u8; 32]) -> Self {
        Self(bytes)
    }

    pub fn derive(&self, vault_id: Uuid, purpose: KeyPurpose) -> Result<DerivedKey, CryptoError> {
        let hkdf = Hkdf::<Sha256>::new(Some(vault_id.as_bytes()), &self.0);
        let mut output = [0_u8; 32];
        hkdf.expand(purpose.label(), &mut output)
            .map_err(|_| CryptoError::KeyDerivation)?;
        Ok(DerivedKey(output))
    }
}

#[derive(Zeroize, ZeroizeOnDrop)]
pub struct DerivedKey([u8; 32]);

impl DerivedKey {
    pub(crate) fn as_bytes(&self) -> &[u8; 32] {
        &self.0
    }
}
