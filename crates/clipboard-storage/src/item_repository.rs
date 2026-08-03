use crate::{Database, StorageError};
use clipboard_domain::{ClipboardContent, ClipboardItem, SyncScope};
use rusqlite::{Transaction, params};
use uuid::Uuid;

pub struct ItemRepository<'database> {
    database: &'database Database,
}

impl<'database> ItemRepository<'database> {
    pub(crate) fn new(database: &'database Database) -> Self {
        Self { database }
    }

    pub fn insert(&self, item: &ClipboardItem) -> Result<(), StorageError> {
        let mut connection = self.database.connection.borrow_mut();
        let transaction = connection.transaction()?;
        insert_item(&transaction, item)?;
        transaction.commit()?;
        Ok(())
    }

    pub fn insert_and_enqueue(&self, item: &ClipboardItem) -> Result<(), StorageError> {
        if !item.is_syncable() {
            return Err(StorageError::LocalOnly);
        }
        let event_json = serde_json::to_string(item)?;

        let mut connection = self.database.connection.borrow_mut();
        let transaction = connection.transaction()?;
        insert_item(&transaction, item)?;
        transaction.execute(
            "INSERT INTO sync_outbox(item_id, event_json, created_ms) VALUES (?1, ?2, ?3)",
            params![item.id, event_json, item.created_ms],
        )?;
        transaction.commit()?;
        Ok(())
    }

    pub fn list(&self) -> Result<Vec<ClipboardItem>, StorageError> {
        let connection = self.database.connection.borrow();
        let mut statement = connection.prepare(
            "SELECT id, vault_id, source_app, created_ms, last_used_ms, content_json
             FROM clipboard_items
             ORDER BY last_used_ms DESC, id DESC",
        )?;
        let rows = statement
            .query_map([], |row| {
                Ok((
                    row.get::<_, Uuid>(0)?,
                    row.get::<_, Uuid>(1)?,
                    row.get::<_, String>(2)?,
                    row.get::<_, i64>(3)?,
                    row.get::<_, i64>(4)?,
                    row.get::<_, String>(5)?,
                ))
            })?
            .collect::<Result<Vec<_>, _>>()?;

        rows.into_iter()
            .map(
                |(id, vault_id, source_app, created_ms, last_used_ms, content_json)| {
                    Ok(ClipboardItem {
                        id,
                        vault_id,
                        content: serde_json::from_str(&content_json)?,
                        source_app,
                        created_ms,
                        last_used_ms,
                    })
                },
            )
            .collect()
    }
}

fn insert_item(transaction: &Transaction<'_>, item: &ClipboardItem) -> Result<(), StorageError> {
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
    Ok(())
}
