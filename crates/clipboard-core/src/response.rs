use serde::Serialize;
use uuid::Uuid;

#[derive(Debug, Serialize)]
#[serde(untagged)]
pub enum CoreResponse {
    Mutation {
        item_id: Uuid,
    },
    Search {
        items: Vec<SearchItem>,
    },
    Retention {
        deleted_local: usize,
        tombstones_created: usize,
    },
    Empty {},
}

impl CoreResponse {
    pub fn search_items(&self) -> &[SearchItem] {
        match self {
            Self::Search { items } => items,
            _ => &[],
        }
    }
}

#[derive(Debug, Serialize)]
pub struct SearchItem {
    pub id: Uuid,
    pub kind: String,
    pub preview: String,
    pub source_app: String,
    pub last_used_ms: i64,
    pub favorite: bool,
}
