use clipboard_core::{
    CacheFileBundle, CoreCommand, CoreError, CoreResponse, CoreService, DeleteRequest,
    IngestFileBundle, ReadFileBundle, SetFavorite, UncacheFileBundle,
};
use clipboard_domain::{ClipboardContent, FileEntry, Hlc};
use clipboard_storage::Database;
use std::fs;
use tempfile::tempdir;
use uuid::Uuid;

const KEY: [u8; 32] = [0x62; 32];

#[test]
fn opening_core_removes_abandoned_file_cache_pending_objects() {
    let directory = tempdir().unwrap();
    let pending = directory
        .path()
        .join("objects")
        .join("file-cache")
        .join(".pending")
        .join("abandoned");
    fs::create_dir_all(&pending).unwrap();
    fs::write(pending.join("chunk.clipobj"), b"partial cache").unwrap();

    CoreService::open(directory.path(), Uuid::from_u128(699), &KEY).unwrap();

    assert!(!pending.exists());
}

#[test]
fn favorite_file_cache_rejects_capacity_overflow_without_partial_objects() {
    let directory = tempdir().unwrap();
    let source = directory.path().join("too-large.txt");
    fs::write(&source, b"too large").unwrap();
    let vault_id = Uuid::from_u128(700);
    let mut core = CoreService::open(directory.path(), vault_id, &KEY).unwrap();
    let item_id = ingest_file(&mut core, &source);

    let result = core.execute(CoreCommand::CacheFileBundle(CacheFileBundle {
        item_id,
        max_bytes: 1,
    }));

    assert!(result.is_err());
    let stored = Database::open(&directory.path().join("history.db"), &KEY)
        .unwrap()
        .items()
        .list_all()
        .unwrap();
    let ClipboardContent::FileBundle(bundle) = &stored[0].content else {
        panic!("expected file bundle");
    };
    assert!(bundle.cache.is_none());
    assert_eq!(
        Database::open(&directory.path().join("history.db"), &KEY)
            .unwrap()
            .outbox()
            .pending_count()
            .unwrap(),
        0
    );
    assert!(fs::read_dir(directory.path().join("objects").join("file-cache")).is_err());
}

#[test]
fn cached_favorite_file_bundle_restores_after_source_is_deleted() {
    let directory = tempdir().unwrap();
    let source = directory.path().join("restore-me.txt");
    fs::write(&source, b"cached contents").unwrap();
    let vault_id = Uuid::from_u128(701);
    let mut core = CoreService::open(directory.path(), vault_id, &KEY).unwrap();
    let item_id = ingest_file(&mut core, &source);

    core.execute(CoreCommand::CacheFileBundle(CacheFileBundle {
        item_id,
        max_bytes: 1024,
    }))
    .unwrap();
    core.execute(CoreCommand::SetFavorite(SetFavorite {
        item_id,
        favorite: true,
        updated: Hlc::new(2, 0, Uuid::from_u128(702)),
    }))
    .unwrap();
    fs::remove_file(&source).unwrap();

    let CoreResponse::FileBundle { entries, .. } = core
        .execute(CoreCommand::ReadFileBundle(ReadFileBundle { item_id }))
        .unwrap()
    else {
        panic!("expected file bundle response");
    };
    assert_eq!(entries.len(), 1);
    assert_ne!(entries[0].path, source.to_string_lossy());
    assert_eq!(fs::read(&entries[0].path).unwrap(), b"cached contents");
    assert_eq!(
        Database::open(&directory.path().join("history.db"), &KEY)
            .unwrap()
            .outbox()
            .pending_count()
            .unwrap(),
        0
    );
}

#[test]
fn favoriting_a_file_bundle_without_a_cache_is_rejected() {
    let directory = tempdir().unwrap();
    let source = directory.path().join("not-cached.txt");
    fs::write(&source, b"not cached").unwrap();
    let mut core = CoreService::open(directory.path(), Uuid::from_u128(703), &KEY).unwrap();
    let item_id = ingest_file(&mut core, &source);

    let result = core.execute(CoreCommand::SetFavorite(SetFavorite {
        item_id,
        favorite: true,
        updated: Hlc::new(2, 0, Uuid::from_u128(704)),
    }));

    assert!(matches!(result, Err(CoreError::FileCacheMissing(id)) if id == item_id));
    let stored = Database::open(&directory.path().join("history.db"), &KEY)
        .unwrap()
        .items()
        .list_all()
        .unwrap();
    assert!(!stored[0].favorite_state.is_some_and(|state| state.value));
}

