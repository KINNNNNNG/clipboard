use std::path::{Path, PathBuf};

use uuid::Uuid;

use crate::StorageError;

/// Name of the non-secret sidecar that records which vault a history database belongs to.
pub const VAULT_MARKER_FILE_NAME: &str = "history.vault";

/// Reads the vault recorded next to the encrypted history database.
///
/// A missing or empty marker returns `None` so legacy vaults keep opening; anything else than a
/// canonical UUID is reported instead of guessed.
pub(crate) fn read(data_dir: &Path) -> Result<Option<Uuid>, StorageError> {
    let path = data_dir.join(VAULT_MARKER_FILE_NAME);
    let text = match std::fs::read_to_string(&path) {
        Ok(text) => text,
        Err(error) if error.kind() == std::io::ErrorKind::NotFound => return Ok(None),
        Err(error) => return Err(StorageError::Io(error)),
    };
    let trimmed = text.trim();
    if trimmed.is_empty() {
        return Ok(None);
    }
    Uuid::parse_str(trimmed)
        .map(Some)
        .map_err(|_| StorageError::VaultMarkerInvalid)
}

/// Records the vault for a history database that does not have a marker yet.
///
/// The write is atomic so a crash cannot leave a half-written identifier behind.
pub(crate) fn write_if_absent(data_dir: &Path, vault_id: Uuid) -> Result<(), StorageError> {
    let path = data_dir.join(VAULT_MARKER_FILE_NAME);
    if path.exists() {
        return Ok(());
    }
    let temporary_path: PathBuf = data_dir.join(format!("history-{}.tmp", Uuid::new_v4().simple()));
    std::fs::write(&temporary_path, format!("{vault_id}\n"))?;
    std::fs::rename(&temporary_path, &path)?;
    Ok(())
}
