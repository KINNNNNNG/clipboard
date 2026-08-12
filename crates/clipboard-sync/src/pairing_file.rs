use argon2::Argon2;
use base64::{Engine as _, engine::general_purpose::URL_SAFE_NO_PAD};
use chacha20poly1305::{
    XChaCha20Poly1305, XNonce,
    aead::{Aead, KeyInit},
};
use rand_core::{OsRng, RngCore};
use serde::{Deserialize, Serialize};
use uuid::Uuid;
use zeroize::{Zeroize, Zeroizing};

use crate::SyncError;

const PAIRING_VERSION: u8 = 1;
const SALT_LENGTH: usize = 16;
const NONCE_LENGTH: usize = 24;

#[derive(Clone, PartialEq, Eq)]
pub struct PairingFileMaterial {
    pub vault_id: Uuid,
    pub master_key: [u8; 32],
    pub provider: String,
    pub endpoint: String,
    pub root_or_prefix: Option<String>,
    pub bucket: Option<String>,
    pub region: Option<String>,
}

impl std::fmt::Debug for PairingFileMaterial {
    fn fmt(&self, formatter: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        formatter
            .debug_struct("PairingFileMaterial")
            .field("vault_id", &self.vault_id)
            .field("master_key", &"[REDACTED]")
            .field("provider", &self.provider)
            .field("endpoint", &"[REDACTED]")
            .field(
                "root_or_prefix",
                &self.root_or_prefix.as_ref().map(|_| "[REDACTED]"),
            )
            .field("bucket", &self.bucket.as_ref().map(|_| "[REDACTED]"))
            .field("region", &self.region.as_ref().map(|_| "[REDACTED]"))
            .finish()
    }
}

impl Drop for PairingFileMaterial {
    fn drop(&mut self) {
        self.master_key.zeroize();
    }
}

impl PairingFileMaterial {
    pub fn new(
        vault_id: Uuid,
        master_key: [u8; 32],
        provider: String,
        endpoint: String,
        root_or_prefix: Option<String>,
    ) -> Self {
        Self {
            vault_id,
            master_key,
            provider,
            endpoint,
            root_or_prefix,
            bucket: None,
            region: None,
        }
    }

    pub fn with_oss_configuration(mut self, bucket: String, region: String) -> Self {
        self.bucket = Some(bucket);
        self.region = Some(region);
        self
    }
}

#[derive(Serialize, Deserialize)]
struct PairingFile {
    version: u8,
    salt: String,
    nonce: String,
    ciphertext: String,
}

#[derive(Serialize, Deserialize)]
struct PairingPayload {
    vault_id: Uuid,
    master_key: [u8; 32],
    provider: String,
    endpoint: String,
    root_or_prefix: Option<String>,
    #[serde(default)]
    bucket: Option<String>,
    #[serde(default)]
    region: Option<String>,
}

pub fn encode_pairing_file(
    material: &PairingFileMaterial,
    password: &str,
) -> Result<Vec<u8>, SyncError> {
    validate_material(material, password)?;
    let mut salt = [0_u8; SALT_LENGTH];
    OsRng.fill_bytes(&mut salt);
    let mut nonce = [0_u8; NONCE_LENGTH];
    OsRng.fill_bytes(&mut nonce);
    let key = derive_pairing_key(password, &salt)?;
    let payload = Zeroizing::new(
        serde_json::to_vec(&PairingPayload {
            vault_id: material.vault_id,
            master_key: material.master_key,
            provider: material.provider.clone(),
            endpoint: material.endpoint.clone(),
            root_or_prefix: material.root_or_prefix.clone(),
            bucket: material.bucket.clone(),
            region: material.region.clone(),
        })
        .map_err(|_| SyncError::InvalidRecoveryCode)?,
    );
    let cipher =
        XChaCha20Poly1305::new_from_slice(&*key).map_err(|_| SyncError::InvalidRecoveryCode)?;
    let ciphertext = cipher
        .encrypt(XNonce::from_slice(&nonce), payload.as_ref())
        .map_err(|_| SyncError::InvalidRecoveryCode)?;
    serde_json::to_vec(&PairingFile {
        version: PAIRING_VERSION,
        salt: URL_SAFE_NO_PAD.encode(salt),
        nonce: URL_SAFE_NO_PAD.encode(nonce),
        ciphertext: URL_SAFE_NO_PAD.encode(ciphertext),
    })
    .map_err(|_| SyncError::InvalidRecoveryCode)
}

