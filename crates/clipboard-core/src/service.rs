use crate::file_cache::FileCache;
use crate::{
    ApplyRetentionRequest, CacheFileBundle, CoreCommand, CoreError, CoreResponse, DeleteRequest,
    IngestFileBundle, IngestText, ProbeRemote, ReadFileBundle, SearchFilters, SearchItem,
    SearchRequest, SetFavorite, SyncDirectory, SyncDirectoryResponse, SyncRemote,
    UncacheFileBundle,
};
use crate::{IngestImage, object_store::ObjectStore};
use clipboard_crypto::{KeyPurpose, VaultKey};
use clipboard_domain::{
    ClipboardContent, ClipboardItem, DeleteState, FavoriteState, FileBundle, Hlc,
    RetentionCandidate, plan_retention,
};
use clipboard_search::SearchEngine;
use clipboard_storage::Database;
use clipboard_sync::{
    DirectoryTransport, NoopSyncDiagnostics, OssStore, RemoteConfig, RemoteSegmentHeader,
    RemoteStore, SYNC_PROTOCOL_VERSION, SegmentHeader, SyncDiagnostic, SyncDiagnostics, SyncEvent,
    WebDavStore, open_segment, seal_segment,
};
use std::{collections::HashSet, path::Path, time::SystemTime};
use uuid::Uuid;

pub const MAX_IMAGE_BYTES: usize = 50 * 1024 * 1024;

pub struct CoreService {
    database: Database,
    vault_id: Uuid,
    vault_key: VaultKey,
    object_store: ObjectStore,
    file_cache: FileCache,
    processed_segments: HashSet<(Uuid, Uuid)>,
}

impl CoreService {
    pub fn open(data_dir: &Path, vault_id: Uuid, vault_key: &[u8; 32]) -> Result<Self, CoreError> {
        std::fs::create_dir_all(data_dir)?;
        let database = Database::open(&data_dir.join("history.db"), vault_key)?;
        let vault_key = VaultKey::from_bytes(*vault_key);
        let image_key = vault_key.derive(vault_id, KeyPurpose::Image)?;
        let object_store = ObjectStore::open(data_dir, vault_id, image_key)?;
        let file_cache_key = vault_key.derive(vault_id, KeyPurpose::FileCache)?;
        let file_cache = FileCache::open(data_dir, vault_id, file_cache_key)?;
        Ok(Self {
            database,
            vault_id,
            vault_key,
            object_store,
            file_cache,
            processed_segments: HashSet::new(),
        })
    }

    pub fn execute(&mut self, command: CoreCommand) -> Result<CoreResponse, CoreError> {
        match command {
            CoreCommand::IngestText(request) => self.ingest_text(request),
            CoreCommand::IngestFileBundle(request) => self.ingest_file_bundle(request),
            CoreCommand::CacheFileBundle(request) => self.cache_file_bundle(request),
            CoreCommand::UncacheFileBundle(request) => self.uncache_file_bundle(request),
            CoreCommand::ReadFileBundle(request) => self.read_file_bundle(request),
            CoreCommand::SyncDirectory(request) => {
                let diagnostics = NoopSyncDiagnostics;
                self.sync_directory_with_diagnostics(request, &diagnostics)
                    .map(CoreResponse::Sync)
            }
            CoreCommand::SyncRemote(request) => {
                let diagnostics = NoopSyncDiagnostics;
                self.sync_remote_with_diagnostics(request, &diagnostics)
                    .map(CoreResponse::Sync)
            }
            CoreCommand::ProbeRemote(request) => self.probe_remote(request),
            CoreCommand::Search(request) => self.search(request),
            CoreCommand::SetFavorite(request) => self.set_favorite(request),
            CoreCommand::Delete(request) => self.delete(request),
            CoreCommand::ClearUnfavorite => self.clear_unfavorite(),
            CoreCommand::ApplyRetention(request) => self.apply_retention(request),
        }
    }

    pub fn sync_directory_with_diagnostics(
        &mut self,
        request: SyncDirectory,
        diagnostics: &dyn SyncDiagnostics,
    ) -> Result<SyncDirectoryResponse, CoreError> {
        let transport = DirectoryTransport::open(&request.remote_path)?;
        self.sync_store_with_diagnostics(request.device_id, &transport, diagnostics)
    }

