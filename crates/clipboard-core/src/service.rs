use crate::{
    CoreCommand, CoreError, CoreResponse, IngestText, SearchItem, SearchRequest, SetFavorite,
};
use clipboard_domain::{ClipboardContent, ClipboardItem};
use clipboard_search::SearchEngine;
use clipboard_storage::Database;
use std::path::Path;
use uuid::Uuid;

pub struct CoreService {
    database: Database,
    vault_id: Uuid,
}

impl CoreService {
    pub fn open(data_dir: &Path, vault_id: Uuid, vault_key: &[u8; 32]) -> Result<Self, CoreError> {
        std::fs::create_dir_all(data_dir)?;
        let database = Database::open(&data_dir.join("history.db"), vault_key)?;
        Ok(Self { database, vault_id })
    }

    pub fn execute(&mut self, command: CoreCommand) -> Result<CoreResponse, CoreError> {
        match command {
            CoreCommand::IngestText(request) => self.ingest_text(request),
            CoreCommand::Search(request) => self.search(request),
            CoreCommand::SetFavorite(SetFavorite { .. }) => Err(CoreError::InvalidCommand(
                "favorite mutation is not enabled in the core foundation".into(),
            )),
            CoreCommand::Delete(_) => Err(CoreError::InvalidCommand(
                "delete mutation is not enabled in the core foundation".into(),
            )),
            CoreCommand::ApplyRetention => Err(CoreError::InvalidCommand(
                "retention execution is not enabled in the core foundation".into(),
            )),
        }
    }

    fn ingest_text(&self, request: IngestText) -> Result<CoreResponse, CoreError> {
        let item = ClipboardItem::new(
            Uuid::now_v7(),
            self.vault_id,
            ClipboardContent::Text(request.text),
            request.source_app,
            request.captured_ms,
        );
        self.database.items().insert_and_enqueue(&item)?;
        Ok(CoreResponse::Mutation { item_id: item.id })
    }

    fn search(&self, request: SearchRequest) -> Result<CoreResponse, CoreError> {
        let stored_items = self.database.items().list()?;
        let documents = stored_items.iter().map(|item| {
            let (text, path) = searchable_fields(item);
            (item.id.to_string(), text, path)
        });
        let engine = SearchEngine::from_documents(documents);
        let ids = engine.search(&clipboard_search::SearchQuery::new(
            request.pattern,
            request.mode,
        ))?;
        let items = ids
            .into_iter()
            .filter_map(|id| Uuid::parse_str(&id).ok())
            .filter_map(|id| stored_items.iter().find(|item| item.id == id))
            .map(search_item)
            .collect();
        Ok(CoreResponse::Search { items })
    }
}

fn searchable_fields(item: &ClipboardItem) -> (String, String) {
    match &item.content {
        ClipboardContent::Text(text) => (text.clone(), String::new()),
        ClipboardContent::Image { .. } => (String::new(), String::new()),
        ClipboardContent::FileBundle(bundle) => {
            let paths = bundle
                .entries
                .iter()
                .map(|entry| entry.path.clone())
                .collect::<Vec<_>>()
                .join("\n");
            (paths.clone(), paths)
        }
    }
}

fn search_item(item: &ClipboardItem) -> SearchItem {
    let (kind, preview) = match &item.content {
        ClipboardContent::Text(text) => ("text", text.clone()),
        ClipboardContent::Image { width, height, .. } => {
            ("image", format!("image {width}x{height}"))
        }
        ClipboardContent::FileBundle(bundle) => (
            "file_bundle",
            bundle
                .entries
                .iter()
                .map(|entry| entry.path.as_str())
                .collect::<Vec<_>>()
                .join("\n"),
        ),
    };
    SearchItem {
        id: item.id,
        kind: kind.into(),
        preview,
        source_app: item.source_app.clone(),
        last_used_ms: item.last_used_ms,
    }
}
