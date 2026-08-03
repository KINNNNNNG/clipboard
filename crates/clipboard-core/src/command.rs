use crate::CoreError;
use clipboard_domain::{Hlc, RetentionPolicy};
use clipboard_search::SearchMode;
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
    Search(SearchRequest),
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
}

#[derive(Debug, Deserialize)]
pub struct IngestImage {
    pub width: u32,
    pub height: u32,
    pub source_app: String,
    pub captured_ms: i64,
}

#[derive(Debug, Deserialize)]
pub struct SearchRequest {
    pub pattern: String,
    pub mode: SearchMode,
    #[serde(default)]
    pub filters: SearchFilters,
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
