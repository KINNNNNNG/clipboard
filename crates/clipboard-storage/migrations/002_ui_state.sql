ALTER TABLE clipboard_items
ADD COLUMN content_fingerprint BLOB CHECK(
    content_fingerprint IS NULL OR length(content_fingerprint) = 32
);

CREATE INDEX clipboard_items_fingerprint_recent
ON clipboard_items(vault_id, content_fingerprint, last_used_ms DESC)
WHERE content_fingerprint IS NOT NULL;
