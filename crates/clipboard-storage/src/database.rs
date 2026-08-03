use crate::{ItemRepository, OutboxRepository, StorageError};
use rusqlite::{Connection, OptionalExtension, params};
use std::{cell::RefCell, path::Path};

const INITIAL_SCHEMA_VERSION: i64 = 1;

pub struct Database {
    pub(crate) connection: RefCell<Connection>,
    cipher_version: String,
}

impl Database {
    pub fn open(path: &Path, key: &[u8; 32]) -> Result<Self, StorageError> {
        Self::initialize(Connection::open(path)?, key)
    }

    pub fn open_in_memory(key: &[u8; 32]) -> Result<Self, StorageError> {
        Self::initialize(Connection::open_in_memory()?, key)
    }

    pub fn cipher_version(&self) -> &str {
        &self.cipher_version
    }

    pub fn items(&self) -> ItemRepository<'_> {
        ItemRepository::new(self)
    }

    pub fn outbox(&self) -> OutboxRepository<'_> {
        OutboxRepository::new(self)
    }

    fn initialize(mut connection: Connection, key: &[u8; 32]) -> Result<Self, StorageError> {
        let encoded_key = format!("x'{}'", hex::encode(key));
        connection.pragma_update(None, "key", encoded_key)?;

        let cipher_version = connection
            .query_row("PRAGMA cipher_version", [], |row| row.get::<_, String>(0))
            .optional()?
            .filter(|version| !version.is_empty())
            .ok_or(StorageError::CipherUnavailable)?;

        connection.execute_batch("PRAGMA foreign_keys = ON;")?;
        Self::migrate(&mut connection)?;

        Ok(Self {
            connection: RefCell::new(connection),
            cipher_version,
        })
    }

    fn migrate(connection: &mut Connection) -> Result<(), StorageError> {
        let has_migration_table: bool = connection.query_row(
            "SELECT EXISTS(SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = 'schema_migrations')",
            [],
            |row| row.get(0),
        )?;
        if has_migration_table {
            return Ok(());
        }

        let transaction = connection.transaction()?;
        transaction.execute_batch(include_str!("../migrations/001_initial.sql"))?;
        transaction.execute(
            "INSERT INTO schema_migrations(version, applied_at_ms) VALUES (?1, CAST(strftime('%s', 'now') AS INTEGER) * 1000)",
            params![INITIAL_SCHEMA_VERSION],
        )?;
        transaction.commit()?;
        Ok(())
    }
}
