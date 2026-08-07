use clipboard_crypto::{DerivedKey, ObjectCipher};
use clipboard_domain::{ClipboardContent, ClipboardItem, DeleteState, FavoriteState, SyncScope};
use serde::{Deserialize, Serialize};
use uuid::Uuid;

use crate::{SYNC_PROTOCOL_VERSION, SyncError};

const JOURNAL_AAD_LABEL: &[u8] = b"journal";

#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize, Deserialize)]
pub struct SegmentHeader {
    pub protocol_version: u8,
    pub vault_id: Uuid,
    pub device_id: Uuid,
    pub segment_id: Uuid,
}

// Keep the serialized event payload directly inspectable before encryption.
#[allow(clippy::large_enum_variant)]
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
pub enum SyncEvent {
    TextUpsert { item: ClipboardItem },
    Favorite { item_id: Uuid, state: FavoriteState },
    Delete { item_id: Uuid, state: DeleteState },
}

impl TryFrom<&ClipboardItem> for SyncEvent {
    type Error = SyncError;

    fn try_from(item: &ClipboardItem) -> Result<Self, Self::Error> {
        if item.sync_scope() != SyncScope::Vault
            || !matches!(&item.content, ClipboardContent::Text(_))
        {
            return Err(SyncError::LocalOnlyRejected);
        }

        Ok(Self::TextUpsert { item: item.clone() })
    }
}

pub fn seal_segment(
    journal_key: &DerivedKey,
    header: &SegmentHeader,
    events: &[SyncEvent],
) -> Result<Vec<u8>, SyncError> {
    validate_header(header)?;
    validate_events(header, events)?;

    let aad = segment_aad(header);
    let plaintext = serde_json::to_vec(events).map_err(|_| SyncError::InvalidSegment)?;
    ObjectCipher::new(journal_key.derive_scoped(&aad)?)
        .seal(&plaintext, &aad)
        .map_err(Into::into)
}

pub fn open_segment(
    journal_key: &DerivedKey,
    header: &SegmentHeader,
    sealed: &[u8],
) -> Result<Vec<SyncEvent>, SyncError> {
    validate_header(header)?;

    let aad = segment_aad(header);
    let plaintext = ObjectCipher::new(journal_key.derive_scoped(&aad)?)
        .open(sealed, &aad)
        .map_err(SyncError::from)?;
    let events = serde_json::from_slice::<Vec<SyncEvent>>(&plaintext)
        .map_err(|_| SyncError::InvalidSegment)?;
    validate_events(header, &events)?;
    Ok(events)
}

pub(crate) fn segment_aad(header: &SegmentHeader) -> Vec<u8> {
    let mut aad = Vec::with_capacity(JOURNAL_AAD_LABEL.len() + 1 + 16 * 3);
    aad.push(header.protocol_version);
    aad.extend_from_slice(header.vault_id.as_bytes());
    aad.extend_from_slice(header.device_id.as_bytes());
    aad.extend_from_slice(header.segment_id.as_bytes());
    aad.extend_from_slice(JOURNAL_AAD_LABEL);
    aad
}

fn validate_header(header: &SegmentHeader) -> Result<(), SyncError> {
    if header.protocol_version != SYNC_PROTOCOL_VERSION {
        return Err(SyncError::UnsupportedProtocolVersion(
            header.protocol_version,
        ));
    }
    Ok(())
}

fn validate_events(header: &SegmentHeader, events: &[SyncEvent]) -> Result<(), SyncError> {
    for event in events {
        if let SyncEvent::TextUpsert { item } = event {
            SyncEvent::try_from(item)?;
            if item.vault_id != header.vault_id {
                return Err(SyncError::InvalidSegment);
            }
        }
    }
    Ok(())
}
