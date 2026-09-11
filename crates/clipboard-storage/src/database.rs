use crate::{ItemRepository, OutboxRepository, StorageError, vault_marker};
use rusqlite::{Connection, OptionalExtension, params};
use std::{cell::RefCell, path::Path};
use uuid::Uuid;

const INITIAL_SCHEMA_VERSION: i64 = 1;
const MIGRATIONS: &[(i64, &str)] = &[
    (2, include_str!("../migrations/002_ui_state.sql")),
    (
        3,
        include_str!("../migrations/003_source_app_display_name.sql"),
    ),
];
const LATEST_SCHEMA_VERSION: i64 = 3;
const HISTORY_FILE_NAME: &str = "history.db";

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

    /// Opens the encrypted history database that belongs to `vault_id` inside `data_dir`.
    ///
    /// A vault marker that names another vault is rejected before the database is opened, so a
    /// foreign key never rewrites or clears existing history. When the marker matches the requested
    /// vault, an unreadable database is damage rather than a key mismatch, because a wrong vault is
    /// already ruled out by the marker.
    pub fn open_vault(
        data_dir: &Path,
        vault_id: Uuid,
        key: &[u8; 32],
    ) -> Result<Self, StorageError> {
        let marker = vault_marker::read(data_dir)?;
        if let Some(existing) = marker
            && existing != vault_id
        {
            return Err(StorageError::VaultMismatch);
        }
        let database = match Self::open(&data_dir.join(HISTORY_FILE_NAME), key) {
            Ok(database) => database,
            Err(StorageError::Unreadable) if marker.is_some() => return Err(StorageError::Corrupt),
            Err(error) => return Err(error),
        };
        if marker.is_none() {
            let _ = vault_marker::write_if_absent(data_dir, vault_id);
        }
        Ok(database)
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
        if !has_migration_table {
            let transaction = connection.transaction()?;
            transaction.execute_batch(include_str!("../migrations/001_initial.sql"))?;
            transaction.execute(
                "INSERT INTO schema_migrations(version, applied_at_ms) VALUES (?1, CAST(strftime('%s', 'now') AS INTEGER) * 1000)",
                params![INITIAL_SCHEMA_VERSION],
            )?;
            transaction.commit()?;
        }

        let applied_versions = {
            let mut statement =
                connection.prepare("SELECT version FROM schema_migrations ORDER BY version")?;
            statement
                .query_map([], |row| row.get::<_, i64>(0))?
                .collect::<Result<Vec<_>, _>>()?
        };
        if let Some(&found) = applied_versions.last()
            && found > LATEST_SCHEMA_VERSION
        {
            return Err(StorageError::UnsupportedSchemaVersion {
                found,
                supported: LATEST_SCHEMA_VERSION,
            });
        }
        if applied_versions.is_empty()
            || applied_versions
                .iter()
                .copied()
                .ne(1..=i64::try_from(applied_versions.len())
                    .map_err(|_| StorageError::InvalidMigrationHistory)?)
        {
            return Err(StorageError::InvalidMigrationHistory);
        }

        let mut current_version = *applied_versions
            .last()
            .ok_or(StorageError::InvalidMigrationHistory)?;
        for &(version, sql) in MIGRATIONS {
            if version <= current_version {
                continue;
            }
            let transaction = connection.transaction()?;
            transaction.execute_batch(sql)?;
            transaction.execute(
                "INSERT INTO schema_migrations(version, applied_at_ms) VALUES (?1, CAST(strftime('%s', 'now') AS INTEGER) * 1000)",
                params![version],
            )?;
            transaction.commit()?;
            current_version = version;
        }
        Ok(())
    }
}
