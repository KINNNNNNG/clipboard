use clipboard_domain::{DeleteState, FavoriteState, Hlc};
use proptest::prelude::*;
use uuid::Uuid;

fn favorite_state(value: bool, physical_ms: i64, logical: u32, node: u128) -> FavoriteState {
    FavoriteState {
        value,
        updated: Hlc::new(physical_ms, logical, Uuid::from_u128(node)),
    }
}

fn delete_state(deleted: bool, physical_ms: i64, logical: u32, node: u128) -> DeleteState {
    DeleteState {
        deleted,
        updated: Hlc::new(physical_ms, logical, Uuid::from_u128(node)),
    }
}

proptest! {
    #[test]
    fn favorite_merge_is_commutative(
        a_value in any::<bool>(),
        b_value in any::<bool>(),
        a_time in 0i64..10_000,
        b_time in 0i64..10_000,
        a_logical in 0u32..100,
        b_logical in 0u32..100,
        a_node in any::<u128>(),
        b_node in any::<u128>(),
    ) {
        let a = favorite_state(a_value, a_time, a_logical, a_node);
        let b = favorite_state(b_value, b_time, b_logical, b_node);
        prop_assert_eq!(a.merge(&b), b.merge(&a));
    }

    #[test]
    fn favorite_merge_is_idempotent(
        value in any::<bool>(),
        time in 0i64..10_000,
        logical in 0u32..100,
        node in any::<u128>(),
    ) {
        let state = favorite_state(value, time, logical, node);
        prop_assert_eq!(state.merge(&state), state);
    }

    #[test]
    fn delete_merge_is_commutative(
        a_value in any::<bool>(),
        b_value in any::<bool>(),
        a_time in 0i64..10_000,
        b_time in 0i64..10_000,
        a_logical in 0u32..100,
        b_logical in 0u32..100,
        a_node in any::<u128>(),
        b_node in any::<u128>(),
    ) {
        let a = delete_state(a_value, a_time, a_logical, a_node);
        let b = delete_state(b_value, b_time, b_logical, b_node);
        prop_assert_eq!(a.merge(&b), b.merge(&a));
    }

    #[test]
    fn delete_merge_is_idempotent(
        deleted in any::<bool>(),
        time in 0i64..10_000,
        logical in 0u32..100,
        node in any::<u128>(),
    ) {
        let state = delete_state(deleted, time, logical, node);
        prop_assert_eq!(state.merge(&state), state);
    }
}

#[test]
fn equal_timestamps_resolve_conflicting_values_deterministically() {
    let timestamp = Hlc::new(10, 2, Uuid::from_u128(7));
    let favorite_false = FavoriteState {
        value: false,
        updated: timestamp,
    };
    let favorite_true = FavoriteState {
        value: true,
        updated: timestamp,
    };
    let delete_false = DeleteState {
        deleted: false,
        updated: timestamp,
    };
    let delete_true = DeleteState {
        deleted: true,
        updated: timestamp,
    };

    assert_eq!(favorite_false.merge(&favorite_true), favorite_true);
    assert_eq!(favorite_true.merge(&favorite_false), favorite_true);
    assert_eq!(delete_false.merge(&delete_true), delete_true);
    assert_eq!(delete_true.merge(&delete_false), delete_true);
}
