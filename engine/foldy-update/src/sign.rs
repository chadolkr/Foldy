//! `update.json` Ed25519 서명 (설계서 10.5: GitHub 계정이 탈취돼도 서명 키 없이는 설치되지 않는다).
//!
//! 파일 형식은 모두 한 줄 소문자 16진수 텍스트다. `#`으로 시작하는 줄은 주석.
//! - 비밀키(시드 32바이트): GitHub Actions 시크릿 `FOLDY_UPDATE_SIGNING_KEY`에만 둔다. 저장소에 올리지 않는다.
//! - 공개키(32바이트): `engine/foldy-update/keys/update-signing.pub`에 커밋해 앱에 내장한다.
//! - 서명(64바이트): `update.json.sig`.

use ed25519_dalek::{Signature, Signer, SigningKey, VerifyingKey};

use crate::error::{Result, UpdateError};
use crate::hex;

/// 앱에 내장되는 공개키 파일. 아직 키를 만들지 않았으면 주석만 있다 → 검증이 항상 실패(설치 안 함).
pub const EMBEDDED_PUBLIC_KEY: &str = include_str!("../keys/update-signing.pub");

/// 운영체제 난수로 새 서명 키를 만든다.
pub fn generate_signing_key() -> Result<SigningKey> {
    let mut seed = [0u8; 32];
    getrandom::getrandom(&mut seed)
        .map_err(|e| UpdateError::InvalidKey(format!("난수 생성 실패: {e}")))?;
    Ok(SigningKey::from_bytes(&seed))
}

/// 주석·공백을 뺀 본문.
fn payload(text: &str) -> String {
    text.lines()
        .map(str::trim)
        .filter(|l| !l.is_empty() && !l.starts_with('#'))
        .collect::<String>()
}

pub fn signing_key_to_text(key: &SigningKey) -> String {
    format!(
        "# Foldy 업데이트 서명 비밀키 (Ed25519 시드). 절대 커밋하지 말 것 — GitHub Actions 시크릿에만 저장.\n{}\n",
        hex::encode(key.as_bytes())
    )
}

pub fn verifying_key_to_text(key: &VerifyingKey) -> String {
    format!(
        "# Foldy 업데이트 서명 공개키 (Ed25519)\n{}\n",
        hex::encode(key.as_bytes())
    )
}

pub fn parse_signing_key(text: &str) -> Result<SigningKey> {
    let seed = hex::decode_array::<32>(&payload(text))
        .ok_or_else(|| UpdateError::InvalidKey("비밀키는 16진수 64자여야 함".into()))?;
    Ok(SigningKey::from_bytes(&seed))
}

pub fn parse_verifying_key(text: &str) -> Result<VerifyingKey> {
    let bytes = hex::decode_array::<32>(&payload(text))
        .ok_or_else(|| UpdateError::InvalidKey("공개키는 16진수 64자여야 함".into()))?;
    VerifyingKey::from_bytes(&bytes)
        .map_err(|e| UpdateError::InvalidKey(format!("공개키가 곡선 위의 점이 아님: {e}")))
}

/// 앱에 내장된 공개키. 없으면 [`UpdateError::NoEmbeddedKey`] (업데이트를 설치하지 않는다).
pub fn embedded_verifying_key() -> Result<VerifyingKey> {
    if payload(EMBEDDED_PUBLIC_KEY).is_empty() {
        return Err(UpdateError::NoEmbeddedKey);
    }
    parse_verifying_key(EMBEDDED_PUBLIC_KEY)
}

/// `.sig` 파일 내용 (16진수 128자 + 줄바꿈).
pub fn sign(key: &SigningKey, data: &[u8]) -> String {
    format!("{}\n", hex::encode(&key.sign(data).to_bytes()))
}

/// 바이트 그대로에 대한 서명을 검사한다. `verify_strict`로 변형 서명(가변성)도 거부한다.
pub fn verify(key: &VerifyingKey, data: &[u8], sig_text: &str) -> Result<()> {
    let bytes = hex::decode_array::<64>(&payload(sig_text)).ok_or(UpdateError::BadSignature)?;
    let sig = Signature::from_bytes(&bytes);
    key.verify_strict(data, &sig)
        .map_err(|_| UpdateError::BadSignature)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn sign_verify_roundtrip() {
        let sk = generate_signing_key().unwrap();
        let vk = sk.verifying_key();
        let sig = sign(&sk, b"hello");
        verify(&vk, b"hello", &sig).unwrap();
        assert!(matches!(
            verify(&vk, b"hellO", &sig),
            Err(UpdateError::BadSignature)
        ));
    }

    #[test]
    fn key_text_roundtrip() {
        let sk = generate_signing_key().unwrap();
        let back = parse_signing_key(&signing_key_to_text(&sk)).unwrap();
        assert_eq!(back.as_bytes(), sk.as_bytes());
        let vk = parse_verifying_key(&verifying_key_to_text(&sk.verifying_key())).unwrap();
        assert_eq!(vk, sk.verifying_key());
    }

    #[test]
    fn garbage_signature_rejected() {
        let vk = generate_signing_key().unwrap().verifying_key();
        assert!(matches!(
            verify(&vk, b"x", "not hex"),
            Err(UpdateError::BadSignature)
        ));
        assert!(matches!(
            verify(&vk, b"x", &"00".repeat(64)),
            Err(UpdateError::BadSignature)
        ));
    }

    #[test]
    fn embedded_key_is_valid_or_absent() {
        // 키를 아직 만들지 않았으면 NoEmbeddedKey, 만들었으면 올바른 공개키여야 한다
        match embedded_verifying_key() {
            Ok(_) | Err(UpdateError::NoEmbeddedKey) => {}
            Err(e) => panic!("내장 공개키 형식 오류: {e}"),
        }
    }
}
