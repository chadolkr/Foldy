# Microsoft Store 첫 제출 안내 (Partner Center)

Foldy File Manager(Store ID `9NS1PWL5QGP6`)를 Partner Center에 처음 제출할 때 칸마다 무엇을 넣는지 적었다.
계획과 Store 판의 차이는 [../store.md](../store.md).

| 파일 | 내용 |
|---|---|
| [ko.md](ko.md) · [en.md](en.md) · [es.md](es.md) · [ja.md](ja.md) · [zh-cn.md](zh-cn.md) | 언어별 스토어 등록 정보 (설명, 짧은 설명, 기능, 검색어, 새 기능, 스크린샷 설명) |
| `/mnt/project-files/store/screenshots/<언어>/` (프로젝트 파일) | 언어별 스크린샷 7장, 1920×1080 PNG. `assets/help/images/<언어>/`의 실제 화면에 한 줄 설명을 얹은 것 |
| 사이트 `/privacy/` | 개인정보 처리방침 5개 언어: https://chadolkr.github.io/Foldy/privacy/ |

## 미리 할 일

- 1.0.0 태그 릴리스 워크플로가 끝나면 그 실행(Actions → Release) 맨 아래 **Artifacts**에서 `foldy-store-msix`를 받아 압축을 푼다. 안에 `Foldy-1.0.0-x64.msix`이 있다.
- 이 저장소 PR(사이트 개인정보 페이지)이 병합돼 사이트에 `/privacy/`가 보여야 한다.

## Partner Center에서 (순서대로)

https://partner.microsoft.com/dashboard/apps-and-games/overview → **Foldy File Manager** → **제출 시작(Start your submission)**.

### 1. 가격 및 사용 가능 여부 (Pricing and availability)

| 칸 | 값 |
|---|---|
| 시장 (Markets) | 모든 시장 (기본값 그대로) |
| 표시 여부 (Visibility) | 공개 (Public audience) |
| 게시 (Schedule) | 인증을 통과하면 바로 (As soon as it passes certification) |
| 가격 (Pricing) | 무료 (Free) |
| 무료 평가판·할인 | 없음 |

### 2. 속성 (Properties)

| 칸 | 값 |
|---|---|
| 범주 (Category) | 유틸리티 및 도구 (Utilities & tools) |
| 하위 범주 | 파일 관리자 (File managers) |
| 개인정보: "이 제품이 개인 정보에 접근, 수집, 전송합니까?" | **예** (오류 보고가 동의를 받고 전송하므로 안전하게 예) |
| 개인정보 처리방침 URL | `https://chadolkr.github.io/Foldy/privacy/` |
| 웹 사이트 | `https://chadolkr.github.io/Foldy/` |
| 지원 연락처 | `https://github.com/chadolkr/Foldy/issues` |
| 시스템 요구 사항 | 비워 둠 |
| 제품 선언 | 기본값 그대로. "접근성 지침 테스트를 마쳤음"은 체크하지 않는다 |

### 3. 연령 등급 (Age ratings)

IARC 설문. 앱 종류는 **유틸리티·생산성·통신·기타(Utility, Productivity, Communication, or Other)** 를 고른다. 이어지는 질문은 모두 **아니요**:

- 폭력, 공포, 성적인 내용, 도박, 욕설, 약물·술·담배가 나오나요? → 아니요
- 사용자끼리 대화하거나 내용을 주고받나요? → 아니요
- 사용자의 위치를 다른 사람과 공유하나요? → 아니요
- 앱 안에서 디지털 상품을 사나요? → 아니요
- 제한 없는 인터넷 접근(웹 브라우저 등)을 주나요? → 아니요
- 개인정보를 모아 제3자와 공유하나요? → 아니요

예상 결과: 모든 연령(3+, 한국 전체이용가).

### 4. 패키지 (Packages)

- 위에서 받은 `.msix`를 끌어다 놓는다.
- 장치 패밀리: **Windows 10/11 Desktop**만 체크 (나머지는 끔).

### 5. 스토어 등록 정보 (Store listings)

- 언어 목록에 5개(한국어, English, Español, 日本語, 简体中文)가 보여야 한다. 없는 언어는 **추가 언어 관리(Manage additional languages)** 에서 더한다 (스페인어는 es-ES, 중국어는 zh-CN 간체).
- 언어마다 들어가서 그 언어 파일(ko.md 등)의 각 제목 아래 글을 같은 이름의 칸에 붙여 넣는다.
  - 설명, 이 버전의 새로운 기능, 제품 기능(한 줄씩 칸에), 짧은 설명, 검색어(한 칸에 하나씩 7개), 추가 시스템 요구 사항, 저작권 및 상표 정보, 추가 라이선스 조항, 개발자
- 스크린샷: 그 언어 폴더의 7장을 번호 순서대로 올리고, 각 그림의 설명 칸에 "스크린샷 설명"의 같은 번호 문장을 넣는다.
- 로고·예고편은 선택이라 비워 둔다 (패키지 안의 아이콘을 쓴다).

### 6. 제출 옵션 (Submission options)

**인증 참고 사항 (Notes for certification)**, 영어로:

```
No account or sign-in is needed, and every feature works offline.
How to test: start Foldy, press Ctrl+Space and type part of a file name. In the left sidebar try
"Free up space", "Tidy Desktop, Downloads, folders" and "Create folders from a template".
The Store edition does not include the Windows service (FoldyIndexer) that our website installer uses,
so search scans the current folder instead of a drive-wide index. Updates come only from the Microsoft Store.
Crash reporting is off by default and asks for consent before sending.
Source code (GPL-3.0): https://github.com/chadolkr/Foldy
```

**제한된 기능 사유 (Why do you need the runFullTrust capability?)**, 영어로:

```
Foldy is a desktop file manager (WPF/.NET) packaged as a full-trust desktop app. runFullTrust is required
because, like File Explorer, it browses, copies, moves, renames and deletes files and folders anywhere the
user chooses on local, USB and network drives; opens files with their default apps and shows the Windows
shell context menu; registers a global hotkey (Ctrl+Space) for quick search; and runs Git, SSH and SFTP
connections that the user starts. It does not install services or drivers, does not need administrator
rights, and does not update itself (updates come from the Microsoft Store).
```

### 7. 제출

**스토어에 제출(Submit to the Store)**. 인증은 보통 1~3 영업일, 결과는 메일로 온다. 통과하면 위 "게시" 설정대로 바로 공개된다.

## 다음 버전부터

새 버전 MSIX를 받아 **업데이트(Update)** → 패키지만 바꾸고, 등록 정보의 "이 버전의 새로운 기능"만 고쳐 제출한다.
익숙해지면 Microsoft Store 제출 API(`msstore` CLI)로 릴리스 워크플로에서 자동 제출할 수 있다.
