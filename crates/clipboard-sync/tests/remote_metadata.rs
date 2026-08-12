use clipboard_sync::{
    DirectoryTransport, RemoteHeader, RemoteMetadataStore, SegmentHeader, SnapshotId,
    SnapshotManifest, SnapshotSegment, SyncTransport, VersionVector,
};
use tempfile::tempdir;
use uuid::Uuid;

#[test]
fn directory_transport_round_trips_header_device_state_and_snapshot_metadata() {
    let directory = tempdir().unwrap();
    let transport = DirectoryTransport::open(directory.path()).unwrap();
    let header = RemoteHeader::new(Uuid::from_u128(1));
    transport.put_header(&header).unwrap();
    assert_eq!(transport.get_header().unwrap(), Some(header.clone()));

    let device = Uuid::from_u128(2);
    transport
        .put_device_state(device, b"encrypted-state")
        .unwrap();
    assert_eq!(
        transport.get_device_state(device).unwrap(),
        Some(b"encrypted-state".to_vec())
    );
    transport
        .put_device_state(Uuid::from_u128(4), b"other-state")
        .unwrap();
    assert_eq!(
        transport.list_device_states().unwrap(),
        vec![device, Uuid::from_u128(4)]
    );

    let snapshot = SnapshotId(Uuid::from_u128(3));
    transport
        .put_snapshot(snapshot, b"encrypted-snapshot")
        .unwrap();
    assert_eq!(
        transport.get_snapshot(snapshot).unwrap(),
        Some(b"encrypted-snapshot".to_vec())
    );
}

#[test]
fn directory_transport_updates_a_device_state_without_creating_a_second_object() {
    let directory = tempdir().unwrap();
    let transport = DirectoryTransport::open(directory.path()).unwrap();
    let device = Uuid::from_u128(2);
    transport.put_device_state(device, b"old").unwrap();
    transport.put_device_state(device, b"new").unwrap();
    assert_eq!(
        transport.get_device_state(device).unwrap(),
        Some(b"new".to_vec())
    );
    assert_eq!(transport.list_device_states().unwrap(), vec![device]);
}

#[test]
fn directory_transport_does_not_replace_an_existing_header_or_metadata_object() {
    let directory = tempdir().unwrap();
    let transport = DirectoryTransport::open(directory.path()).unwrap();
    let first = RemoteHeader::new(Uuid::from_u128(1));
    let second = RemoteHeader::new(Uuid::from_u128(2));
    transport.put_header(&first).unwrap();
    assert!(transport.put_header(&second).is_err());
    assert_eq!(transport.get_header().unwrap(), Some(first));
}

#[test]
fn directory_transport_can_delete_a_published_segment_after_compaction_gate() {
    let directory = tempdir().unwrap();
    let transport = DirectoryTransport::open(directory.path()).unwrap();
    let header = SegmentHeader {
        protocol_version: 1,
        vault_id: Uuid::from_u128(1),
        device_id: Uuid::from_u128(2),
        segment_id: Uuid::from_u128(3),
    };
    transport.put_segment(&header, b"ciphertext").unwrap();
    assert!(transport.delete_segment(&header).unwrap());
    assert!(transport.get_segment(&header).is_err());
}

#[test]
fn directory_transport_lists_published_snapshots_without_pending_objects() {
    let directory = tempdir().unwrap();
    let transport = DirectoryTransport::open(directory.path()).unwrap();
    let snapshot = SnapshotId(Uuid::from_u128(8));
    let manifest = SnapshotManifest {
        snapshot_id: snapshot,
        version_vector: VersionVector::from([(Uuid::from_u128(2), 3)]),
        segments: vec![SnapshotSegment {
            header: SegmentHeader {
                protocol_version: 1,
                vault_id: Uuid::from_u128(1),
                device_id: Uuid::from_u128(2),
                segment_id: Uuid::from_u128(9),
            },
            sequence: 3,
        }],
    };
    transport
        .put_snapshot(snapshot, b"snapshot-ciphertext")
        .unwrap();
    assert_eq!(transport.list_snapshots().unwrap(), vec![snapshot]);
    assert_eq!(
        transport.get_snapshot(snapshot).unwrap(),
        Some(b"snapshot-ciphertext".to_vec())
    );
    assert_eq!(manifest.segments[0].sequence, 3);
    assert!(
        !directory
            .path()
            .join("snapshot-00000000-0000-0000-0000-000000000008.enc.pending")
            .exists()
    );
}
