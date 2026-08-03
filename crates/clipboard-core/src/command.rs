use crate::CoreError;
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
    ApplyRetention,
}

#[derive(Debug, Deserialize)]
pub struct IngestText {
    pub text: String,
    pub source_app: String,
    pub captured_ms: i64,
}

#[derive(Debug, Deserialize)]
pub struct SearchRequest {
    pub pattern: String,
    pub mode: SearchMode,
}

#[derive(Debug, Deserialize)]
pub struct SetFavorite {
    pub item_id: Uuid,
    pub favorite: bool,
}

#[derive(Debug, Deserialize)]
pub struct DeleteRequest {
    pub item_id: Uuid,
}
