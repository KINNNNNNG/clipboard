use crate::{Database, OutboxError};
use clipboard_domain::ClipboardItem;
use rusqlite::params;
use uuid::Uuid;

pub struct OutboxEntry {
    pub id: i64,
    pub item_id: Uuid,
    pub event_json: String,
    pub created_ms: i64,
}

pub struct OutboxRepository<'database> {
    database: &'database Database,
}

impl<'database> OutboxRepository<'database> {
    pub(crate) fn new(database: &'database Database) -> Self {
        Self { database }
    }

    pub fn enqueue_item(&self, item: &ClipboardItem) -> Result<(), OutboxError> {
        if !item.is_syncable() {
            return Err(OutboxError::LocalOnly);
        }

        let event_json = serde_json::to_string(item)?;
        self.enqueue_event(item.id, &event_json, item.created_ms)
    }

    pub fn enqueue_event(
        &self,
        item_id: Uuid,
        event_json: &str,
        created_ms: i64,
    ) -> Result<(), OutboxError> {
        self.database.connection.borrow_mut().execute(
            "INSERT INTO sync_outbox(item_id, event_json, created_ms) VALUES (?1, ?2, ?3)",
            params![item_id, event_json, created_ms],
        )?;
        Ok(())
    }

    pub fn pending_count(&self) -> Result<usize, OutboxError> {
        let count = self.database.connection.borrow().query_row(
            "SELECT COUNT(*) FROM sync_outbox",
            [],
            |row| row.get::<_, i64>(0),
        )?;
        Ok(count as usize)
    }

    pub fn pending(&self) -> Result<Vec<OutboxEntry>, OutboxError> {
        let connection = self.database.connection.borrow();
        let mut statement = connection.prepare(
            "SELECT sequence, item_id, event_json, created_ms \
             FROM sync_outbox \
             ORDER BY created_ms ASC, sequence ASC",
        )?;
        let entries = statement
            .query_map([], |row| {
                Ok(OutboxEntry {
                    id: row.get(0)?,
                    item_id: row.get(1)?,
                    event_json: row.get(2)?,
                    created_ms: row.get(3)?,
                })
            })?
            .collect::<Result<Vec<_>, _>>()?;
        Ok(entries)
    }

    pub fn acknowledge(&self, id: i64) -> Result<bool, OutboxError> {
        let removed = self
            .database
            .connection
            .borrow_mut()
            .execute("DELETE FROM sync_outbox WHERE sequence = ?1", params![id])?;
        Ok(removed == 1)
    }
}
