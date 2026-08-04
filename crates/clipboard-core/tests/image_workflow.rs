use clipboard_core::{
    ApplyRetentionRequest, CoreCommand, CoreError, CoreResponse, CoreService, IngestImage,
    SearchFilters, SearchRequest,
};
use clipboard_domain::RetentionPolicy;
use clipboard_search::SearchMode;
use clipboard_storage::Database;
use rusqlite::Connection;
use std::{fs, path::Path};
use tempfile::tempdir;
use uuid::Uuid;

const KEY: [u8; 32] = [0x61; 32];

#[test]
fn encrypted_image_round_trips_after_reopen_and_search_exposes_metadata() {
    let directory = tempdir().unwrap();
    let vault_id = Uuid::from_u128(101);
    let png = png_fixture();
    let item_id;
    {
        let mut core = CoreService::open(directory.path(), vault_id, &KEY).unwrap();
        item_id = mutation_id(
            core.ingest_image(
                IngestImage {
                    width: 1,
                    height: 1,
                    source_app: "mspaint.exe".into(),
                    captured_ms: 100,
                },
                &png,
            )
            .unwrap(),
        );

        let response = core
            .execute(CoreCommand::Search(SearchRequest {
                pattern: String::new(),
                mode: SearchMode::Substring,
                filters: SearchFilters {
                    kinds: vec!["image".into()],
                    ..SearchFilters::default()
                },
            }))
            .unwrap();
        let items = response.search_items();
        assert_eq!(items.len(), 1);
        assert_eq!(items[0].id, item_id);
        assert_eq!(items[0].kind, "image");
        assert_eq!(items[0].width, Some(1));
        assert_eq!(items[0].height, Some(1));
        assert_eq!(items[0].bytes, Some(png.len() as u64));
    }

    let encrypted = fs::read(single_object_file(directory.path())).unwrap();
    assert!(!encrypted.starts_with(&png[..8]));

    let reopened = CoreService::open(directory.path(), vault_id, &KEY).unwrap();
    assert_eq!(reopened.read_image(item_id).unwrap(), png);
}

#[test]
fn tampered_image_ciphertext_is_rejected_without_partial_plaintext() {
    let directory = tempdir().unwrap();
    let vault_id = Uuid::from_u128(102);
    let png = png_fixture();
    let core = CoreService::open(directory.path(), vault_id, &KEY).unwrap();
    let item_id = mutation_id(
        core.ingest_image(
            IngestImage {
                width: 1,
                height: 1,
                source_app: "mspaint.exe".into(),
                captured_ms: 100,
            },
            &png,
        )
        .unwrap(),
    );
    let object_path = single_object_file(directory.path());
    let mut encrypted = fs::read(&object_path).unwrap();
    *encrypted.last_mut().unwrap() ^= 0x80;
    fs::write(&object_path, encrypted).unwrap();

    assert!(matches!(
        core.read_image(item_id),
        Err(CoreError::Crypto(
            clipboard_crypto::CryptoError::Authentication
        ))
    ));
}

#[test]
fn failed_database_commit_removes_the_finalized_image_object() {
    let directory = tempdir().unwrap();
    let database_path = directory.path().join("history.db");
    Database::open(&database_path, &KEY).unwrap();
    install_failing_outbox_trigger(&database_path);
    let core = CoreService::open(directory.path(), Uuid::from_u128(103), &KEY).unwrap();

    assert!(
        core.ingest_image(
            IngestImage {
                width: 1,
                height: 1,
                source_app: "mspaint.exe".into(),
                captured_ms: 100,
            },
            &png_fixture(),
        )
        .is_err()
    );

    assert!(object_files(directory.path()).is_empty());
}

#[test]
fn opening_core_removes_files_left_in_the_pending_directory() {
    let directory = tempdir().unwrap();
    let pending = directory.path().join("objects").join(".pending");
    fs::create_dir_all(&pending).unwrap();
    let interrupted = pending.join("interrupted.tmp");
    fs::write(&interrupted, b"partial ciphertext").unwrap();

    CoreService::open(directory.path(), Uuid::from_u128(104), &KEY).unwrap();

    assert!(!interrupted.exists());
}

#[test]
fn image_space_retention_removes_the_encrypted_object_after_tombstoning_the_history_item() {
    let directory = tempdir().unwrap();
    let vault_id = Uuid::from_u128(105);
    let mut core = CoreService::open(directory.path(), vault_id, &KEY).unwrap();
    mutation_id(
        core.ingest_image(
            IngestImage {
                width: 1,
                height: 1,
                source_app: "mspaint.exe".into(),
                captured_ms: 100,
            },
            &png_fixture(),
        )
        .unwrap(),
    );
    assert_eq!(object_files(directory.path()).len(), 1);

    core.execute(CoreCommand::ApplyRetention(ApplyRetentionRequest {
        now_ms: 200,
        policy: RetentionPolicy {
            max_regular_items: None,
            max_age_days: None,
            max_image_bytes: Some(0),
        },
    }))
    .unwrap();

    assert!(object_files(directory.path()).is_empty());
}

fn mutation_id(response: CoreResponse) -> Uuid {
    match response {
        CoreResponse::Mutation { item_id } => item_id,
        _ => panic!("expected mutation response"),
    }
}

fn png_fixture() -> Vec<u8> {
    hex::decode(include_str!("../../../tests/fixtures/1x1.png.hex").trim()).unwrap()
}

fn object_files(data_dir: &Path) -> Vec<std::path::PathBuf> {
    fs::read_dir(data_dir.join("objects"))
        .unwrap()
        .filter_map(Result::ok)
        .map(|entry| entry.path())
        .filter(|path| path.is_file())
        .collect()
}

fn single_object_file(data_dir: &Path) -> std::path::PathBuf {
    let files = object_files(data_dir);
    assert_eq!(files.len(), 1);
    files.into_iter().next().unwrap()
}

fn install_failing_outbox_trigger(path: &Path) {
    let connection = Connection::open(path).unwrap();
    let encoded_key = format!("x'{}'", hex::encode(KEY));
    connection.pragma_update(None, "key", encoded_key).unwrap();
    connection
        .execute_batch(
            "CREATE TRIGGER fail_image_outbox
             BEFORE INSERT ON sync_outbox
             BEGIN
                 SELECT RAISE(ABORT, 'injected image outbox failure');
             END;",
        )
        .unwrap();
}
