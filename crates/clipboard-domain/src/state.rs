use crate::Hlc;
use serde::{Deserialize, Serialize};

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
pub struct FavoriteState {
    pub value: bool,
    pub updated: Hlc,
}

impl FavoriteState {
    pub fn merge(&self, other: &Self) -> Self {
        if (other.updated, other.value) > (self.updated, self.value) {
            *other
        } else {
            *self
        }
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
pub struct DeleteState {
    pub deleted: bool,
    pub updated: Hlc,
}

impl DeleteState {
    pub fn merge(&self, other: &Self) -> Self {
        if (other.updated, other.deleted) > (self.updated, self.deleted) {
            *other
        } else {
            *self
        }
    }
}