    pub fn sync_remote_with_diagnostics(
        &mut self,
        request: SyncRemote,
        diagnostics: &dyn SyncDiagnostics,
    ) -> Result<SyncDirectoryResponse, CoreError> {
        let store = create_remote_store(request.remote)?;
        self.sync_store_with_diagnostics(request.device_id, store.as_ref(), diagnostics)
    }

    fn probe_remote(&self, request: ProbeRemote) -> Result<CoreResponse, CoreError> {
        create_remote_store(request.remote)?.probe()?;
        Ok(CoreResponse::RemoteProbe { available: true })
    }

    fn sync_store_with_diagnostics(
        &mut self,
        device_id: Uuid,
        transport: &dyn RemoteStore,
        diagnostics: &dyn SyncDiagnostics,
    ) -> Result<SyncDirectoryResponse, CoreError> {
        let journal_key = self.vault_key.derive(self.vault_id, KeyPurpose::Journal)?;
        let mut response = SyncDirectoryResponse {
            pulled: 0,
            merged: 0,
            uploaded: 0,
            rejected_local_only: 0,
        };

        for remote_header in transport.list_completed()? {
            let header = *remote_header.header();
            if header.vault_id != self.vault_id
                || header.device_id == device_id
                || self
                    .processed_segments
                    .contains(&(header.device_id, header.segment_id))
            {
                continue;
            }

            let ciphertext = transport.get_completed(&remote_header)?;
            diagnostics.record(SyncDiagnostic::segment_completed(
                clipboard_sync::SyncDiagnosticPhase::Pull,
                header.segment_id,
                ciphertext.len() as u64,
                1,
            ));
            let events = open_segment(&journal_key, &header, &ciphertext)?;
            diagnostics.record(SyncDiagnostic::segment_completed(
                clipboard_sync::SyncDiagnosticPhase::Decrypt,
                header.segment_id,
                ciphertext.len() as u64,
                events.len() as u64,
            ));
            response.pulled += 1;
            let mut merged_in_segment = 0_u64;
            for event in events {
                let merged = self.merge_inbound_event(event)?;
                response.merged += usize::from(merged);
                merged_in_segment += u64::from(merged);
            }
            diagnostics.record(SyncDiagnostic::segment_completed(
                clipboard_sync::SyncDiagnosticPhase::Merge,
                header.segment_id,
                ciphertext.len() as u64,
                merged_in_segment,
            ));
            self.processed_segments
                .insert((header.device_id, header.segment_id));
        }

        for entry in self.database.outbox().pending()? {
            let item: ClipboardItem = serde_json::from_str(&entry.event_json)?;
            let event = match SyncEvent::try_from(&item) {
                Ok(event) => event,
                Err(clipboard_sync::SyncError::LocalOnlyRejected) => {
                    diagnostics.record(SyncDiagnostic::local_only_rejected(entry.item_id));
                    self.database.outbox().acknowledge(entry.id)?;
                    response.rejected_local_only += 1;
                    continue;
                }
                Err(error) => return Err(error.into()),
            };
            let header = SegmentHeader {
                protocol_version: SYNC_PROTOCOL_VERSION,
                vault_id: self.vault_id,
                device_id,
                segment_id: Uuid::now_v7(),
            };
            let ciphertext = seal_segment(&journal_key, &header, &[event])?;
            let remote_header = RemoteSegmentHeader::try_from(header)?;
            transport.put_pending_then_publish(&remote_header, &ciphertext)?;
            diagnostics.record(SyncDiagnostic::segment_completed(
                clipboard_sync::SyncDiagnosticPhase::Upload,
                header.segment_id,
                ciphertext.len() as u64,
                1,
            ));
            self.database.outbox().acknowledge(entry.id)?;
            response.uploaded += 1;
        }

        Ok(response)
    }

    fn merge_inbound_event(&self, event: SyncEvent) -> Result<bool, CoreError> {
        match event {
            SyncEvent::TextUpsert { item } => self.merge_inbound_text(item),
            SyncEvent::Favorite { item_id, state } => {
                let Some(mut item) = self.find_inbound_item(item_id)? else {
                    return Ok(false);
                };
                let merged = item
                    .favorite_state
                    .map_or(state, |existing| existing.merge(&state));
                if item.favorite_state == Some(merged) {
                    return Ok(false);
                }
                item.favorite_state = Some(merged);
                self.database.items().update(&item)?;
                Ok(true)
            }
            SyncEvent::Delete { item_id, state } => {
                let Some(mut item) = self.find_inbound_item(item_id)? else {
                    return Ok(false);
                };
                let merged = item
                    .delete_state
                    .map_or(state, |existing| existing.merge(&state));
                if item.delete_state == Some(merged) {
                    return Ok(false);
                }
                item.delete_state = Some(merged);
                self.database.items().update(&item)?;
                Ok(true)
            }
        }
    }

