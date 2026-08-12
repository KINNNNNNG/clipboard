use clipboard_crypto::{KeyPurpose, VaultKey};
use clipboard_domain::{
    ClipboardContent, ClipboardItem, DeleteState, FileBundle, FileEntry, Hlc, SyncScope,
};
use clipboard_sync::{
    SYNC_PROTOCOL_VERSION, SegmentHeader, SyncError, SyncEvent, open_segment, seal_segment,
};
use uuid::Uuid;

fn journal_key() -> clipboard_crypto::DerivedKey {
    VaultKey::from_bytes([0x42; 32])
        .derive(Uuid::from_u128(1), KeyPurpose::Journal)
        .unwrap()
}

fn header(version: u8) -> SegmentHeader {
    SegmentHeader {
        protocol_version: version,
        vault_id: Uuid::from_u128(2),
        device_id: Uuid::from_u128(3),
        segment_id: Uuid::from_u128(4),
    }
}

fn text_item() -> ClipboardItem {
    ClipboardItem::new(
        Uuid::from_u128(5),
        Uuid::from_u128(2),
        ClipboardContent::Text("alpha".to_owned()),
        "editor.exe".to_owned(),
        100,
    )
}

#[test]
fn encrypted_segment_round_trips_text_event() {
    let event = SyncEvent::try_from(&text_item()).unwrap();
    let sealed = seal_segment(
        &journal_key(),
        &header(SYNC_PROTOCOL_VERSION),
        &[event.clone()],
    )
    .unwrap();

    assert_eq!(
        open_segment(&journal_key(), &header(SYNC_PROTOCOL_VERSION), &sealed).unwrap(),
        vec![event]
    );
}

#[test]
fn deleted_text_item_is_encoded_as_a_delete_event() {
    let mut item = text_item();
    item.delete_state = Some(DeleteState {
        deleted: true,
        updated: Hlc::new(200, 0, Uuid::from_u128(6)),
    });

    assert_eq!(
        SyncEvent::try_from(&item),
        Ok(SyncEvent::Delete {
            item_id: item.id,
            state: item.delete_state.unwrap(),
        })
    );
}

#[test]
fn deleted_vault_image_is_encoded_as_a_delete_event() {
    let mut item = ClipboardItem::new(
        Uuid::from_u128(7),
        Uuid::from_u128(2),
        ClipboardContent::Image {
            object_id: Uuid::from_u128(8),
            width: 1,
            height: 1,
            bytes: 1,
        },
        "editor.exe".to_owned(),
        100,
    );
    let state = DeleteState {
        deleted: true,
        updated: Hlc::new(200, 0, Uuid::from_u128(6)),
    };
    item.delete_state = Some(state);

    assert_eq!(
        SyncEvent::try_from(&item),
        Ok(SyncEvent::Delete {
            item_id: item.id,
            state,
        })
    );
}

#[test]
fn encrypted_segment_rejects_tampering_and_wrong_vault() {
    let event = SyncEvent::try_from(&text_item()).unwrap();
    let expected_header = header(SYNC_PROTOCOL_VERSION);
    let mut sealed = seal_segment(&journal_key(), &expected_header, &[event]).unwrap();
    *sealed.last_mut().unwrap() ^= 0x01;
    assert!(open_segment(&journal_key(), &expected_header, &sealed).is_err());

    let sealed = seal_segment(&journal_key(), &expected_header, &[]).unwrap();
    let wrong_vault = SegmentHeader {
        vault_id: Uuid::from_u128(99),
        ..expected_header
    };
    assert!(open_segment(&journal_key(), &wrong_vault, &sealed).is_err());
}

#[test]
fn segment_rejects_an_unsupported_protocol_version() {
    let unsupported = header(SYNC_PROTOCOL_VERSION + 1);
    assert_eq!(
        seal_segment(&journal_key(), &unsupported, &[]),
        Err(SyncError::UnsupportedProtocolVersion(
            unsupported.protocol_version
        ))
    );
}

#[test]
fn sync_event_rejects_non_text_and_local_only_items() {
    let image = ClipboardItem::new(
        Uuid::from_u128(6),
        Uuid::from_u128(2),
        ClipboardContent::Image {
            object_id: Uuid::from_u128(7),
            width: 1,
            height: 1,
            bytes: 1,
        },
        "editor.exe".to_owned(),
        100,
    );
    assert_eq!(image.sync_scope(), SyncScope::Vault);
    assert_eq!(
        SyncEvent::try_from(&image),
        Err(SyncError::LocalOnlyRejected)
    );

    let local_only = ClipboardItem::new(
        Uuid::from_u128(8),
        Uuid::from_u128(2),
        ClipboardContent::FileBundle(
            FileBundle::new(vec![FileEntry::file(
                "C:\\private\\report.txt".to_owned(),
                1,
                100,
            )])
            .unwrap(),
        ),
        "explorer.exe".to_owned(),
        100,
    );
    assert_eq!(local_only.sync_scope(), SyncScope::LocalOnly);
    assert_eq!(
        SyncEvent::try_from(&local_only),
        Err(SyncError::LocalOnlyRejected)
    );
}

#[test]
fn seal_segment_rejects_a_directly_constructed_file_bundle_event() {
    let file_item = ClipboardItem::new(
        Uuid::from_u128(9),
        Uuid::from_u128(2),
        ClipboardContent::FileBundle(
            FileBundle::new(vec![FileEntry::file(
                "C:\\private\\payroll.xlsx".to_owned(),
                1,
                100,
            )])
            .unwrap(),
        ),
        "explorer.exe".to_owned(),
        100,
    );

    assert_eq!(
        seal_segment(
            &journal_key(),
            &header(SYNC_PROTOCOL_VERSION),
            &[SyncEvent::TextUpsert { item: file_item }],
        ),
        Err(SyncError::LocalOnlyRejected)
    );
}
