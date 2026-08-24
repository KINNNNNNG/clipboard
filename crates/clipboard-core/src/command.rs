use crate::CoreError;
use clipboard_domain::{FileEntry, Hlc, RetentionPolicy};
use clipboard_search::SearchMode;
use clipboard_sync::RemoteConfig;
use serde::Deserialize;
use uuid::Uuid;

#[derive(Debug, Deserialize)]
pub struct ApiRequest {
    pub api_version: u32,
    #[serde(flatten)]
    pub command: CoreCommand,
}

impl ApiRequest {
    pub fn validate(self) -> Result<CoreCommand, CoreError> {
        if self.api_version != 1 {
            return Err(CoreError::UnsupportedApiVersion(self.api_version));
        }
        Ok(self.command)
    }
}

#[derive(Debug, Deserialize)]
#[serde(tag = "type", content = "payload", rename_all = "snake_case")]
pub enum CoreCommand {
    IngestText(IngestText),
    IngestFileBundle(IngestFileBundle),
    CacheFileBundle(CacheFileBundle),
    UncacheFileBundle(UncacheFileBundle),
    ReadFileBundle(ReadFileBundle),
    SyncDirectory(SyncDirectory),
    SyncRemote(SyncRemote),
    ProbeRemote(ProbeRemote),
    Search(SearchRequest),
    MarkUsed(MarkUsed),
    SetFavorite(SetFavorite),
    Delete(DeleteRequest),
    ClearUnfavorite,
    ApplyRetention(ApplyRetentionRequest),
}

#[derive(Debug, Deserialize)]
pub struct IngestText {
    pub text: String,
    pub source_app: String,
    pub captured_ms: i64,
    #[serde(default)]
    pub source_app_display_name: Option<String>,
}

#[derive(Debug, Deserialize)]
pub struct IngestFileBundle {
    pub entries: Vec<FileEntry>,
    pub source_app: String,
    pub captured_ms: i64,
    #[serde(default)]
    pub source_app_display_name: Option<String>,
}

#[derive(Debug, Deserialize)]
pub struct ReadFileBundle {
    pub item_id: Uuid,
}

#[derive(Debug, Deserialize)]
pub struct CacheFileBundle {
    pub item_id: Uuid,
    pub max_bytes: u64,
}

#[derive(Debug, Deserialize)]
pub struct UncacheFileBundle {
    pub item_id: Uuid,
}

#[derive(Debug, Deserialize)]
pub struct SyncDirectory {
    pub remote_path: String,
    pub device_id: Uuid,
}

#[derive(Debug, Deserialize)]
pub struct SyncRemote {
    pub device_id: Uuid,
    pub remote: RemoteConfig,
}

#[derive(Debug, Deserialize)]
pub struct ProbeRemote {
    pub remote: RemoteConfig,
}

#[derive(Debug, Deserialize)]
pub struct IngestImage {
    pub width: u32,
    pub height: u32,
    pub source_app: String,
    pub captured_ms: i64,
    #[serde(default)]
    pub source_app_display_name: Option<String>,
}

#[derive(Debug, Deserialize)]
pub struct SearchRequest {
    pub pattern: String,
    pub mode: SearchMode,
    #[serde(default)]
    pub filters: SearchFilters,
}

#[derive(Debug, Deserialize)]
pub struct MarkUsed {
    pub item_id: Uuid,
    pub used_ms: i64,
}

#[derive(Debug, Default, Deserialize)]
pub struct SearchFilters {
    pub created_after_ms: Option<i64>,
    pub created_before_ms: Option<i64>,
    #[serde(default)]
    pub source_apps: Vec<String>,
    #[serde(default)]
    pub kinds: Vec<String>,
}

#[derive(Debug, Deserialize)]
pub struct SetFavorite {
    pub item_id: Uuid,
    pub favorite: bool,
    pub updated: Hlc,
}

#[derive(Debug, Deserialize)]
pub struct DeleteRequest {
    pub item_id: Uuid,
    pub updated: Hlc,
}

#[derive(Debug, Deserialize)]
pub struct ApplyRetentionRequest {
    pub now_ms: i64,
    pub policy: RetentionPolicy,
}
