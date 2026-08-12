use std::fs;

use clipboard_sync::{DirectoryTransport, SegmentHeader, SyncTransport};
use tempfile::tempdir;
use uuid::Uuid;

fn header(segment_id: u128) -> SegmentHeader {
    SegmentHeader {
        protocol_version: 1,
        vault_id: Uuid::from_u128(1),
        device_id: Uuid::from_u128(2),
        segment_id: Uuid::from_u128(segment_id),
    }
}

#[test]
fn directory_transport_ignores_pending_uploads_until_rename() {
    let directory = tempdir().unwrap();
    let transport = DirectoryTransport::open(directory.path()).unwrap();
    fs::write(
        directory.path().join(
            "00000000-0000-0000-0000-000000000002-00000000-0000-0000-0000-000000000003.pending",
        ),
        b"ciphertext",
    )
    .unwrap();

    assert!(
        transport
            .list_segments(header(3).device_id)
            .unwrap()
            .is_empty()
    );
}

#[test]
fn directory_transport_publishes_encrypted_segments_in_filename_order() {
    let directory = tempdir().unwrap();
    let transport = DirectoryTransport::open(directory.path()).unwrap();
    let later = header(20);
    let earlier = header(10);
    transport.put_segment(&later, b"later").unwrap();
    transport.put_segment(&earlier, b"earlier").unwrap();

    assert_eq!(
        transport.list_segments(earlier.device_id).unwrap(),
        vec![earlier, later]
    );
    assert_eq!(transport.get_segment(&earlier).unwrap(), b"earlier");
    assert!(directory.path().read_dir().unwrap().all(|entry| {
        !entry
            .unwrap()
            .file_name()
            .to_string_lossy()
            .ends_with(".pending")
    }));
}

#[test]
fn directory_transport_publishes_image_objects_without_listing_them_as_segments() {
    let directory = tempdir().unwrap();
    let transport = DirectoryTransport::open(directory.path()).unwrap();
    let object_name = "image-00000000-0000-0000-0000-000000000009.enc";
    transport
        .put_object(object_name, b"encrypted-image")
        .unwrap();

    assert_eq!(
        transport.get_object(object_name).unwrap(),
        b"encrypted-image"
    );
    assert!(transport.list_all_segments().unwrap().is_empty());
}
