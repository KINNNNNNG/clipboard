use std::{
    fs::{self, OpenOptions},
    io::Write,
    path::{Path, PathBuf},
};

use uuid::Uuid;

use crate::{
    PENDING_OBJECT_SUFFIX, RemoteImageObject, RemoteSegmentHeader, RemoteStore,
    SYNC_PROTOCOL_VERSION, SegmentHeader, SyncError, completed_image_object_name,
    pending_image_object_name,
};

pub trait SyncTransport: Send + Sync {
    fn put_segment(&self, header: &SegmentHeader, ciphertext: &[u8]) -> Result<(), SyncError>;
    fn list_segments(&self, device_id: Uuid) -> Result<Vec<SegmentHeader>, SyncError>;
    fn get_segment(&self, header: &SegmentHeader) -> Result<Vec<u8>, SyncError>;
}

#[derive(Clone)]
pub struct DirectoryTransport {
    directory: PathBuf,
}

impl DirectoryTransport {
    pub fn open(directory: impl AsRef<Path>) -> Result<Self, SyncError> {
        let directory = directory.as_ref().to_path_buf();
        fs::create_dir_all(&directory).map_err(|_| SyncError::Transport)?;
        Ok(Self { directory })
    }

    pub fn list_all_segments(&self) -> Result<Vec<SegmentHeader>, SyncError> {
        let mut names = Vec::new();
        for entry in fs::read_dir(&self.directory).map_err(|_| SyncError::Transport)? {
            let entry = entry.map_err(|_| SyncError::Transport)?;
            if !entry
                .file_type()
                .map_err(|_| SyncError::Transport)?
                .is_file()
            {
                continue;
            }
            let name = entry.file_name();
            let Some(name) = name.to_str() else {
                continue;
            };
            if name.ends_with(".enc") {
                names.push(name.to_owned());
            }
        }
        names.sort_unstable();

        Ok(names
            .into_iter()
            .filter_map(|name| parse_segment_name(&name))
            .collect())
    }

    fn path_for(&self, header: &SegmentHeader, suffix: &str) -> PathBuf {
        self.directory
            .join(format!("{}{}", segment_name(header), suffix))
    }

    pub fn put_object(&self, object_name: &str, ciphertext: &[u8]) -> Result<(), SyncError> {
        self.put_named_object(
            object_name,
            &format!("{object_name}{PENDING_OBJECT_SUFFIX}"),
            ciphertext,
        )
    }

    pub fn get_object(&self, object_name: &str) -> Result<Vec<u8>, SyncError> {
        fs::read(self.directory.join(object_name)).map_err(|_| SyncError::Transport)
    }

    fn put_named_object(
        &self,
        completed_name: &str,
        pending_name: &str,
        ciphertext: &[u8],
    ) -> Result<(), SyncError> {
        let final_path = self.directory.join(completed_name);
        if final_path.exists() {
            return Ok(());
        }
        let pending_path = self.directory.join(pending_name);
        let mut pending = OpenOptions::new()
            .write(true)
            .create_new(true)
            .open(&pending_path)
            .map_err(|_| SyncError::Transport)?;
        pending
            .write_all(ciphertext)
            .map_err(|_| SyncError::Transport)?;
        pending.sync_all().map_err(|_| SyncError::Transport)?;
        drop(pending);
        fs::rename(&pending_path, final_path).map_err(|_| SyncError::Transport)
    }
}

impl SyncTransport for DirectoryTransport {
    fn put_segment(&self, header: &SegmentHeader, ciphertext: &[u8]) -> Result<(), SyncError> {
        if header.protocol_version != SYNC_PROTOCOL_VERSION {
            return Err(SyncError::UnsupportedProtocolVersion(
                header.protocol_version,
            ));
        }

        let final_path = self.path_for(header, ".enc");
        if final_path.exists() {
            return Ok(());
        }

        let pending_path = self.path_for(header, ".pending");
        let mut pending = OpenOptions::new()
            .write(true)
            .create_new(true)
            .open(&pending_path)
            .map_err(|_| SyncError::Transport)?;
        pending
            .write_all(ciphertext)
            .map_err(|_| SyncError::Transport)?;
        pending.sync_all().map_err(|_| SyncError::Transport)?;
        drop(pending);
        fs::rename(&pending_path, final_path).map_err(|_| SyncError::Transport)
    }

    fn list_segments(&self, device_id: Uuid) -> Result<Vec<SegmentHeader>, SyncError> {
        Ok(self
            .list_all_segments()?
            .into_iter()
            .filter(|header| header.device_id == device_id)
            .collect())
    }

    fn get_segment(&self, header: &SegmentHeader) -> Result<Vec<u8>, SyncError> {
        fs::read(self.path_for(header, ".enc")).map_err(|_| SyncError::Transport)
    }
}

impl RemoteStore for DirectoryTransport {
    fn list_completed(&self) -> Result<Vec<RemoteSegmentHeader>, SyncError> {
        Ok(self
            .list_all_segments()?
            .into_iter()
            .filter_map(|header| RemoteSegmentHeader::try_from(header).ok())
            .collect())
    }

    fn get_completed(&self, header: &RemoteSegmentHeader) -> Result<Vec<u8>, SyncError> {
        self.get_segment(header.header())
    }

    fn put_pending_then_publish(
        &self,
        header: &RemoteSegmentHeader,
        ciphertext: &[u8],
    ) -> Result<(), SyncError> {
        self.put_segment(header.header(), ciphertext)
    }

    fn get_image_object(&self, object: &RemoteImageObject) -> Result<Vec<u8>, SyncError> {
        self.get_object(&completed_image_object_name(object))
    }

    fn put_image_object(
        &self,
        object: &RemoteImageObject,
        ciphertext: &[u8],
    ) -> Result<(), SyncError> {
        self.put_named_object(
            &completed_image_object_name(object),
            &pending_image_object_name(object),
            ciphertext,
        )
    }

    fn probe(&self) -> Result<(), SyncError> {
        self.list_all_segments().map(|_| ())
    }
}

fn segment_name(header: &SegmentHeader) -> String {
    format!(
        "{:02x}-{}-{}-{}",
        header.protocol_version, header.vault_id, header.device_id, header.segment_id
    )
}

fn parse_segment_name(name: &str) -> Option<SegmentHeader> {
    let stem = name.strip_suffix(".enc")?;
    let mut parts = stem.split('-');
    let version = u8::from_str_radix(parts.next()?, 16).ok()?;
    let vault_id = parse_uuid(&mut parts)?;
    let device_id = parse_uuid(&mut parts)?;
    let segment_id = parse_uuid(&mut parts)?;
    if parts.next().is_some() {
        return None;
    }
    Some(SegmentHeader {
        protocol_version: version,
        vault_id,
        device_id,
        segment_id,
    })
}

fn parse_uuid<'a>(parts: &mut impl Iterator<Item = &'a str>) -> Option<Uuid> {
    let value = format!(
        "{}-{}-{}-{}-{}",
        parts.next()?,
        parts.next()?,
        parts.next()?,
        parts.next()?,
        parts.next()?
    );
    Uuid::parse_str(&value).ok()
}
