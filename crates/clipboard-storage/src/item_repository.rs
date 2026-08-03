use crate::{Database, StorageError};
use clipboard_domain::{ClipboardContent, ClipboardItem, SyncScope};
use rusqlite::params;

pub struct ItemRepository<'database> {
    database: &'database Database,
}

impl<'database> ItemRepository<'database> {
    pub(crate) fn new(database: &'database Database) -> Self {
        Self { database }
    }

    pub fn insert(&self, item: &ClipboardItem) -> Result<(), StorageError> {
        let kind = match &item.content {
            ClipboardContent::Text(_) => "text",
            ClipboardContent::Image { .. } => "image",
            ClipboardContent::FileBundle(_) => "file_bundle",
        };
        let sync_scope = match item.sync_scope() {
            SyncScope::Vault => "vault",
            SyncScope::LocalOnly => "local_only",
        };
        let content_json = serde_json::to_string(&item.content)?;

        let mut connection = self.database.connection.borrow_mut();
        let transaction = connection.transaction()?;
        transaction.execute(
            "INSERT INTO clipboard_items(
                id, vault_id, kind, sync_scope, source_app, created_ms, last_used_ms, content_json
             ) VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7, ?8)",
            params![
                item.id,
                item.vault_id,
                kind,
                sync_scope,
                item.source_app,
                item.created_ms,
                item.last_used_ms,
                content_json,
            ],
        )?;
        transaction.commit()?;
        Ok(())
    }
}
