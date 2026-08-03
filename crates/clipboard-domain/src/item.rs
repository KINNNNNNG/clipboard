use crate::{DeleteState, FavoriteState, FileBundle};
use serde::{Deserialize, Serialize};
use uuid::Uuid;

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
pub enum SyncScope {
    Vault,
    LocalOnly,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub enum ClipboardContent {
    Text(String),
    Image {
        object_id: Uuid,
        width: u32,
        height: u32,
        bytes: u64,
    },
    FileBundle(FileBundle),
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct ClipboardItem {
    pub id: Uuid,
    pub vault_id: Uuid,
    pub content: ClipboardContent,
    pub source_app: String,
    pub created_ms: i64,
    pub last_used_ms: i64,
    pub content_fingerprint: Option<[u8; 32]>,
    pub favorite_state: Option<FavoriteState>,
    pub delete_state: Option<DeleteState>,
}

impl ClipboardItem {
    pub fn new(
        id: Uuid,
        vault_id: Uuid,
        content: ClipboardContent,
        source_app: String,
        created_ms: i64,
    ) -> Self {
        Self {
            id,
            vault_id,
            content,
            source_app,
            created_ms,
            last_used_ms: created_ms,
            content_fingerprint: None,
            favorite_state: None,
            delete_state: None,
        }
    }

    pub fn sync_scope(&self) -> SyncScope {
        match self.content {
            ClipboardContent::FileBundle(_) => SyncScope::LocalOnly,
            ClipboardContent::Text(_) | ClipboardContent::Image { .. } => SyncScope::Vault,
        }
    }

    pub fn is_syncable(&self) -> bool {
        self.sync_scope() == SyncScope::Vault
    }
}