    fn merge_inbound_text(&self, incoming: ClipboardItem) -> Result<bool, CoreError> {
        let Some(mut existing) = self.find_inbound_item(incoming.id)? else {
            self.database.items().insert(&incoming)?;
            return Ok(true);
        };

        let ClipboardContent::Text(existing_text) = &existing.content else {
            return Err(CoreError::InvalidCommand(
                "incompatible sync item".to_owned(),
            ));
        };
        let ClipboardContent::Text(incoming_text) = &incoming.content else {
            return Err(CoreError::InvalidCommand("non-text sync item".to_owned()));
        };
        if existing_text != incoming_text || existing.vault_id != incoming.vault_id {
            return Err(CoreError::InvalidCommand(
                "incompatible sync item".to_owned(),
            ));
        }

        let mut changed = false;
        if incoming.last_used_ms > existing.last_used_ms {
            existing.last_used_ms = incoming.last_used_ms;
            existing.source_app = incoming.source_app;
            existing.source_app_display_name = incoming.source_app_display_name;
            existing.content_fingerprint = incoming.content_fingerprint;
            changed = true;
        }
        if let Some(incoming_state) = incoming.favorite_state {
            let merged = existing
                .favorite_state
                .map_or(incoming_state, |state| state.merge(&incoming_state));
            if existing.favorite_state != Some(merged) {
                existing.favorite_state = Some(merged);
                changed = true;
            }
        }
        if let Some(incoming_state) = incoming.delete_state {
            let merged = existing
                .delete_state
                .map_or(incoming_state, |state| state.merge(&incoming_state));
            if existing.delete_state != Some(merged) {
                existing.delete_state = Some(merged);
                changed = true;
            }
        }
        if changed {
            self.database.items().update(&existing)?;
        }
        Ok(changed)
    }

    fn find_inbound_item(&self, item_id: Uuid) -> Result<Option<ClipboardItem>, CoreError> {
        Ok(self
            .database
            .items()
            .list_all()?
            .into_iter()
            .find(|item| item.id == item_id && item.vault_id == self.vault_id))
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
            if request.source_app_display_name.is_some() {
                latest.source_app_display_name = request.source_app_display_name;
            }
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
        item.source_app_display_name = request.source_app_display_name;
        item.content_fingerprint = Some(fingerprint);
        self.database.items().insert_and_enqueue(&item)?;
        Ok(CoreResponse::Mutation { item_id: item.id })
    }

    fn ingest_file_bundle(&self, request: IngestFileBundle) -> Result<CoreResponse, CoreError> {
        let mut bundle = FileBundle::new(request.entries)?;
        bundle.entries.sort_by_key(|entry| entry.path.clone());
        let serialized = stable_file_bundle_bytes(&bundle)?;
        let legacy_serialized = legacy_file_bundle_bytes(&bundle)?;
        let fingerprint_key = self
            .vault_key
            .derive(self.vault_id, KeyPurpose::Fingerprint)?;
        let fingerprint = fingerprint_key.keyed_hash(&serialized);
        let legacy_fingerprint = fingerprint_key.keyed_hash(&legacy_serialized);
        let mut stored_items = self.database.items().list()?;
        if let Some(latest) = stored_items.iter_mut().find(|item| {
            item.vault_id == self.vault_id
                && matches!(&item.content, ClipboardContent::FileBundle(_))
                && (item.content_fingerprint == Some(fingerprint)
                    || item.content_fingerprint == Some(legacy_fingerprint))
        }) {
            latest.content = ClipboardContent::FileBundle(bundle);
            latest.content_fingerprint = Some(fingerprint);
            if request.source_app_display_name.is_some() {
                latest.source_app_display_name = request.source_app_display_name;
            }
            if request.captured_ms >= latest.last_used_ms {
                latest.last_used_ms = request.captured_ms;
                latest.source_app = request.source_app;
            }
            self.database.items().update(latest)?;
            return Ok(CoreResponse::Mutation { item_id: latest.id });
        }

        let mut item = ClipboardItem::new(
            Uuid::now_v7(),
            self.vault_id,
            ClipboardContent::FileBundle(bundle),
            request.source_app,
            request.captured_ms,
        );
        item.source_app_display_name = request.source_app_display_name;
        item.content_fingerprint = Some(fingerprint);
        self.database.items().insert(&item)?;
        Ok(CoreResponse::Mutation { item_id: item.id })
    }

