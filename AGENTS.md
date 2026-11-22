# AI 코딩 도구용 안내 (AGENTS.md)

Claude Code, Codex, Copilot 같은 AI 도구로 Foldy를 고칠 때 먼저 읽는 짧은 안내예요. 사람 기여자도 같은 규칙을 따라요([CONTRIBUTING.md](CONTRIBUTING.md)).

## 프로젝트

- Windows용 파일 관리자. UI는 C# .NET 10 + WPF(`src/`), 색인·복사 엔진은 Rust(`engine/`), 캐시는 SQLite.
- 테스트는 `tests/`(.NET)와 각 crate 안(Rust). 화면 자동 테스트는 `tests/Foldy.UiTests`(FlaUI).
- 화면 문구는 5개 언어(ko·en·es·ja·zh-CN). 번역 원본은 `build/i18n/`.

## 빌드와 테스트

- `./build.ps1` 하나로 Rust 빌드, 테스트, clippy, .NET 빌드와 테스트를 모두 돌려요. CI도 같은 스크립트를 써요.
- 끝났다고 말하기 전에 빌드와 테스트가 통과하는지 확인해 주세요. 테스트를 건너뛰거나 끄지 마세요.

## 꼭 지킬 것

1. **디자인 원칙**: 화면이나 기능을 바꾸면 [docs/DESIGN-PRINCIPLES.md](docs/DESIGN-PRINCIPLES.md)를 따라요. 처음 보는 사람도 바로 쓸 수 있게, 쉬운 말(해요체)로, 되돌릴 수 있게.
2. **회사·개인 정보 금지**: 코드·커밋·문서·화면 캡처에 회사 이름, 사내 주소, 사용자 이름이 든 경로, 이메일을 넣지 않아요([docs/no-company-info.md](docs/no-company-info.md)).
3. **비밀 금지**: 서명 키, 토큰, 비밀번호를 커밋하지 않아요.
4. **결정은 기록**: 설계를 정하거나 바꾸면 [docs/DECISIONS.md](docs/DECISIONS.md)에 이유와 함께 적어요.
5. **버전**: [유의적 버전](docs/versioning.md)을 따라요.
6. **라이선스**: GPL-3.0-or-later. 새 의존 패키지는 GPL-3.0과 호환되는 것만.

## 보안 문제

취약점을 찾으면 공개 이슈나 PR에 자세히 쓰지 말고, 고친 뒤에 공개해요. 지난 리뷰는 [docs/reviews/](docs/reviews/)에 있어요.

## AI 사용 밝히기

AI 도구의 도움을 받았으면 PR 설명에 적어 주세요. 커밋에는 `Assisted-by: 도구 이름` 한 줄을 붙여도 좋아요. 올리는 사람이 내용을 직접 확인하고 책임져요.
