use clipboard_core::{
    SnapshotError, create_snapshot, list_snapshots, prune_snapshots, restore_snapshot,
    verify_snapshot,
};
use clipboard_storage::{Database, HISTORY_FILE_NAME};
use std::fs;
use tempfile::tempdir;
use uuid::Uuid;

const KEY: [u8; 32] = [0x61; 32];
const VAULT_ID: Uuid = Uuid::from_u128(0x61);

fn open_vault(data_dir: &std::path::Path) -> Database {
    Database::open_vault(data_dir, VAULT_ID, &KEY).unwrap()
}

fn write_object(data_dir: &std::path::Path, name: &str, bytes: &[u8]) {
    let path = data_dir.join("objects").join(name);
    fs::create_dir_all(path.parent().unwrap()).unwrap();
    fs::write(path, bytes).unwrap();
}

#[test]
fn snapshot_round_trip_restores_the_database_and_objects() {
    let data = tempdir().unwrap();
    let snapshots = tempdir().unwrap();
    let database = open_vault(data.path());
    write_object(data.path(), "image-object", b"encrypted-image");
    write_object(data.path(), "file-cache/chunk-0", b"encrypted-chunk");

    let created = create_snapshot(
        &database,
        data.path(),
        VAULT_ID,
        &KEY,
        snapshots.path(),
        1_760_000_000_000,
        3,
    )
    .unwrap();

    assert_eq!(created.file_count, 2);
    assert_eq!(created.total_bytes, 15 + 15);
    assert!(created.directory.join(HISTORY_FILE_NAME).exists());
    assert!(verify_snapshot(&created.directory, VAULT_ID, &KEY).is_ok());

    drop(database);
    fs::write(data.path().join(HISTORY_FILE_NAME), b"damaged").unwrap();
    fs::remove_file(data.path().join("objects").join("image-object")).unwrap();

    let restored = restore_snapshot(
        &created.directory,
        data.path(),
        VAULT_ID,
        &KEY,
        1_760_000_100_000,
    )
    .unwrap();

    assert_eq!(restored.file_count, 2);
    let quarantined = fs::read_dir(data.path())
        .unwrap()
        .filter_map(Result::ok)
        .any(|entry| {
            entry
                .file_name()
                .to_string_lossy()
                .starts_with("history.db.corrupt-")
        });
    assert!(quarantined, "the damaged database must be quarantined");
    assert_eq!(
        fs::read(data.path().join("objects").join("image-object")).unwrap(),
        b"encrypted-image"
    );
    open_vault(data.path());
}

#[test]
fn create_refuses_an_unreadable_database() {
    let data = tempdir().unwrap();
    let snapshots = tempdir().unwrap();
    let database = open_vault(data.path());
    fs::write(data.path().join(HISTORY_FILE_NAME), vec![0u8; 4096]).unwrap();

    let error = create_snapshot(
        &database,
        data.path(),
        VAULT_ID,
        &KEY,
        snapshots.path(),
        1_760_000_000_000,
        3,
    )
    .unwrap_err();

    assert!(matches!(error, SnapshotError::DatabaseUnreadable));
    assert_eq!(fs::read_dir(snapshots.path()).unwrap().count(), 0);
}

#[test]
fn list_skips_a_snapshot_whose_contents_changed() {
    let data = tempdir().unwrap();
    let snapshots = tempdir().unwrap();
    let database = open_vault(data.path());
    write_object(data.path(), "image-object", b"encrypted-image");
    let created = create_snapshot(
        &database,
        data.path(),
        VAULT_ID,
        &KEY,
        snapshots.path(),
        1_760_000_000_000,
        3,
    )
    .unwrap();
    assert_eq!(
        list_snapshots(snapshots.path(), VAULT_ID, &KEY)
            .unwrap()
            .len(),
        1
    );

    fs::write(
        created.directory.join("objects").join("image-object"),
        b"tampered",
    )
    .unwrap();

    assert!(
        list_snapshots(snapshots.path(), VAULT_ID, &KEY)
            .unwrap()
            .is_empty()
    );
}

#[test]
fn prune_keeps_the_newest_snapshots_and_removes_incomplete_directories() {
    let data = tempdir().unwrap();
    let snapshots = tempdir().unwrap();
    let database = open_vault(data.path());
    for index in 0..4 {
        create_snapshot(
            &database,
            data.path(),
            VAULT_ID,
            &KEY,
            snapshots.path(),
            1_760_000_000_000 + index * 1_000,
            2,
        )
        .unwrap();
    }
    let incomplete = snapshots.path().join("snapshot-20200101-000000");
    fs::create_dir_all(&incomplete).unwrap();

    assert_eq!(prune_snapshots(snapshots.path(), 2).unwrap(), 1);

    let names = {
        let mut names: Vec<String> = fs::read_dir(snapshots.path())
            .unwrap()
            .filter_map(Result::ok)
            .map(|entry| entry.file_name().to_string_lossy().into_owned())
            .collect();
        names.sort();
        names
    };
    assert_eq!(names.len(), 2);
    assert!(!names.iter().any(|name| name == "snapshot-20200101-000000"));
    assert!(
        list_snapshots(snapshots.path(), VAULT_ID, &KEY)
            .unwrap()
            .len()
            == 2
    );
}
