use crate::{
    ApplyRetentionRequest, CoreCommand, CoreError, CoreResponse, DeleteRequest, IngestText,
    SearchFilters, SearchItem, SearchRequest, SetFavorite,
};
use crate::{IngestImage, object_store::ObjectStore};
use clipboard_crypto::{KeyPurpose, VaultKey};
use clipboard_domain::{
    ClipboardContent, ClipboardItem, DeleteState, FavoriteState, Hlc, RetentionCandidate,
    plan_retention,
};
use clipboard_search::SearchEngine;
use clipboard_storage::Database;
use std::{path::Path, time::SystemTime};
use uuid::Uuid;

pub const MAX_IMAGE_BYTES: usize = 50 * 1024 * 1024;

pub struct CoreService {
    database: Database,
    vault_id: Uuid,
    vault_key: VaultKey,
    object_store: ObjectStore,
}

impl CoreService {
    pub fn open(data_dir: &Path, vault_id: Uuid, vault_key: &[u8; 32]) -> Result<Self, CoreError> {
        std::fs::create_dir_all(data_dir)?;
        let database = Database::open(&data_dir.join("history.db"), vault_key)?;
        let vault_key = VaultKey::from_bytes(*vault_key);
        let image_key = vault_key.derive(vault_id, KeyPurpose::Image)?;
        let object_store = ObjectStore::open(data_dir, vault_id, image_key)?;
        Ok(Self {
            database,
            vault_id,
            vault_key,
            object_store,
        })
    }

    pub fn execute(&mut self, command: CoreCommand) -> Result<CoreResponse, CoreError> {
        match command {
            CoreCommand::IngestText(request) => self.ingest_text(request),
            CoreCommand::Search(request) => self.search(request),
            CoreCommand::SetFavorite(request) => self.set_favorite(request),
            CoreCommand::Delete(request) => self.delete(request),
            CoreCommand::ClearUnfavorite => self.clear_unfavorite(),
            CoreCommand::ApplyRetention(request) => self.apply_retention(request),
        }
    }

    fn ingest_text(&self, request: IngestText) -> Result<CoreResponse, CoreError> {
        let fingerprint = self
            .vault_key
            .derive(self.vault_id, KeyPurpose::Fingerprint)?
            .keyed_hash(request.text.as_bytes());
        let mut stored_items = self.database.items().list()?;
        if let Some(latest) = stored_items
            .iter_mut()
            .find(|item| item.vault_id == self.vault_id)
            && let ClipboardContent::Text(latest_text) = &latest.content
            && (latest.content_fingerprint == Some(fingerprint)
                || (latest.content_fingerprint.is_none() && latest_text == &request.text))
        {
            latest.content_fingerprint = Some(fingerprint);
            if request.captured_ms >= latest.last_used_ms {
                latest.last_used_ms = request.captured_ms;
                latest.source_app = request.source_app;
            }
            self.database.items().update_and_enqueue(latest)?;
            return Ok(CoreResponse::Mutation { item_id: latest.id });
        }

        let mut item = ClipboardItem::new(
            Uuid::now_v7(),
            self.vault_id,
            ClipboardContent::Text(request.text),
            request.source_app,
            request.captured_ms,
        );
        item.content_fingerprint = Some(fingerprint);
        self.database.items().insert_and_enqueue(&item)?;
        Ok(CoreResponse::Mutation { item_id: item.id })
    }

    pub fn ingest_image(
        &self,
        request: IngestImage,
        png: &[u8],
    ) -> Result<CoreResponse, CoreError> {
        if png.is_empty() || request.width == 0 || request.height == 0 {
            return Err(CoreError::InvalidImage);
        }
        if png.len() > MAX_IMAGE_BYTES {
            return Err(CoreError::ImageTooLarge {
                actual: png.len(),
                maximum: MAX_IMAGE_BYTES,
            });
        }

        let fingerprint = self
            .vault_key
            .derive(self.vault_id, KeyPurpose::Fingerprint)?
            .keyed_hash(png);
        let mut stored_items = self.database.items().list()?;
        if let Some(latest) = stored_items
            .iter_mut()
            .find(|item| item.vault_id == self.vault_id)
            && matches!(&latest.content, ClipboardContent::Image { .. })
            && latest.content_fingerprint == Some(fingerprint)
        {
            if request.captured_ms >= latest.last_used_ms {
                latest.last_used_ms = request.captured_ms;
                latest.source_app = request.source_app;
            }
            self.database.items().update_and_enqueue(latest)?;
            return Ok(CoreResponse::Mutation { item_id: latest.id });
        }

        let object_id = Uuid::now_v7();
        let mut item = ClipboardItem::new(
            Uuid::now_v7(),
            self.vault_id,
            ClipboardContent::Image {
                object_id,
                width: request.width,
                height: request.height,
                bytes: png.len() as u64,
            },
            request.source_app,
            request.captured_ms,
        );
        item.content_fingerprint = Some(fingerprint);
        self.object_store.store_image(object_id, png)?;
        if let Err(error) = self.database.items().insert_and_enqueue(&item) {
            let _ = self.object_store.remove_image(object_id);
            return Err(error.into());
        }
        Ok(CoreResponse::Mutation { item_id: item.id })
    }