    fn read_file_bundle(&self, request: ReadFileBundle) -> Result<CoreResponse, CoreError> {
        let item = self.find_item(request.item_id)?;
        let ClipboardContent::FileBundle(bundle) = item.content else {
            return Err(CoreError::NotFileBundle(request.item_id));
        };
        if bundle.cache.is_some()
            && bundle
                .entries
                .iter()
                .any(|entry| !std::path::Path::new(&entry.path).exists())
        {
            return Ok(CoreResponse::FileBundle {
                item_id: item.id,
                entries: self.file_cache.materialize(item.id, &bundle)?,
            });
        }
        Ok(CoreResponse::FileBundle {
            item_id: item.id,
            entries: bundle.entries,
        })
    }

    fn cache_file_bundle(&self, request: CacheFileBundle) -> Result<CoreResponse, CoreError> {
        let mut item = self.find_item(request.item_id)?;
        let ClipboardContent::FileBundle(ref mut bundle) = item.content else {
            return Err(CoreError::NotFileBundle(request.item_id));
        };
        let cache = self
            .file_cache
            .cache_bundle(item.id, bundle, request.max_bytes)?;
        bundle.cache = Some(cache);
        if let Err(error) = self.database.items().update(&item) {
            let _ = self.file_cache.remove_bundle(item.id);
            return Err(error.into());
        }
        Ok(CoreResponse::Mutation { item_id: item.id })
    }

    fn uncache_file_bundle(&self, request: UncacheFileBundle) -> Result<CoreResponse, CoreError> {
        let mut item = self.find_item(request.item_id)?;
        let ClipboardContent::FileBundle(ref mut bundle) = item.content else {
            return Err(CoreError::NotFileBundle(request.item_id));
        };
        bundle.cache = None;
        self.database.items().update(&item)?;
        self.file_cache.remove_bundle(item.id)?;
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
            if request.source_app_display_name.is_some() {
                latest.source_app_display_name = request.source_app_display_name;
            }
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
        item.source_app_display_name = request.source_app_display_name;
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
        if request.favorite
            && matches!(&item.content, ClipboardContent::FileBundle(bundle) if bundle.cache.is_none())
        {
            return Err(CoreError::FileCacheMissing(request.item_id));
        }
        let requested = FavoriteState {
            value: request.favorite,
            updated: request.updated,
        };
        let merged = item
            .favorite_state
            .map_or(requested, |existing| existing.merge(&requested));
        if item.favorite_state != Some(merged) {
            let releases_file_cache = is_favorite(&item)
                && !merged.value
                && matches!(&item.content, ClipboardContent::FileBundle(_));
            if releases_file_cache {
                let ClipboardContent::FileBundle(bundle) = &mut item.content else {
                    unreachable!("file bundle cache release requires a file bundle")
                };
                bundle.cache = None;
            }
            item.favorite_state = Some(merged);
            self.persist_update(&item)?;
            if releases_file_cache {
                self.file_cache.remove_bundle(item.id)?;
            }
        }
        Ok(CoreResponse::Mutation { item_id: item.id })
    }

