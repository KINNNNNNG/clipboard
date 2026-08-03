use crate::{Database, StorageError};
use clipboard_domain::{ClipboardContent, ClipboardItem, DeleteState, FavoriteState, SyncScope};
use rusqlite::{Transaction, params};
use uuid::Uuid;

pub struct ItemRepository<'database> {
    database: &'database Database,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct CleanupResult {
    pub deleted_local: usize,
    pub tombstones_created: usize,
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

    pub fn update(&self, item: &ClipboardItem) -> Result<(), StorageError> {
        let mut connection = self.database.connection.borrow_mut();
        let transaction = connection.transaction()?;
        update_item(&transaction, item)?;
        transaction.commit()?;
        Ok(())
    }

    pub fn update_and_enqueue(&self, item: &ClipboardItem) -> Result<(), StorageError> {
        if !item.is_syncable() {
            return Err(StorageError::LocalOnly);
        }
        let event_json = serde_json::to_string(item)?;

        let mut connection = self.database.connection.borrow_mut();
        let transaction = connection.transaction()?;
        update_item(&transaction, item)?;
        transaction.execute(
            "INSERT INTO sync_outbox(item_id, event_json, created_ms) VALUES (?1, ?2, ?3)",
            params![item.id, event_json, event_timestamp(item)],
        )?;
        transaction.commit()?;
        Ok(())
    }

    pub fn delete_local(&self, item_id: Uuid) -> Result<bool, StorageError> {
        let mut connection = self.database.connection.borrow_mut();
        let transaction = connection.transaction()?;
        let deleted = transaction.execute(
            "DELETE FROM clipboard_items WHERE id = ?1 AND sync_scope = 'local_only'",
            params![item_id],
        )?;
        transaction.commit()?;
        Ok(deleted > 0)
    }

    pub fn apply_cleanup(
        &self,
        delete_local: &[Uuid],
        tombstones: &[ClipboardItem],
    ) -> Result<CleanupResult, StorageError> {
        if tombstones.iter().any(|item| !item.is_syncable()) {
            return Err(StorageError::LocalOnly);
        }
        let events = tombstones
            .iter()
            .map(serde_json::to_string)
            .collect::<Result<Vec<_>, _>>()?;

        let mut connection = self.database.connection.borrow_mut();
        let transaction = connection.transaction()?;
        let mut deleted_local = 0;
        for item_id in delete_local {
            deleted_local += transaction.execute(
                "DELETE FROM clipboard_items WHERE id = ?1 AND sync_scope = 'local_only'",
                params![item_id],
            )?;
        }
        for (item, event_json) in tombstones.iter().zip(events) {
            update_item(&transaction, item)?;
            transaction.execute(
                "INSERT INTO sync_outbox(item_id, event_json, created_ms) VALUES (?1, ?2, ?3)",
                params![item.id, event_json, event_timestamp(item)],
            )?;
        }
        transaction.commit()?;

        Ok(CleanupResult {
            deleted_local,
            tombstones_created: tombstones.len(),
        })
    }

    pub fn list(&self) -> Result<Vec<ClipboardItem>, StorageError> {
        let mut items = self.list_all()?;
        items.retain(|item| {
            !item
                .delete_state
                .is_some_and(|delete_state| delete_state.deleted)
        });
        Ok(items)
    }

    pub fn list_all(&self) -> Result<Vec<ClipboardItem>, StorageError> {
        let connection = self.database.connection.borrow();
        let mut statement = connection.prepare(
            "SELECT id, vault_id, source_app, created_ms, last_used_ms, content_json,
                    content_fingerprint, favorite_json, delete_json
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
                    row.get::<_, Option<Vec<u8>>>(6)?,
                    row.get::<_, Option<String>>(7)?,
                    row.get::<_, Option<String>>(8)?,
                ))
            })?
            .collect::<Result<Vec<_>, _>>()?;

        rows.into_iter().map(decode_item).collect()
    }
}

type StoredItemRow = (
    Uuid,
    Uuid,
    String,
    i64,
    i64,
    String,
    Option<Vec<u8>>,
    Option<String>,
    Option<String>,
);

fn decode_item(
    (
        id,
        vault_id,
        source_app,
        created_ms,
        last_used_ms,
        content_json,
        content_fingerprint,
        favorite_json,
        delete_json,
    ): StoredItemRow,
) -> Result<ClipboardItem, StorageError> {
    let content_fingerprint = content_fingerprint
        .map(|fingerprint| {
            fingerprint
                .try_into()
                .map_err(|_| StorageError::InvalidFingerprintLength)
        })
        .transpose()?;
    let favorite_state = favorite_json
        .map(|json| serde_json::from_str::<FavoriteState>(&json))
        .transpose()?;
    let delete_state = delete_json
        .map(|json| serde_json::from_str::<DeleteState>(&json))
        .transpose()?;

    Ok(ClipboardItem {
        id,
        vault_id,
        content: serde_json::from_str(&content_json)?,
        source_app,
        created_ms,
        last_used_ms,
        content_fingerprint,
        favorite_state,
        delete_state,
    })
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
    let content_fingerprint = item
        .content_fingerprint
        .as_ref()
        .map(|fingerprint| fingerprint.as_slice());
    let favorite_json = item
        .favorite_state
        .as_ref()
        .map(serde_json::to_string)
        .transpose()?;
    let delete_json = item
        .delete_state
        .as_ref()
        .map(serde_json::to_string)
        .transpose()?;

    transaction.execute(
        "INSERT INTO clipboard_items(
            id, vault_id, kind, sync_scope, source_app, created_ms, last_used_ms, content_json,
            content_fingerprint, favorite_json, delete_json
         ) VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7, ?8, ?9, ?10, ?11)",
        params![
            item.id,
            item.vault_id,
            kind,
            sync_scope,
            item.source_app,
            item.created_ms,
            item.last_used_ms,
            content_json,
            content_fingerprint,
            favorite_json,
            delete_json,
        ],
    )?;
    Ok(())
}

fn update_item(transaction: &Transaction<'_>, item: &ClipboardItem) -> Result<(), StorageError> {
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
    let content_fingerprint = item
        .content_fingerprint
        .as_ref()
        .map(|fingerprint| fingerprint.as_slice());
    let favorite_json = item
        .favorite_state
        .as_ref()
        .map(serde_json::to_string)
        .transpose()?;
    let delete_json = item
        .delete_state
        .as_ref()
        .map(serde_json::to_string)
        .transpose()?;

    let updated = transaction.execute(
        "UPDATE clipboard_items
         SET vault_id = ?2, kind = ?3, sync_scope = ?4, source_app = ?5, created_ms = ?6,
             last_used_ms = ?7, content_json = ?8, content_fingerprint = ?9,
             favorite_json = ?10, delete_json = ?11
         WHERE id = ?1",
        params![
            item.id,
            item.vault_id,
            kind,
            sync_scope,
            item.source_app,
            item.created_ms,
            item.last_used_ms,
            content_json,
            content_fingerprint,
            favorite_json,
            delete_json,
        ],
    )?;
    if updated == 0 {
        return Err(StorageError::ItemNotFound(item.id));
    }
    Ok(())
}

fn event_timestamp(item: &ClipboardItem) -> i64 {
    let favorite_ms = item
        .favorite_state
        .map_or(i64::MIN, |state| state.updated.physical_ms);
    let delete_ms = item
        .delete_state
        .map_or(i64::MIN, |state| state.updated.physical_ms);
    item.last_used_ms.max(favorite_ms).max(delete_ms)
}
