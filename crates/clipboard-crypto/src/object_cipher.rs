use crate::{CryptoError, DerivedKey};
use chacha20poly1305::{
    XChaCha20Poly1305, XNonce,
    aead::{Aead, KeyInit, Payload},
};
use rand_core::{OsRng, RngCore};

const FORMAT_VERSION: u8 = 1;
const NONCE_SIZE: usize = 24;
const TAG_SIZE: usize = 16;
const HEADER_SIZE: usize = 1 + NONCE_SIZE;

pub struct ObjectCipher {
    key: DerivedKey,
}

impl ObjectCipher {
    pub fn new(key: DerivedKey) -> Self {
        Self { key }
    }

    pub fn seal(&self, plaintext: &[u8], aad: &[u8]) -> Result<Vec<u8>, CryptoError> {
        let mut nonce_bytes = [0_u8; NONCE_SIZE];
        OsRng.fill_bytes(&mut nonce_bytes);
        let nonce = XNonce::from_slice(&nonce_bytes);
        let payload = Payload {
            msg: plaintext,
            aad,
        };
        let ciphertext = self
            .cipher()
            .encrypt(nonce, payload)
            .map_err(|_| CryptoError::Authentication)?;

        let mut sealed = Vec::with_capacity(HEADER_SIZE + ciphertext.len());
        sealed.push(FORMAT_VERSION);
        sealed.extend_from_slice(&nonce_bytes);
        sealed.extend_from_slice(&ciphertext);
        Ok(sealed)
    }

    pub fn open(&self, sealed: &[u8], aad: &[u8]) -> Result<Vec<u8>, CryptoError> {
        if sealed.len() < HEADER_SIZE + TAG_SIZE {
            return Err(CryptoError::InvalidCiphertext);
        }
        if sealed[0] != FORMAT_VERSION {
            return Err(CryptoError::UnsupportedVersion(sealed[0]));
        }

        let nonce = XNonce::from_slice(&sealed[1..HEADER_SIZE]);
        let payload = Payload {
            msg: &sealed[HEADER_SIZE..],
            aad,
        };
        self.cipher()
            .decrypt(nonce, payload)
            .map_err(|_| CryptoError::Authentication)
    }

    fn cipher(&self) -> XChaCha20Poly1305 {
        XChaCha20Poly1305::new_from_slice(self.key.as_bytes())
            .expect("derived keys always contain 32 bytes")
    }
}