pub fn decode_pairing_file(
    encoded: &[u8],
    password: &str,
) -> Result<PairingFileMaterial, SyncError> {
    if password.is_empty() {
        return Err(SyncError::InvalidRecoveryCode);
    }
    let file: PairingFile =
        serde_json::from_slice(encoded).map_err(|_| SyncError::InvalidRecoveryCode)?;
    if file.version != PAIRING_VERSION {
        return Err(SyncError::UnsupportedRecoveryCodeVersion(file.version));
    }
    let salt = URL_SAFE_NO_PAD
        .decode(file.salt)
        .map_err(|_| SyncError::InvalidRecoveryCode)?;
    let nonce = URL_SAFE_NO_PAD
        .decode(file.nonce)
        .map_err(|_| SyncError::InvalidRecoveryCode)?;
    let ciphertext = URL_SAFE_NO_PAD
        .decode(file.ciphertext)
        .map_err(|_| SyncError::InvalidRecoveryCode)?;
    if salt.len() != SALT_LENGTH || nonce.len() != NONCE_LENGTH {
        return Err(SyncError::InvalidRecoveryCode);
    }
    let key = derive_pairing_key(password, &salt)?;
    let cipher =
        XChaCha20Poly1305::new_from_slice(&*key).map_err(|_| SyncError::InvalidRecoveryCode)?;
    let plaintext = Zeroizing::new(
        cipher
            .decrypt(XNonce::from_slice(&nonce), ciphertext.as_ref())
            .map_err(|_| SyncError::InvalidRecoveryCode)?,
    );
    let payload: PairingPayload =
        serde_json::from_slice(&plaintext).map_err(|_| SyncError::InvalidRecoveryCode)?;
    let material = PairingFileMaterial::new(
        payload.vault_id,
        payload.master_key,
        payload.provider,
        payload.endpoint,
        payload.root_or_prefix,
    )
    .with_optional_oss_configuration(payload.bucket, payload.region);
    validate_material(&material, password)?;
    Ok(material)
}

trait PairingMaterialExtensions {
    fn with_optional_oss_configuration(
        self,
        bucket: Option<String>,
        region: Option<String>,
    ) -> Self;
}

impl PairingMaterialExtensions for PairingFileMaterial {
    fn with_optional_oss_configuration(
        mut self,
        bucket: Option<String>,
        region: Option<String>,
    ) -> Self {
        self.bucket = bucket;
        self.region = region;
        self
    }
}

fn derive_pairing_key(password: &str, salt: &[u8]) -> Result<Zeroizing<[u8; 32]>, SyncError> {
    let mut key = Zeroizing::new([0_u8; 32]);
    Argon2::default()
        .hash_password_into(password.as_bytes(), salt, &mut *key)
        .map_err(|_| SyncError::InvalidRecoveryCode)?;
    Ok(key)
}

fn validate_material(material: &PairingFileMaterial, password: &str) -> Result<(), SyncError> {
    if password.is_empty()
        || material.vault_id.is_nil()
        || !matches!(material.provider.as_str(), "webdav" | "oss")
        || material.endpoint.len() > 2048
        || !material.endpoint.starts_with("https://")
        || material
            .root_or_prefix
            .as_ref()
            .is_some_and(|value| value.len() > 1024)
        || material
            .bucket
            .as_ref()
            .is_some_and(|value| value.len() > 256)
        || material
            .region
            .as_ref()
            .is_some_and(|value| value.len() > 128)
        || (material.provider == "oss"
            && (material.bucket.as_deref().is_none_or(str::is_empty)
                || material.region.as_deref().is_none_or(str::is_empty)))
        || (material.provider == "webdav"
            && (material.bucket.is_some() || material.region.is_some()))
    {
        return Err(SyncError::InvalidRecoveryCode);
    }
    Ok(())
}
