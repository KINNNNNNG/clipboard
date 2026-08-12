use crate::CoreError;
use clipboard_crypto::{DerivedKey, ObjectCipher};
use std::{
    fs::{self, OpenOptions},
    io::Write,
    path::{Path, PathBuf},
};
use uuid::Uuid;

const OBJECT_FORMAT_VERSION: u8 = 1;

pub(crate) struct ObjectStore {
    root: PathBuf,
    pending: PathBuf,
    vault_id: Uuid,
    image_cipher: ObjectCipher,
}

impl ObjectStore {
    pub(crate) fn open(
        data_dir: &Path,
        vault_id: Uuid,
        image_key: DerivedKey,
    ) -> Result<Self, CoreError> {
        let root = data_dir.join("objects");
        let pending = root.join(".pending");
        fs::create_dir_all(&pending)?;
        for entry in fs::read_dir(&pending)? {
            let path = entry?.path();
            if path.is_file() {
                fs::remove_file(path)?;
            }
        }
        Ok(Self {
            root,
            pending,
            vault_id,
            image_cipher: ObjectCipher::new(image_key),
        })
    }

    pub(crate) fn store_image(&self, object_id: Uuid, png: &[u8]) -> Result<(), CoreError> {
        let encrypted = self
            .image_cipher
            .seal(png, &image_aad(self.vault_id, object_id))?;
        let pending_path = self.pending.join(format!("{object_id}.tmp"));
        let final_path = self.object_path(object_id);
        let result = (|| -> Result<(), CoreError> {
            let mut file = OpenOptions::new()
                .write(true)
                .create_new(true)
                .open(&pending_path)?;
            file.write_all(&encrypted)?;
            file.sync_all()?;
            drop(file);
            fs::rename(&pending_path, &final_path)?;
            Ok(())
        })();
        if result.is_err() {
            let _ = fs::remove_file(&pending_path);
        }
        result
    }

    pub(crate) fn read_image(&self, object_id: Uuid) -> Result<Vec<u8>, CoreError> {
        let encrypted = fs::read(self.object_path(object_id))?;
        Ok(self
            .image_cipher
            .open(&encrypted, &image_aad(self.vault_id, object_id))?)
    }

    pub(crate) fn read_encrypted_image(&self, object_id: Uuid) -> Result<Vec<u8>, CoreError> {
        Ok(fs::read(self.object_path(object_id))?)
    }

    pub(crate) fn store_encrypted_image(
        &self,
        object_id: Uuid,
        encrypted: &[u8],
    ) -> Result<(), CoreError> {
        let aad = image_aad(self.vault_id, object_id);
        self.image_cipher.open(encrypted, &aad)?;

        let final_path = self.object_path(object_id);
        if final_path.exists() {
            let existing = fs::read(&final_path)?;
            self.image_cipher.open(&existing, &aad)?;
            if existing == encrypted {
                return Ok(());
            }
            return Err(CoreError::InvalidCommand(
                "image object already exists with different ciphertext".to_owned(),
            ));
        }

        let pending_path = self
            .pending
            .join(format!("{object_id}.{}.tmp", Uuid::new_v4()));
        let result = (|| -> Result<(), CoreError> {
            let mut file = OpenOptions::new()
                .write(true)
                .create_new(true)
                .open(&pending_path)?;
            file.write_all(encrypted)?;
            file.sync_all()?;
            drop(file);
            fs::rename(&pending_path, &final_path)?;
            Ok(())
        })();
        if result.is_err() {
            let _ = fs::remove_file(&pending_path);
        }
        result
    }

    pub(crate) fn remove_image(&self, object_id: Uuid) -> Result<(), CoreError> {
        match fs::remove_file(self.object_path(object_id)) {
            Ok(()) => Ok(()),
            Err(error) if error.kind() == std::io::ErrorKind::NotFound => Ok(()),
            Err(error) => Err(error.into()),
        }
    }

    fn object_path(&self, object_id: Uuid) -> PathBuf {
        self.root.join(format!("{object_id}.clipobj"))
    }
}

fn image_aad(vault_id: Uuid, object_id: Uuid) -> Vec<u8> {
    let mut aad = Vec::with_capacity(57);
    aad.extend_from_slice(b"clipboard-object\0");
    aad.extend_from_slice(vault_id.as_bytes());
    aad.extend_from_slice(object_id.as_bytes());
    aad.extend_from_slice(b"\0image\0");
    aad.push(OBJECT_FORMAT_VERSION);
    aad
}

#[cfg(test)]
mod tests {
    use super::*;
    use clipboard_crypto::{KeyPurpose, VaultKey};
    use tempfile::tempdir;

    const KEY: [u8; 32] = [0x37; 32];

    fn store() -> ObjectStore {
        let directory = tempdir().unwrap();
        let directory = Box::leak(Box::new(directory));
        let vault_id = Uuid::from_u128(41);
        let key = VaultKey::from_bytes(KEY)
            .derive(vault_id, KeyPurpose::Image)
            .unwrap();
        ObjectStore::open(directory.path(), vault_id, key).unwrap()
    }

    #[test]
    fn encrypted_image_can_be_read_without_decrypting() {
        let store = store();
        let object_id = Uuid::from_u128(42);
        let plaintext = b"image bytes";
        store.store_image(object_id, plaintext).unwrap();

        let encrypted = store.read_encrypted_image(object_id).unwrap();
        assert_ne!(encrypted, plaintext);
        assert_eq!(store.read_image(object_id).unwrap(), plaintext);
    }

    #[test]
    fn encrypted_image_with_wrong_object_id_is_rejected_before_write() {
        let store = store();
        let source_id = Uuid::from_u128(43);
        let target_id = Uuid::from_u128(44);
        let encrypted = store
            .image_cipher
            .seal(b"image bytes", &image_aad(store.vault_id, source_id))
            .unwrap();

        assert!(matches!(
            store.store_encrypted_image(target_id, &encrypted),
            Err(CoreError::Crypto(_))
        ));
        assert!(!store.object_path(target_id).exists());
    }

    #[test]
    fn tampered_encrypted_image_is_rejected_without_partial_file() {
        let store = store();
        let object_id = Uuid::from_u128(45);
        let mut encrypted = store
            .image_cipher
            .seal(b"image bytes", &image_aad(store.vault_id, object_id))
            .unwrap();
        let last_index = encrypted.len() - 1;
        encrypted[last_index] ^= 0x80;

        assert!(matches!(
            store.store_encrypted_image(object_id, &encrypted),
            Err(CoreError::Crypto(_))
        ));
        assert!(!store.object_path(object_id).exists());
        assert_eq!(fs::read_dir(&store.pending).unwrap().count(), 0);
    }
}
