use clipboard_domain::FileEntry;
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
    FileBundle {
        item_id: Uuid,
        entries: Vec<FileEntry>,
    },
    Retention {
        deleted_local: usize,
        tombstones_created: usize,
    },
    Sync(SyncDirectoryResponse),
    RemoteSync {
        pulled: usize,
        merged: usize,
        uploaded: usize,
        rejected_local_only: usize,
        error_category: Option<String>,
        error_code: Option<String>,
        error_detail: Option<String>,
        error_operation: Option<String>,
    },
    RemoteProbe {
        available: bool,
        error_category: Option<String>,
        error_code: Option<String>,
        error_detail: Option<String>,
    },
    UpdateCheck {
        available: bool,
        current_version: String,
        latest_version: String,
        installer_url: Option<String>,
        checksums_url: Option<String>,
        release_url: Option<String>,
        published_at: Option<String>,
    },
    UpdateDownload {
        version: String,
        installer_path: String,
        size_bytes: u64,
    },
    Empty {},
}

#[derive(Debug, Serialize, PartialEq, Eq)]
pub struct SyncDirectoryResponse {
    pub pulled: usize,
    pub merged: usize,
    pub uploaded: usize,
    pub rejected_local_only: usize,
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
    pub source_app_display_name: Option<String>,
    pub last_used_ms: i64,
    pub favorite: bool,
    pub width: Option<u32>,
    pub height: Option<u32>,
    pub bytes: Option<u64>,
    pub file_count: Option<usize>,
    pub representative_name: Option<String>,
    pub representative_kind: Option<String>,
}
