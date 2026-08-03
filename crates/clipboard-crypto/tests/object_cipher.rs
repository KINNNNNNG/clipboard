use clipboard_crypto::{CryptoError, KeyPurpose, ObjectCipher, VaultKey};
use uuid::Uuid;

#[test]
fn ciphertext_round_trips_and_detects_tampering() {
    let vault_id = Uuid::from_u128(7);
    let key = VaultKey::from_bytes([0x42; 32]);
    let cipher = ObjectCipher::new(key.derive(vault_id, KeyPurpose::Image).unwrap());
    let aad = b"vault=7;object=9;type=image;version=1";

    let mut sealed = cipher.seal(b"clipboard image bytes", aad).unwrap();
    assert_eq!(sealed[0], 1);
    assert_eq!(sealed.len(), 1 + 24 + b"clipboard image bytes".len() + 16);
    assert_eq!(cipher.open(&sealed, aad).unwrap(), b"clipboard image bytes");

    *sealed.last_mut().unwrap() ^= 0x01;
    assert_eq!(cipher.open(&sealed, aad), Err(CryptoError::Authentication));
}

#[test]
fn ciphertext_is_bound_to_aad_and_key_purpose() {
    let vault_id = Uuid::from_u128(7);
    let key = VaultKey::from_bytes([0x42; 32]);
    let image_cipher = ObjectCipher::new(key.derive(vault_id, KeyPurpose::Image).unwrap());
    let file_cipher = ObjectCipher::new(key.derive(vault_id, KeyPurpose::FileCache).unwrap());
    let sealed = image_cipher.seal(b"secret", b"image-object").unwrap();

    assert_eq!(
        image_cipher.open(&sealed, b"other-object"),
        Err(CryptoError::Authentication)
    );
    assert_eq!(
        file_cipher.open(&sealed, b"image-object"),
        Err(CryptoError::Authentication)
    );
}

#[test]
fn malformed_or_unknown_ciphertexts_are_rejected() {
    let key = VaultKey::from_bytes([0x42; 32]);
    let cipher = ObjectCipher::new(key.derive(Uuid::from_u128(7), KeyPurpose::Image).unwrap());

    assert_eq!(
        cipher.open(&[1, 2, 3], b"aad"),
        Err(CryptoError::InvalidCiphertext)
    );

    let mut sealed = cipher.seal(b"secret", b"aad").unwrap();
    sealed[0] = 2;
    assert_eq!(
        cipher.open(&sealed, b"aad"),
        Err(CryptoError::UnsupportedVersion(2))
    );
}
