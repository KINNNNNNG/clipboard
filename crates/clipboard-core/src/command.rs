use clipboard_search::SearchMode;
use serde::Deserialize;
use uuid::Uuid;

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
