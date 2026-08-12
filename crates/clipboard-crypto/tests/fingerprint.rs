use clipboard_crypto::{KeyPurpose, VaultKey};
use uuid::Uuid;

#[test]
fn keyed_hash_is_stable_for_the_same_vault_key_purpose_and_content() {
    let key = VaultKey::from_bytes([0x42; 32]);
    let vault_id = Uuid::from_u128(7);
    let derived = key.derive(vault_id, KeyPurpose::Fingerprint).unwrap();

    assert_eq!(
        derived.keyed_hash(b"same clipboard text"),
        derived.keyed_hash(b"same clipboard text")
    );
}

#[test]
fn keyed_hash_is_isolated_by_master_key_vault_and_purpose() {
    let content = b"same clipboard text";
    let first_key = VaultKey::from_bytes([0x11; 32]);
    let second_key = VaultKey::from_bytes([0x22; 32]);
    let first_vault = Uuid::from_u128(1);
    let second_vault = Uuid::from_u128(2);

    let baseline = first_key
        .derive(first_vault, KeyPurpose::Fingerprint)
        .unwrap()
        .keyed_hash(content);

    assert_ne!(
        baseline,
        second_key
            .derive(first_vault, KeyPurpose::Fingerprint)
            .unwrap()
            .keyed_hash(content)
    );
    assert_ne!(
        baseline,
        first_key
            .derive(second_vault, KeyPurpose::Fingerprint)
            .unwrap()
            .keyed_hash(content)
    );
    assert_ne!(
        baseline,
        first_key
            .derive(first_vault, KeyPurpose::Image)
            .unwrap()
            .keyed_hash(content)
    );
}

#[test]
fn scoped_key_derivation_isolated_by_object_context() {
    let key = VaultKey::from_bytes([0x51; 32]);
    let vault_id = Uuid::from_u128(42);
    let base = key.derive(vault_id, KeyPurpose::FileCache).unwrap();

    let first = base.derive_scoped(b"bundle-a/file-0/chunk-0").unwrap();
    let second = base.derive_scoped(b"bundle-a/file-0/chunk-1").unwrap();

    assert_ne!(
        first.keyed_hash(b"same bytes"),
        second.keyed_hash(b"same bytes")
    );
}
