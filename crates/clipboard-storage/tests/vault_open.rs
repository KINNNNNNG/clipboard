use clipboard_domain::{ClipboardContent, ClipboardItem};
use clipboard_storage::{Database, StorageError, VAULT_MARKER_FILE_NAME};
use rusqlite::{Connection, params};
use tempfile::tempdir;
use uuid::Uuid;

const VAULT_ID: Uuid = Uuid::from_u128(0x0a11);
const OTHER_VAULT_ID: Uuid = Uuid::from_u128(0x0b22);
const KEY: [u8; 32] = [0x51; 32];
const OTHER_KEY: [u8; 32] = [0x52; 32];

#[test]
fn vault_marker_is_written_on_first_open_and_verified_on_reopen() {
    let directory = tempdir().unwrap();

    let database = Database::open_vault(directory.path(), VAULT_ID, &KEY).unwrap();
    assert!(!database.cipher_version().is_empty());
    drop(database);

    let marker = std::fs::read_to_string(directory.path().join(VAULT_MARKER_FILE_NAME)).unwrap();
    assert_eq!(marker.trim(), VAULT_ID.to_string());

    let reopened = Database::open_vault(directory.path(), VAULT_ID, &KEY).unwrap();
    assert!(!reopened.cipher_version().is_empty());
}

#[test]
fn mismatched_vault_is_rejected_before_the_database_is_touched() {
    let directory = tempdir().unwrap();
    let database = Database::open_vault(directory.path(), VAULT_ID, &KEY).unwrap();
    let item = ClipboardItem::new(
        Uuid::from_u128(0x0a12),
        VAULT_ID,
        ClipboardContent::Text("preserved history".into()),
        "notepad.exe".into(),
        100,
    );
    database.items().insert(&item).unwrap();
    drop(database);

    let path = directory.path().join("history.db");
    let before = std::fs::read(&path).unwrap();
    let error = Database::open_vault(directory.path(), OTHER_VAULT_ID, &KEY)
        .err()
        .expect("opening the vault should fail");
    assert!(matches!(error, StorageError::VaultMismatch));
    assert_eq!(std::fs::read(&path).unwrap(), before);

    let reopened = Database::open_vault(directory.path(), VAULT_ID, &KEY).unwrap();
    assert_eq!(reopened.items().list().unwrap(), vec![item]);
}

#[test]
fn wrong_key_after_a_matching_marker_reports_corruption() {
    let directory = tempdir().unwrap();
    Database::open_vault(directory.path(), VAULT_ID, &KEY).unwrap();

    let error = Database::open_vault(directory.path(), VAULT_ID, &OTHER_KEY)
        .err()
        .expect("opening the vault should fail");

    assert!(matches!(error, StorageError::Corrupt));
}

#[test]
fn legacy_database_without_a_marker_reports_an_unreadable_vault() {
    let directory = tempdir().unwrap();
    Database::open(&directory.path().join("history.db"), &KEY).unwrap();

    let error = Database::open_vault(directory.path(), VAULT_ID, &OTHER_KEY)
        .err()
        .expect("opening the vault should fail");

    assert!(matches!(error, StorageError::Unreadable));
}

#[test]
fn exclusive_lock_reports_a_locked_database() {
    let directory = tempdir().unwrap();
    Database::open_vault(directory.path(), VAULT_ID, &KEY).unwrap();
    let path = directory.path().join("history.db");
    let blocker = Connection::open(&path).unwrap();
    blocker
        .pragma_update(None, "key", format!("x'{}'", hex::encode(KEY)))
        .unwrap();
    blocker.execute_batch("BEGIN EXCLUSIVE;").unwrap();

    let error = Database::open_vault(directory.path(), VAULT_ID, &KEY)
        .err()
        .expect("opening the vault should fail");

    assert!(matches!(error, StorageError::Locked));
}

#[test]
fn newer_schema_version_reports_a_migration_failure() {
    let directory = tempdir().unwrap();
    let path = directory.path().join("history.db");
    {
        let connection = Connection::open(&path).unwrap();
        connection
            .pragma_update(None, "key", format!("x'{}'", hex::encode(KEY)))
            .unwrap();
        connection
            .execute_batch(include_str!("../migrations/001_initial.sql"))
            .unwrap();
        connection
            .execute(
                "INSERT INTO schema_migrations(version, applied_at_ms) VALUES (?1, ?2)",
                params![99_i64, 0_i64],
            )
            .unwrap();
    }
    std::fs::write(
        directory.path().join(VAULT_MARKER_FILE_NAME),
        format!("{VAULT_ID}\n"),
    )
    .unwrap();

    let error = Database::open_vault(directory.path(), VAULT_ID, &KEY)
        .err()
        .expect("opening the vault should fail");

    assert!(matches!(
        error,
        StorageError::UnsupportedSchemaVersion {
            found: 99,
            supported: 3
        }
    ));
}