    pub fn read_image(&self, item_id: Uuid) -> Result<Vec<u8>, CoreError> {
        let item = self.find_item(item_id)?;
        let ClipboardContent::Image { object_id, .. } = item.content else {
            return Err(CoreError::NotImage(item_id));
        };
        self.object_store.read_image(object_id)
    }

    fn search(&self, request: SearchRequest) -> Result<CoreResponse, CoreError> {
        let stored_items = self
            .database
            .items()
            .list()?
            .into_iter()
            .filter(|item| item.vault_id == self.vault_id)
            .filter(|item| matches_filters(item, &request.filters))
            .collect::<Vec<_>>();
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

    fn set_favorite(&self, request: SetFavorite) -> Result<CoreResponse, CoreError> {
        let mut item = self.find_item(request.item_id)?;
        let requested = FavoriteState {
            value: request.favorite,
            updated: request.updated,
        };
        let merged = item
            .favorite_state
            .map_or(requested, |existing| existing.merge(&requested));
        if item.favorite_state != Some(merged) {
            item.favorite_state = Some(merged);
            self.persist_update(&item)?;
        }
        Ok(CoreResponse::Mutation { item_id: item.id })
    }

    fn delete(&self, request: DeleteRequest) -> Result<CoreResponse, CoreError> {
        let mut item = self.find_item(request.item_id)?;
        if !item.is_syncable() {
            self.database.items().delete_local(item.id)?;
            self.remove_image_objects(std::slice::from_ref(&item))?;
            return Ok(CoreResponse::Mutation { item_id: item.id });
        }

        let requested = DeleteState {
            deleted: true,
            updated: request.updated,
        };
        let merged = item
            .delete_state
            .map_or(requested, |existing| existing.merge(&requested));
        if item.delete_state != Some(merged) {
            item.delete_state = Some(merged);
            self.database.items().update_and_enqueue(&item)?;
            self.remove_image_objects(std::slice::from_ref(&item))?;
        }
        Ok(CoreResponse::Mutation { item_id: item.id })
    }

    fn clear_unfavorite(&self) -> Result<CoreResponse, CoreError> {
        let items = self
            .database
            .items()
            .list()?
            .into_iter()
            .filter(|item| item.vault_id == self.vault_id)
            .filter(|item| !is_favorite(item))
            .collect::<Vec<_>>();
        let now_ms = unix_time_ms();
        let mut delete_local = Vec::new();
        let mut tombstones = Vec::new();

        for item in &items {
            let mut item = item.clone();
            if item.is_syncable() {
                item.delete_state = Some(DeleteState {
                    deleted: true,
                    updated: next_delete_hlc(item.delete_state, now_ms, self.vault_id),
                });
                tombstones.push(item);
            } else {
                delete_local.push(item.id);
            }
        }
        let result = self
            .database
            .items()
            .apply_cleanup(&delete_local, &tombstones)?;
        self.remove_image_objects(&items)?;

        Ok(CoreResponse::Retention {
            deleted_local: result.deleted_local,
            tombstones_created: result.tombstones_created,
        })
    }

    fn apply_retention(&self, request: ApplyRetentionRequest) -> Result<CoreResponse, CoreError> {
        let items = self
            .database
            .items()
            .list()?
            .into_iter()
            .filter(|item| item.vault_id == self.vault_id)
            .collect::<Vec<_>>();
        let candidates = items
            .iter()
            .map(|item| {
                let candidate = RetentionCandidate::new(
                    item.id,
                    item.last_used_ms,
                    is_favorite(item),
                    item.is_syncable(),
                );
                match item.content {
                    ClipboardContent::Image { bytes, .. } => candidate.with_image_bytes(bytes),
                    ClipboardContent::Text(_) | ClipboardContent::FileBundle(_) => candidate,
                }
            })
            .collect::<Vec<_>>();
        let plan = plan_retention(&request.policy, request.now_ms, &candidates);
        let removed_ids = plan
            .delete_local
            .iter()
            .chain(&plan.create_tombstones)
            .copied()
            .collect::<std::collections::HashSet<_>>();
        let removed_items = items
            .iter()
            .filter(|item| removed_ids.contains(&item.id))
            .cloned()
            .collect::<Vec<_>>();

        let mut tombstones = Vec::new();
        for item_id in plan.create_tombstones {
            let Some(mut item) = items.iter().find(|item| item.id == item_id).cloned() else {
                continue;
            };
            item.delete_state = Some(DeleteState {
                deleted: true,
                updated: next_delete_hlc(item.delete_state, request.now_ms, self.vault_id),
            });
            tombstones.push(item);
        }
        let result = self
            .database
            .items()
            .apply_cleanup(&plan.delete_local, &tombstones)?;
        self.remove_image_objects(&removed_items)?;

        Ok(CoreResponse::Retention {
            deleted_local: result.deleted_local,
            tombstones_created: result.tombstones_created,
        })
    }

    fn find_item(&self, item_id: Uuid) -> Result<ClipboardItem, CoreError> {
        self.database
            .items()
            .list_all()?
            .into_iter()
            .find(|item| item.id == item_id && item.vault_id == self.vault_id)
            .ok_or(CoreError::ItemNotFound(item_id))
    }

    fn persist_update(&self, item: &ClipboardItem) -> Result<(), CoreError> {
        if item.is_syncable() {
            self.database.items().update_and_enqueue(item)?;
        } else {
            self.database.items().update(item)?;
        }
        Ok(())
    }

    fn remove_image_objects(&self, items: &[ClipboardItem]) -> Result<(), CoreError> {
        for item in items {
            if let ClipboardContent::Image { object_id, .. } = item.content {
                self.object_store.remove_image(object_id)?;
            }
        }
        Ok(())
    }
}

fn matches_filters(item: &ClipboardItem, filters: &SearchFilters) -> bool {
    if filters
        .created_after_ms
        .is_some_and(|after_ms| item.created_ms < after_ms)
        || filters
            .created_before_ms
            .is_some_and(|before_ms| item.created_ms > before_ms)
    {
        return false;
    }
    if !filters.source_apps.is_empty()
        && !filters
            .source_apps
            .iter()
            .any(|source_app| source_app.eq_ignore_ascii_case(&item.source_app))
    {
        return false;
    }
    filters.kinds.is_empty()
        || filters
            .kinds
            .iter()
            .any(|kind| kind.eq_ignore_ascii_case(item_kind(item)))
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

fn item_kind(item: &ClipboardItem) -> &'static str {
    match item.content {
        ClipboardContent::Text(_) => "text",
        ClipboardContent::Image { .. } => "image",
        ClipboardContent::FileBundle(_) => "file_bundle",
    }
}

fn search_item(item: &ClipboardItem) -> SearchItem {
    let (kind, preview, width, height, bytes) = match &item.content {
        ClipboardContent::Text(text) => ("text", text.clone(), None, None, None),
        ClipboardContent::Image {
            width,
            height,
            bytes,
            ..
        } => (
            "image",
            format!("image {width}x{height}"),
            Some(*width),
            Some(*height),
            Some(*bytes),
        ),
        ClipboardContent::FileBundle(bundle) => (
            "file_bundle",
            bundle
                .entries
                .iter()
                .map(|entry| entry.path.as_str())
                .collect::<Vec<_>>()
                .join("\n"),
            None,
            None,
            None,
        ),
    };
    SearchItem {
        id: item.id,
        kind: kind.into(),
        preview,
        source_app: item.source_app.clone(),
        last_used_ms: item.last_used_ms,
        favorite: is_favorite(item),
        width,
        height,
        bytes,
    }
}

fn is_favorite(item: &ClipboardItem) -> bool {
    item.favorite_state.is_some_and(|state| state.value)
}

fn next_delete_hlc(existing: Option<DeleteState>, now_ms: i64, node_id: Uuid) -> Hlc {
    match existing {
        Some(state) if state.updated.physical_ms >= now_ms => Hlc::new(
            state.updated.physical_ms,
            state.updated.logical.saturating_add(1),
            node_id,
        ),
        _ => Hlc::new(now_ms, 0, node_id),
    }
}

fn unix_time_ms() -> i64 {
    SystemTime::UNIX_EPOCH
        .elapsed()
        .map(|duration| i64::try_from(duration.as_millis()).unwrap_or(i64::MAX))
        .unwrap_or(0)
}
