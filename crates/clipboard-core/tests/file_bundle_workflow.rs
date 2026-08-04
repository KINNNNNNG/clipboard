use clipboard_core::{CoreCommand, CoreResponse, CoreService, IngestFileBundle, ReadFileBundle};
use clipboard_domain::{FileEntry, FileEntryKind};
use clipboard_storage::Database;
use tempfile::tempdir;
use uuid::Uuid;

const KEY: [u8; 32] = [0x71; 32];

#[test]
fn normalized_file_bundle_reuses_item_id_stays_local_and_can_be_read() {
    let directory = tempdir().unwrap();
    let vault_id = Uuid::from_u128(201);
    let mut core = CoreService::open(directory.path(), vault_id, &KEY).unwrap();

    let first_id = mutation_id(
        core.execute(CoreCommand::IngestFileBundle(IngestFileBundle {
            entries: vec![FileEntry::file("C:\\Docs\\a.txt".into(), 42, 100)],
            source_app: "explorer.exe".into(),
            captured_ms: 100,
        }))
        .unwrap(),
    );
    let second_id = mutation_id(
        core.execute(CoreCommand::IngestFileBundle(IngestFileBundle {
            entries: vec![FileEntry::file("c:/docs/A.txt/".into(), 42, 100)],
            source_app: "explorer.exe".into(),
            captured_ms: 200,
        }))
        .unwrap(),
    );

    assert_eq!(first_id, second_id);
    assert_eq!(
        Database::open(&directory.path().join("history.db"), &KEY)
            .unwrap()
            .outbox()
            .pending_count()
            .unwrap(),
        0
    );

    let response = core
        .execute(CoreCommand::ReadFileBundle(ReadFileBundle {
            item_id: first_id,
        }))
        .unwrap();
    let CoreResponse::FileBundle { item_id, entries } = response else {
        panic!("expected file bundle response");
    };
    assert_eq!(item_id, first_id);
    assert_eq!(entries.len(), 1);
    assert_eq!(entries[0].path, "C:\\Docs\\a.txt");
    assert_eq!(entries[0].kind, FileEntryKind::File);
    assert_eq!(entries[0].size, 42);
    assert_eq!(entries[0].modified_ms, 100);
}

#[test]
fn invalid_file_bundles_fail_before_storage() {
    let directory = tempdir().unwrap();
    let vault_id = Uuid::from_u128(202);
    let mut core = CoreService::open(directory.path(), vault_id, &KEY).unwrap();

    for entries in [
        vec![],
        vec![FileEntry::file("relative\\a.txt".into(), 1, 1)],
        vec![FileEntry::file("   ".into(), 1, 1)],
        vec![
            FileEntry::file("C:\\Docs\\a.txt".into(), 1, 1),
            FileEntry::file("c:/docs/A.txt/".into(), 1, 1),
        ],
    ] {
        assert!(
            core.execute(CoreCommand::IngestFileBundle(IngestFileBundle {
                entries,
                source_app: "explorer.exe".into(),
                captured_ms: 100,
            }))
            .is_err()
        );
    }

    let database = Database::open(&directory.path().join("history.db"), &KEY).unwrap();
    assert!(database.items().list().unwrap().is_empty());
    assert_eq!(database.outbox().pending_count().unwrap(), 0);
}

fn mutation_id(response: CoreResponse) -> Uuid {
    match response {
        CoreResponse::Mutation { item_id } => item_id,
        _ => panic!("expected mutation response"),
    }
}
