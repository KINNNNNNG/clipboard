use std::collections::{BTreeMap, BTreeSet};
use uuid::Uuid;

#[derive(Clone, Copy, Debug, PartialEq, Eq, PartialOrd, Ord, Hash)]
pub struct SnapshotId(pub Uuid);

#[derive(Clone, Debug, Default, PartialEq, Eq)]
pub struct VersionVector(BTreeMap<Uuid, u64>);

impl From<[(Uuid, u64); 2]> for VersionVector {
    fn from(entries: [(Uuid, u64); 2]) -> Self {
        Self(entries.into_iter().collect())
    }
}

impl From<[(Uuid, u64); 1]> for VersionVector {
    fn from(entries: [(Uuid, u64); 1]) -> Self {
        Self(entries.into_iter().collect())
    }
}

impl VersionVector {
    pub fn get(&self, device: Uuid) -> u64 {
        self.0.get(&device).copied().unwrap_or_default()
    }

    pub fn covers(&self, required: &Self) -> bool {
        required
            .0
            .iter()
            .all(|(device, sequence)| self.get(*device) >= *sequence)
    }
}

#[derive(Clone, Debug)]
struct SnapshotAck {
    device: Uuid,
    vector: VersionVector,
}

#[derive(Clone, Debug)]
pub struct DeviceRegistry {
    known: BTreeSet<Uuid>,
    active: BTreeSet<Uuid>,
    acknowledgements: BTreeMap<SnapshotId, Vec<SnapshotAck>>,
}

impl DeviceRegistry {
    pub fn new(local_device: Uuid) -> Self {
        let mut registry = Self {
            known: BTreeSet::new(),
            active: BTreeSet::new(),
            acknowledgements: BTreeMap::new(),
        };
        registry.register_active(local_device);
        registry
    }

    pub fn register_active(&mut self, device: Uuid) {
        self.known.insert(device);
        self.active.insert(device);
    }

    pub fn deactivate(&mut self, device: Uuid) -> Result<(), &'static str> {
        if !self.known.contains(&device) {
            return Err("unknown device");
        }
        self.active.remove(&device);
        Ok(())
    }

    pub fn is_known(&self, device: Uuid) -> bool {
        self.known.contains(&device)
    }

    pub fn is_active(&self, device: Uuid) -> bool {
        self.active.contains(&device)
    }

    pub fn record_ack(&mut self, device: Uuid, snapshot: SnapshotId, vector: VersionVector) {
        if !self.known.contains(&device) {
            return;
        }
        let acknowledgements = self.acknowledgements.entry(snapshot).or_default();
        acknowledgements.retain(|ack| ack.device != device);
        acknowledgements.push(SnapshotAck { device, vector });
    }

    pub fn can_compact(&self, snapshot: SnapshotId, required: &VersionVector) -> bool {
        let Some(acks) = self.acknowledgements.get(&snapshot) else {
            return false;
        };
        self.active.iter().all(|device| {
            acks.iter()
                .find(|ack| ack.device == *device)
                .is_some_and(|ack| ack.vector.covers(required))
        })
    }
}
