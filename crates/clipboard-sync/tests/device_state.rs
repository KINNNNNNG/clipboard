use clipboard_sync::{DeviceRegistry, SnapshotId, VersionVector};
use uuid::Uuid;

#[test]
fn snapshot_is_compactable_only_after_every_active_device_acknowledges_it() {
    let first = Uuid::from_u128(1);
    let second = Uuid::from_u128(2);
    let snapshot = SnapshotId(Uuid::from_u128(3));
    let mut registry = DeviceRegistry::new(first);
    registry.register_active(second);

    let vector = VersionVector::from([(first, 4), (second, 2)]);
    registry.record_ack(first, snapshot, vector.clone());
    assert!(!registry.can_compact(snapshot, &vector));

    registry.record_ack(second, snapshot, vector.clone());
    assert!(registry.can_compact(snapshot, &vector));
}

#[test]
fn deactivated_device_no_longer_blocks_compaction_but_is_not_erased_from_history() {
    let first = Uuid::from_u128(1);
    let second = Uuid::from_u128(2);
    let snapshot = SnapshotId(Uuid::from_u128(4));
    let mut registry = DeviceRegistry::new(first);
    registry.register_active(second);
    let vector = VersionVector::from([(first, 1), (second, 8)]);

    registry.record_ack(first, snapshot, vector.clone());
    registry.deactivate(second).unwrap();

    assert!(registry.can_compact(snapshot, &vector));
    assert!(registry.is_known(second));
    assert!(!registry.is_active(second));
}

#[test]
fn acknowledgements_must_cover_the_snapshot_vector() {
    let device = Uuid::from_u128(1);
    let snapshot = SnapshotId(Uuid::from_u128(5));
    let mut registry = DeviceRegistry::new(device);
    let required = VersionVector::from([(device, 3)]);
    registry.record_ack(device, snapshot, VersionVector::from([(device, 2)]));

    assert!(!registry.can_compact(snapshot, &required));
}

#[test]
fn version_vector_serializes_and_increments_per_device() {
    let device = Uuid::from_u128(7);
    let mut vector = VersionVector::default();
    assert_eq!(vector.increment(device), 1);
    assert_eq!(vector.increment(device), 2);
    let encoded = serde_json::to_vec(&vector).unwrap();
    let decoded: VersionVector = serde_json::from_slice(&encoded).unwrap();
    assert_eq!(decoded.get(device), 2);
}
