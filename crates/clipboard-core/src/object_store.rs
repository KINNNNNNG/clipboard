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
