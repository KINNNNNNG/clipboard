PRAGMA foreign_keys = ON;

CREATE TABLE schema_migrations (
    version INTEGER PRIMARY KEY,
    applied_at_ms INTEGER NOT NULL
);

CREATE TABLE clipboard_items (
    id BLOB PRIMARY KEY CHECK(length(id) = 16),
    vault_id BLOB NOT NULL CHECK(length(vault_id) = 16),
    kind TEXT NOT NULL CHECK(kind IN ('text', 'image', 'file_bundle')),
    sync_scope TEXT NOT NULL CHECK(sync_scope IN ('vault', 'local_only')),
    source_app TEXT NOT NULL,
    created_ms INTEGER NOT NULL,
    last_used_ms INTEGER NOT NULL,
    content_json TEXT NOT NULL,
    favorite_json TEXT,
    delete_json TEXT
);

CREATE TABLE sync_outbox (
    sequence INTEGER PRIMARY KEY AUTOINCREMENT,
    item_id BLOB NOT NULL REFERENCES clipboard_items(id) ON DELETE CASCADE,
    event_json TEXT NOT NULL,
    created_ms INTEGER NOT NULL
);

CREATE TRIGGER reject_local_only_outbox
BEFORE INSERT ON sync_outbox
WHEN (SELECT sync_scope FROM clipboard_items WHERE id = NEW.item_id) <> 'vault'
BEGIN
    SELECT RAISE(ABORT, 'local_only item cannot enter sync outbox');
END;
