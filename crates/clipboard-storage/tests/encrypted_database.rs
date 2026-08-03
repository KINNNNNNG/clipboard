use clipboard_storage::Database;
use tempfile::tempdir;

#[test]
fn sqlcipher_database_reopens_with_the_correct_key() {
    let dir = tempdir().unwrap();
    let path = dir.path().join("history.db");
    let key = [0x11; 32];

    let database = Database::open(&path, &key).unwrap();
    assert!(!database.cipher_version().is_empty());
    drop(database);

    let reopened = Database::open(&path, &key).unwrap();
    assert!(!reopened.cipher_version().is_empty());
}

#[test]
fn wrong_database_key_cannot_read_schema() {
    let dir = tempdir().unwrap();
    let path = dir.path().join("history.db");

    Database::open(&path, &[0x11; 32]).unwrap();
    assert!(Database::open(&path, &[0x22; 32]).is_err());
}