    fn delete(&self, request: DeleteRequest) -> Result<CoreResponse, CoreError> {
        let mut item = self.find_item(request.item_id)?;
        if !item.is_syncable() {
            self.database.items().delete_local(item.id)?;
            self.remove_image_objects(std::slice::from_ref(&item))?;
            self.remove_file_cache_objects(std::slice::from_ref(&item))?;
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
            self.remove_file_cache_objects(std::slice::from_ref(&item))?;
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
        self.remove_file_cache_objects(&items)?;

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
        self.remove_file_cache_objects(&removed_items)?;

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

    fn remove_file_cache_objects(&self, items: &[ClipboardItem]) -> Result<(), CoreError> {
        for item in items {
            if matches!(item.content, ClipboardContent::FileBundle(_)) {
                self.file_cache.remove_bundle(item.id)?;
            }
        }
        Ok(())
    }
}

fn create_remote_store(config: RemoteConfig) -> Result<Box<dyn RemoteStore>, CoreError> {
    match config {
        RemoteConfig::WebDav(config) => Ok(Box::new(WebDavStore::new(config)?)),
        RemoteConfig::Oss(config) => Ok(Box::new(OssStore::new(config)?)),
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
            .any(|source_app| source_app_matches(source_app, item))
    {
        return false;
    }
    filters.kinds.is_empty()
        || filters
            .kinds
            .iter()
            .any(|kind| kind.eq_ignore_ascii_case(item_kind(item)))
}

fn source_app_matches(filter: &str, item: &ClipboardItem) -> bool {
    let filter = filter.trim();
    [
        Some(item.source_app.as_str()),
        item.source_app_display_name.as_deref(),
        Some(without_exe_suffix(&item.source_app)),
    ]
    .into_iter()
    .flatten()
    .any(|candidate| candidate.eq_ignore_ascii_case(filter))
}

fn without_exe_suffix(value: &str) -> &str {
    let Some(suffix) = value.as_bytes().get(value.len().saturating_sub(4)..) else {
        return value;
    };
    if suffix.eq_ignore_ascii_case(b".exe") {
        &value[..value.len() - 4]
    } else {
        value
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

fn item_kind(item: &ClipboardItem) -> &'static str {
    match item.content {
        ClipboardContent::Text(_) => "text",
        ClipboardContent::Image { .. } => "image",
        ClipboardContent::FileBundle(_) => "file_bundle",
    }
}

fn search_item(item: &ClipboardItem) -> SearchItem {
    let (kind, preview, width, height, bytes, file_count, representative_name, representative_kind) =
        match &item.content {
            ClipboardContent::Text(text) => {
                ("text", text.clone(), None, None, None, None, None, None)
            }
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
                None,
                None,
                None,
            ),
            ClipboardContent::FileBundle(bundle) => {
                let representative = bundle.entries.first();
                let representative_kind = representative.map(|entry| match entry.kind {
                    clipboard_domain::FileEntryKind::File => "file",
                    clipboard_domain::FileEntryKind::Directory => "directory",
                });
                let representative_name =
                    representative.and_then(|entry| representative_name(&entry.path, &entry.kind));
                let representative_name = representative_name.or_else(|| {
                    representative_kind
                        .as_ref()
                        .map(|kind| fallback_representative_name(kind))
                });
                (
                    "file_bundle",
                    representative_name.clone().unwrap_or_else(|| "文件".into()),
                    None,
                    None,
                    None,
                    Some(bundle.entries.len()),
                    representative_name,
                    representative_kind.map(str::to_owned),
                )
            }
        };
    SearchItem {
        id: item.id,
        kind: kind.into(),
        preview,
        source_app: item.source_app.clone(),
        source_app_display_name: item.source_app_display_name.clone(),
        last_used_ms: item.last_used_ms,
        favorite: is_favorite(item),
        width,
        height,
        bytes,
        file_count,
        representative_name,
        representative_kind,
    }
}

fn representative_name(path: &str, kind: &clipboard_domain::FileEntryKind) -> Option<String> {
    let name = path.rsplit('\\').find(|component| !component.is_empty())?;
    if name.len() == 2 && name.as_bytes().get(1) == Some(&b':') {
        return Some(fallback_representative_name(match kind {
            clipboard_domain::FileEntryKind::File => "file",
            clipboard_domain::FileEntryKind::Directory => "directory",
        }));
    }
    Some(name.to_owned())
}

fn fallback_representative_name(kind: &str) -> String {
    match kind {
        "directory" => "文件夹".into(),
        _ => "文件".into(),
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

fn stable_file_bundle_bytes(bundle: &FileBundle) -> Result<Vec<u8>, CoreError> {
    serde_json::to_vec(&bundle.entries)
        .map_err(|error| CoreError::InvalidCommand(error.to_string()))
}

fn legacy_file_bundle_bytes(bundle: &FileBundle) -> Result<Vec<u8>, CoreError> {
    #[derive(serde::Serialize)]
    struct LegacyEntry<'a> {
        path: &'a str,
        kind: &'static str,
        size: u64,
        modified_ms: i64,
    }

    let entries = bundle
        .entries
        .iter()
        .map(|entry| LegacyEntry {
            path: &entry.path,
            kind: match entry.kind {
                clipboard_domain::FileEntryKind::File => "File",
                clipboard_domain::FileEntryKind::Directory => "Directory",
            },
            size: entry.size,
            modified_ms: entry.modified_ms,
        })
        .collect::<Vec<_>>();
    serde_json::to_vec(&entries).map_err(|error| CoreError::InvalidCommand(error.to_string()))
}