#[test]
fn cached_directory_bundle_restores_nested_files_after_source_is_deleted() {
    let directory = tempdir().unwrap();
    let source = directory.path().join("folder");
    fs::create_dir_all(source.join("nested")).unwrap();
    fs::write(
        source.join("nested").join("document.txt"),
        b"directory contents",
    )
    .unwrap();
    let mut core = CoreService::open(directory.path(), Uuid::from_u128(705), &KEY).unwrap();
    let item_id = ingest_directory(&mut core, &source);

    core.execute(CoreCommand::CacheFileBundle(CacheFileBundle {
        item_id,
        max_bytes: 1024,
    }))
    .unwrap();
    fs::remove_dir_all(&source).unwrap();

    let CoreResponse::FileBundle { entries, .. } = core
        .execute(CoreCommand::ReadFileBundle(ReadFileBundle { item_id }))
        .unwrap()
    else {
        panic!("expected file bundle response");
    };
    assert_eq!(entries.len(), 1);
    assert_eq!(
        fs::read(
            std::path::Path::new(&entries[0].path)
                .join("nested")
                .join("document.txt")
        )
        .unwrap(),
        b"directory contents"
    );
}

#[test]
fn uncaching_a_file_bundle_prevents_replay_after_source_is_deleted() {
    let directory = tempdir().unwrap();
    let source = directory.path().join("uncached.txt");
    fs::write(&source, b"will be removed").unwrap();
    let mut core = CoreService::open(directory.path(), Uuid::from_u128(706), &KEY).unwrap();
    let item_id = ingest_file(&mut core, &source);

    core.execute(CoreCommand::CacheFileBundle(CacheFileBundle {
        item_id,
        max_bytes: 1024,
    }))
    .unwrap();
    core.execute(CoreCommand::UncacheFileBundle(UncacheFileBundle {
        item_id,
    }))
    .unwrap();
    fs::remove_file(&source).unwrap();

    let CoreResponse::FileBundle { entries, .. } = core
        .execute(CoreCommand::ReadFileBundle(ReadFileBundle { item_id }))
        .unwrap()
    else {
        panic!("expected file bundle response");
    };
    assert!(
        entries[0]
            .path
            .eq_ignore_ascii_case(&source.to_string_lossy())
    );
    assert!(!std::path::Path::new(&entries[0].path).exists());
    assert!(
        !directory
            .path()
            .join("objects")
            .join("file-cache")
            .join(item_id.to_string())
            .exists()
    );
}

#[test]
fn removing_file_bundle_favorite_releases_its_cache() {
    let directory = tempdir().unwrap();
    let source = directory.path().join("unfavorite.txt");
    fs::write(&source, b"cached").unwrap();
    let mut core = CoreService::open(directory.path(), Uuid::from_u128(708), &KEY).unwrap();
    let item_id = ingest_file(&mut core, &source);

    core.execute(CoreCommand::CacheFileBundle(CacheFileBundle {
        item_id,
        max_bytes: 1024,
    }))
    .unwrap();
    core.execute(CoreCommand::SetFavorite(SetFavorite {
        item_id,
        favorite: true,
        updated: Hlc::new(2, 0, Uuid::from_u128(709)),
    }))
    .unwrap();
    core.execute(CoreCommand::SetFavorite(SetFavorite {
        item_id,
        favorite: false,
        updated: Hlc::new(3, 0, Uuid::from_u128(709)),
    }))
    .unwrap();

    let stored = Database::open(&directory.path().join("history.db"), &KEY)
        .unwrap()
        .items()
        .list_all()
        .unwrap();
    let ClipboardContent::FileBundle(bundle) = &stored[0].content else {
        panic!("expected file bundle");
    };
    assert!(bundle.cache.is_none());
    assert!(
        !directory
            .path()
            .join("objects")
            .join("file-cache")
            .join(item_id.to_string())
            .exists()
    );
}

