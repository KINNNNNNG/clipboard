use clipboard_ffi::{CoreHandle, CoreStatus, clipboard_core_close, clipboard_core_open_v2};
use rusqlite::{Connection, params};
use std::ptr::null_mut;
use tempfile::tempdir;
use uuid::Uuid;

const VAULT_ID: Uuid = Uuid::from_u128(0x0c01);
const OTHER_VAULT_ID: Uuid = Uuid::from_u128(0x0c02);
const KEY: [u8; 32] = [0x61; 32];
const OTHER_KEY: [u8; 32] = [0x62; 32];

fn open(data_dir: &str, vault_id: Uuid, key: &[u8; 32]) -> CoreStatus {
    let mut handle: *mut CoreHandle = null_mut();
    let status = unsafe {
        clipboard_core_open_v2(
            data_dir.as_ptr(),
            data_dir.len(),
            key.as_ptr(),
            key.len(),
            vault_id.as_bytes().as_ptr(),
            16,
            &mut handle,
        )
    };
    if status == CoreStatus::Ok {
        unsafe { clipboard_core_close(handle) };
    }
    status
}

#[test]
fn status_codes_keep_their_published_values() {
    assert_eq!(CoreStatus::StorageLocked as i32, 7);
    assert_eq!(CoreStatus::VaultKeyMismatch as i32, 8);
    assert_eq!(CoreStatus::VaultUnreadable as i32, 9);
    assert_eq!(CoreStatus::VaultCorrupt as i32, 10);
    assert_eq!(CoreStatus::StorageMigration as i32, 11);
}

#[test]
fn open_v2_reports_a_mismatched_vault() {
    let directory = tempdir().unwrap();
    let path = directory.path().to_string_lossy();

    assert_eq!(open(&path, VAULT_ID, &KEY), CoreStatus::Ok);
    assert_eq!(
        open(&path, OTHER_VAULT_ID, &KEY),
        CoreStatus::VaultKeyMismatch
    );
}

#[test]
fn open_v2_reports_corruption_when_the_marker_matches_but_the_key_does_not() {
    let directory = tempdir().unwrap();
    let path = directory.path().to_string_lossy();

    assert_eq!(open(&path, VAULT_ID, &KEY), CoreStatus::Ok);
    assert_eq!(open(&path, VAULT_ID, &OTHER_KEY), CoreStatus::VaultCorrupt);
}

#[test]
fn open_v2_reports_a_locked_vault() {
    let directory = tempdir().unwrap();
    let path = directory.path().to_string_lossy();
    assert_eq!(open(&path, VAULT_ID, &KEY), CoreStatus::Ok);
    let connection = Connection::open(directory.path().join("history.db")).unwrap();
    connection
        .pragma_update(None, "key", format!("x'{}'", hex::encode(KEY)))
        .unwrap();
    connection.execute_batch("BEGIN EXCLUSIVE;").unwrap();

    assert_eq!(open(&path, VAULT_ID, &KEY), CoreStatus::StorageLocked);
}

#[test]
fn open_v2_reports_an_unsupported_schema_version() {
    let directory = tempdir().unwrap();
    let path = directory.path().to_string_lossy();
    assert_eq!(open(&path, VAULT_ID, &KEY), CoreStatus::Ok);
    let connection = Connection::open(directory.path().join("history.db")).unwrap();
    connection
        .pragma_update(None, "key", format!("x'{}'", hex::encode(KEY)))
        .unwrap();
    connection
        .execute(
            "INSERT INTO schema_migrations(version, applied_at_ms) VALUES (?1, ?2)",
            params![99_i64, 0_i64],
        )
        .unwrap();
    drop(connection);

    assert_eq!(open(&path, VAULT_ID, &KEY), CoreStatus::StorageMigration);
}
