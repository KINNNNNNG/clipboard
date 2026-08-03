use std::collections::HashSet;
use uuid::Uuid;

const DAY_MS: i64 = 86_400_000;

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct RetentionCandidate {
    id: Uuid,
    last_used_ms: i64,
    favorite: bool,
    syncable: bool,
    image_bytes: Option<u64>,
}

impl RetentionCandidate {
    pub const fn new(id: Uuid, last_used_ms: i64, favorite: bool, syncable: bool) -> Self {
        Self {
            id,
            last_used_ms,
            favorite,
            syncable,
            image_bytes: None,
        }
    }

    pub const fn with_image_bytes(mut self, image_bytes: u64) -> Self {
        self.image_bytes = Some(image_bytes);
        self
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct RetentionPolicy {
    pub max_regular_items: Option<usize>,
    pub max_age_days: Option<u32>,
    pub max_image_bytes: Option<u64>,
}

#[derive(Debug, Default, PartialEq, Eq)]
pub struct RetentionPlan {
    pub delete_local: Vec<Uuid>,
    pub create_tombstones: Vec<Uuid>,
}

pub fn plan_retention(
    policy: &RetentionPolicy,
    now_ms: i64,
    candidates: &[RetentionCandidate],
) -> RetentionPlan {
    let mut regular: Vec<_> = candidates
        .iter()
        .copied()
        .filter(|candidate| !candidate.favorite)
        .collect();
    regular.sort_by_key(|candidate| (candidate.last_used_ms, candidate.id));

    let mut selected_ids = HashSet::new();
    let mut selected = Vec::new();

    if let Some(max_age_days) = policy.max_age_days {
        let max_age_ms = i64::from(max_age_days).saturating_mul(DAY_MS);
        let cutoff_ms = now_ms.saturating_sub(max_age_ms);
        for candidate in regular
            .iter()
            .filter(|candidate| candidate.last_used_ms < cutoff_ms)
        {
            select_once(*candidate, &mut selected_ids, &mut selected);
        }
    }

    if let Some(max_image_bytes) = policy.max_image_bytes {
        let mut retained_image_bytes = regular
            .iter()
            .filter(|candidate| !selected_ids.contains(&candidate.id))
            .filter_map(|candidate| candidate.image_bytes)
            .fold(0_u64, u64::saturating_add);

        for candidate in &regular {
            if retained_image_bytes <= max_image_bytes {
                break;
            }
            if selected_ids.contains(&candidate.id) || candidate.image_bytes.is_none() {
                continue;
            }
            retained_image_bytes =
                retained_image_bytes.saturating_sub(candidate.image_bytes.unwrap_or(0));
            select_once(*candidate, &mut selected_ids, &mut selected);
        }
    }

    if let Some(max_regular_items) = policy.max_regular_items {
        let retained_count = regular
            .iter()
            .filter(|candidate| !selected_ids.contains(&candidate.id))
            .count();
        let mut excess = retained_count.saturating_sub(max_regular_items);
        for candidate in &regular {
            if excess == 0 {
                break;
            }
            if selected_ids.contains(&candidate.id) {
                continue;
            }
            select_once(*candidate, &mut selected_ids, &mut selected);
            excess -= 1;
        }
    }

    let mut plan = RetentionPlan::default();
    for candidate in selected {
        if candidate.syncable {
            plan.create_tombstones.push(candidate.id);
        } else {
            plan.delete_local.push(candidate.id);
        }
    }
    plan
}

fn select_once(
    candidate: RetentionCandidate,
    selected_ids: &mut HashSet<Uuid>,
    selected: &mut Vec<RetentionCandidate>,
) {
    if selected_ids.insert(candidate.id) {
        selected.push(candidate);
    }
}
