use serde::{Deserialize, Serialize};
use uuid::Uuid;

#[derive(Debug, Clone, Copy, PartialEq, Eq, PartialOrd, Ord, Serialize, Deserialize)]
pub struct Hlc {
    pub physical_ms: i64,
    pub logical: u32,
    pub node_id: Uuid,
}

impl Hlc {
    pub const fn new(physical_ms: i64, logical: u32, node_id: Uuid) -> Self {
        Self {
            physical_ms,
            logical,
            node_id,
        }
    }
}