#[test]
fn deleting_a_local_file_bundle_releases_its_cache() {
    let directory = tempdir().unwrap();
    let source = directory.path().join("delete-cached.txt");
    fs::write(&source, b"cached").unwrap();
    let mut core = CoreService::open(directory.path(), Uuid::from_u128(710), &KEY).unwrap();
    let item_id = ingest_file(&mut core, &source);

    core.execute(CoreCommand::CacheFileBundle(CacheFileBundle {
        item_id,
        max_bytes: 1024,
    }))
    .unwrap();
    core.execute(CoreCommand::Delete(DeleteRequest {
        item_id,
        updated: Hlc::new(2, 0, Uuid::from_u128(711)),
    }))
    .unwrap();

    assert!(
        !directory
            .path()
            .join("objects")
            .join("file-cache")
            .join(item_id.to_string())
            .exists()
    );
    assert!(
        !directory
            .path()
            .join("staging")
            .join(item_id.to_string())
            .exists()
    );
}

#[test]
fn cached_bundle_keeps_same_named_roots_from_different_source_directories_distinct() {
    let directory = tempdir().unwrap();
    let first_dir = directory.path().join("first");
    let second_dir = directory.path().join("second");
    fs::create_dir_all(&first_dir).unwrap();
    fs::create_dir_all(&second_dir).unwrap();
    let first = first_dir.join("same-name.txt");
    let second = second_dir.join("same-name.txt");
    fs::write(&first, b"first").unwrap();
    fs::write(&second, b"second").unwrap();
    let mut core = CoreService::open(directory.path(), Uuid::from_u128(707), &KEY).unwrap();
    let item_id = ingest_files(&mut core, &[&first, &second]);

    core.execute(CoreCommand::CacheFileBundle(CacheFileBundle {
        item_id,
        max_bytes: 1024,
    }))
    .unwrap();
    fs::remove_file(&first).unwrap();
    fs::remove_file(&second).unwrap();

    let CoreResponse::FileBundle { entries, .. } = core
        .execute(CoreCommand::ReadFileBundle(ReadFileBundle { item_id }))
        .unwrap()
    else {
        panic!("expected file bundle response");
    };
    assert_ne!(entries[0].path, entries[1].path);
    assert_eq!(fs::read(&entries[0].path).unwrap(), b"first");
    assert_eq!(fs::read(&entries[1].path).unwrap(), b"second");
}

fn ingest_file(core: &mut CoreService, path: &std::path::Path) -> Uuid {
    let path_text = path.to_string_lossy().into_owned();
    let response = core
        .execute(CoreCommand::IngestFileBundle(IngestFileBundle {
            entries: vec![FileEntry::file(path_text, 14, 1)],
            source_app: "explorer.exe".into(),
            captured_ms: 1,
            source_app_display_name: None,
        }))
        .unwrap();
    match response {
        CoreResponse::Mutation { item_id } => item_id,
        _ => panic!("expected mutation response"),
    }
}

fn ingest_directory(core: &mut CoreService, path: &std::path::Path) -> Uuid {
    let response = core
        .execute(CoreCommand::IngestFileBundle(IngestFileBundle {
            entries: vec![FileEntry::directory(path.to_string_lossy().into_owned(), 1)],
            source_app: "explorer.exe".into(),
            captured_ms: 1,
            source_app_display_name: None,
        }))
        .unwrap();
    match response {
        CoreResponse::Mutation { item_id } => item_id,
        _ => panic!("expected mutation response"),
    }
}

fn ingest_files(core: &mut CoreService, paths: &[&std::path::Path]) -> Uuid {
    let response = core
        .execute(CoreCommand::IngestFileBundle(IngestFileBundle {
            entries: paths
                .iter()
                .map(|path| FileEntry::file(path.to_string_lossy().into_owned(), 1, 1))
                .collect(),
            source_app: "explorer.exe".into(),
            captured_ms: 1,
            source_app_display_name: None,
        }))
        .unwrap();
    match response {
        CoreResponse::Mutation { item_id } => item_id,
        _ => panic!("expected mutation response"),
    }
}
