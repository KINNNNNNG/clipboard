use clipboard_domain::{RetentionCandidate, RetentionPolicy, plan_retention};
use uuid::Uuid;

const DAY_MS: i64 = 86_400_000;

fn id(value: u128) -> Uuid {
    Uuid::from_u128(value)
}

#[test]
fn favorites_are_never_auto_deleted_and_oldest_regular_items_are_removed() {
    let favorite = RetentionCandidate::new(id(1), 1, true, true);
    let oldest = RetentionCandidate::new(id(2), 2, false, false);
    let newest = RetentionCandidate::new(id(3), 3, false, true);
    let policy = RetentionPolicy {
        max_regular_items: Some(1),
        max_age_days: None,
        max_image_bytes: None,
    };

    let plan = plan_retention(&policy, 100, &[favorite, oldest, newest]);
    assert_eq!(plan.delete_local, vec![id(2)]);
    assert!(plan.create_tombstones.is_empty());
}

#[test]
fn age_expiration_routes_syncable_items_to_tombstones_and_files_to_local_deletion() {
    let now = 10 * DAY_MS;
    let syncable = RetentionCandidate::new(id(1), now - 3 * DAY_MS, false, true);
    let local_file = RetentionCandidate::new(id(2), now - 2 * DAY_MS, false, false);
    let favorite = RetentionCandidate::new(id(3), 0, true, true);
    let policy = RetentionPolicy {
        max_regular_items: None,
        max_age_days: Some(1),
        max_image_bytes: None,
    };

    let plan = plan_retention(&policy, now, &[favorite, local_file, syncable]);
    assert_eq!(plan.delete_local, vec![id(2)]);
    assert_eq!(plan.create_tombstones, vec![id(1)]);
}

#[test]
fn image_space_limit_removes_oldest_regular_images_only() {
    let favorite_image = RetentionCandidate::new(id(1), 1, true, true).with_image_bytes(1_000);
    let oldest_image = RetentionCandidate::new(id(2), 2, false, true).with_image_bytes(60);
    let newest_image = RetentionCandidate::new(id(3), 3, false, true).with_image_bytes(60);
    let text = RetentionCandidate::new(id(4), 0, false, true);
    let policy = RetentionPolicy {
        max_regular_items: None,
        max_age_days: None,
        max_image_bytes: Some(60),
    };

    let plan = plan_retention(
        &policy,
        100,
        &[favorite_image, newest_image, text, oldest_image],
    );
    assert!(plan.delete_local.is_empty());
    assert_eq!(plan.create_tombstones, vec![id(2)]);
}

#[test]
fn candidates_selected_by_multiple_limits_appear_only_once() {
    let old_image = RetentionCandidate::new(id(1), 0, false, true).with_image_bytes(u64::MAX);
    let policy = RetentionPolicy {
        max_regular_items: Some(0),
        max_age_days: Some(1),
        max_image_bytes: Some(0),
    };

    let plan = plan_retention(&policy, 2 * DAY_MS, &[old_image]);
    assert!(plan.delete_local.is_empty());
    assert_eq!(plan.create_tombstones, vec![id(1)]);
}
