# Google Drive 바로 연결

Google Drive for desktop을 설치하지 않고 Foldy 안에서 Google 계정으로 로그인해 Drive를 탭으로 연다.

- 프로젝트: `src/Foldy.Providers.GoogleDrive` (net10.0-windows, 외부 패키지 없음), 테스트: `tests/Foldy.GoogleDrive.Tests`
- 앱 연결: `src/Foldy.App/Services/GoogleDriveIntegration.cs`, `GDriveConnectWindow`
- 사용자 도움말: `assets/help/index.md`의 "Google Drive"

## 경로

`gdrive://계정이메일/폴더/파일` (`GDriveLocation`, `PathUtil.IsGDrive`). 루트는 "내 드라이브".

- 계정은 소문자로 정규화한다. 경로 조각은 Drive에 보이는 이름 그대로다(퍼센트 인코딩 없음).
- Drive 이름의 `/`는 `∕`(U+2215)로 바꿔 보여 준다.
- 같은 폴더에 같은 이름이 여러 개면(Drive는 허용) 목록 순서대로 `이름 (2).확장자`처럼 구분한다.
- Google 문서·시트·슬라이드·그림은 `.gdoc`·`.gsheet`·`.gslides`·`.gdraw`를 붙여 보여 준다(그 밖의 Google 형식은 `.gform`, `.gsite`, `.glink` 등).
- 바로가기는 대상처럼 다룬다(폴더 바로가기로 들어갈 수 있음). 폴더를 통째로 복사할 때 바로가기 폴더 안으로는 따라 들어가지 않는다(순환 방지).
- 공유 문서함(Shared with me)과 공유 드라이브는 아직 목록에 없다. 주소를 알면 바로가기로 내 드라이브에 넣어 쓴다.

`PathUtil.IsRemoteUri`(SFTP 또는 Google Drive)면 로컬 전용 기능(폴더 크기, Git, 템플릿·위저드·정리·설명, 썸네일 디코딩, 터미널)을 끈다.

## 경로 → id

Drive는 id로 동작한다. `GoogleDriveProvider`가 (경로 → `GDriveFile`) 캐시를 가진다.

1. 목록을 읽을 때 자식들의 경로를 캐시에 넣는다(읽기 시작할 때 그 폴더 아래 옛 캐시는 버린다).
2. 캐시에 없으면 부모를 풀고, 이름으로 찾는다(`name = '…'` 질의, 큰 폴더를 다 읽지 않으려고). 결과가 하나이고 보이는 이름이 같으면 쓴다.
3. 그래도 없으면(구분 이름 `(2)`, 같은 이름 여러 개) 부모를 다시 읽고 캐시에서 찾는다. 없으면 `FileNotFoundException`.

이름 바꾸기·삭제·이동 뒤에는 그 경로 아래 캐시를 버린다(`Forget`).

## API

`GDriveHttpApi`가 Drive REST v3를 `HttpClient`로 직접 부른다(Google SDK 없음).

| 동작 | 호출 |
| --- | --- |
| 목록 | `files.list` `q='<id>' in parents and trashed=false`, `orderBy=folder,name,createdTime`, 1000개씩 쪽 넘김 |
| 이름으로 찾기 | `files.list` `name='…'` (`\`와 `'`는 이스케이프) |
| 내려받기 | `files.get?alt=media` / Google 문서는 `files.export` (docx·xlsx·pptx·png·json) |
| 올리기 | 재개 가능 업로드: `POST upload/…?uploadType=resumable`(메타데이터) → `PUT` 본문 |
| 새 폴더·이름·휴지통·이동 | `files.create`, `files.update`(`trashed`, `addParents`/`removeParents`) |
| 같은 계정 복사 | `files.copy` (서버에서 복사, 내려받지 않음) |
| 영구 삭제 | `files.delete` |
| 계정·용량 | `about.get` |

- 401이면 토큰을 새로 받아 한 번 다시 보낸다. 그래도 401이면 `GDriveAuthException`("다시 로그인").
- 429, 5xx, `rateLimitExceeded`는 지수 백오프로 4번까지 다시 보낸다. 업로드 본문(PUT)은 다시 보내지 않는다(실패로 돌려준다).
- `storageQuotaExceeded`, 권한 없음, 프록시 인증 실패는 사용자에게 읽히는 문장으로 바꾼다.
- 공유 드라이브 항목도 다룰 수 있게 `supportsAllDrives=true`를 붙인다.

## 파일 작업 (`GDriveFileOperationService`, `GDriveTransfer`)

앱의 `IFileOperationService` 맨 앞에 끼워, gdrive:// 경로가 있으면 Drive가 처리하고 아니면 원래 서비스(Rust 복사 엔진 → 셸)로 넘긴다. F5/F6, 붙여넣기, 끌어 놓기 모두 `ShellViewModel.TransferAsync`를 거치므로 같은 길이다.

- 복사·이동: 먼저 전체를 훑어 양(바이트 + 항목마다 1)을 재고, 위에서부터 폴더를 만들고 파일을 옮긴다. 진행률은 작업 패널·마스코트로 간다.
- 충돌: 덮어쓰지 않고 `이름 (2)`로 만든다(데이터를 잃지 않는 쪽).
- 같은 계정 안의 이동은 부모만 바꾼다(즉시). 다른 계정으로는 임시 파일을 거쳐 내려받고 올린다(Google 문서는 Office 파일이 된다).
- 이동의 원본 정리: 로컬 원본은 모두 올린 뒤 지우고, Drive 원본은 Drive 휴지통으로 보낸다.
- 로컬로 받을 때는 `.foldy-part`에 받은 뒤 이름을 바꾼다. 수정 시각은 Drive 값으로 맞춘다.
- 내보낼 수 없는 Google 형식(설문지, 사이트 등)은 로컬에 `.url` 바로가기를 만든다.
- 삭제: Del은 Drive 휴지통, Shift+Del은 `files.delete`.
- SFTP ↔ Google Drive 직접 복사는 아직 없다(먼저 내 PC로).

## 열기·미리보기

- 열기(Enter): `IOpenFileService.PrepareAsync`가 `%LOCALAPPDATA%\Foldy\gdrive-cache\<id>\이름`에 내려받아 그 경로로 연다. 수정 시각·크기가 같으면 다시 받지 않는다. 4MB 넘으면 작업 패널에 진행률을 보인다.
  캐시 파일은 읽기 전용(고쳐도 Drive에 올라가지 않으므로).
- Google 문서는 내려받지 않고 브라우저에서 `webViewLink`를 연다.
- 미리보기(F3): 원격 파일이므로 **[미리보기]** 를 눌렀을 때만 `PreviewViewModel.RemoteFetch`로 캐시에 받아 로컬 파일처럼 읽는다.
- 목록 새로 고침: 감시가 없으므로 활성 탭만 60초마다 다시 읽는다(SFTP와 같음).

## 로그인 (OAuth 2.0, 설치형 앱)

RFC 8252 방식: 시스템 브라우저 + loopback 리디렉트 + PKCE(S256).

1. `127.0.0.1`의 임의 포트에 `TcpListener`를 연다(`HttpListener`는 URL 예약·관리자 권한이 필요할 수 있어 쓰지 않음). 루프백에만 바인딩한다.
2. 기본 브라우저로 `accounts.google.com/o/oauth2/v2/auth`를 연다(`access_type=offline`, `prompt=consent select_account`, `state`, `code_challenge`).
3. 리디렉트 한 번을 받아 `state`를 비교하고(상수 시간), 브라우저에 "Foldy로 돌아가세요" 페이지를 돌려준다. favicon 같은 다른 요청은 404. 5분 안에 끝나지 않으면 시간 초과.
4. code + verifier를 `oauth2.googleapis.com/token`에서 토큰으로 바꾸고, `about.get`으로 계정 이메일을 얻는다.

- 범위는 `https://www.googleapis.com/auth/drive` 하나. 파일 관리자는 사용자의 모든 파일을 다뤄야 해서 `drive.file`(앱이 만든 파일만)로는 부족하다. 이메일은 `about.get`에서 얻으므로 `openid`/`email` 범위도 쓰지 않는다.
- 저장: `%LOCALAPPDATA%\Foldy\gdrive.json` (`GDriveAccountStore`). 갱신 토큰은 DPAPI(현재 사용자 + 앱 고유 엔트로피)로 감싼 값만 쓴다. 임시 파일에 쓴 뒤 교체, 손상되면 `.corrupt`로 옮긴다.
- 액세스 토큰은 메모리에만 두고 만료 1분 전에 새로 받는다(동시 요청이 와도 한 번만).
- 연결 끊기: `oauth2.googleapis.com/revoke`로 취소를 요청하고(오프라인이면 건너뜀) 저장소·캐시에서 지운다.
- 네트워크: `CrashSender.CreateSystemProxyHandler()`와 같은 처리기(시스템 프록시 PAC/WPAD + 로그인 사용자 자격 증명으로 인증 프록시 통과).

### 클라이언트 ID·비밀값

저장소에 넣지 않는다. 쓰는 순서:

1. 사용자가 연결 창 "고급: 내 OAuth 클라이언트 ID"에 넣은 값 (비밀값은 DPAPI로 감싸 저장)
2. 환경 변수 `FOLDY_GOOGLE_CLIENT_ID` / `FOLDY_GOOGLE_CLIENT_SECRET` (개발용)
3. 빌드할 때 넣은 값: MSBuild 속성 `FoldyGoogleClientId` / `FoldyGoogleClientSecret` → `AssemblyMetadata`.
   `release.yml`은 GitHub **변수** `FOLDY_GOOGLE_CLIENT_ID`와 **Secret** `FOLDY_GOOGLE_CLIENT_SECRET`을 환경 변수로 넘긴다(MSBuild가 환경 변수를 속성으로 읽음). 없으면 빈 값으로 빌드되고, 사용자가 1번으로 넣는다.

설치형 앱의 클라이언트 비밀값은 배포된 프로그램에서 꺼낼 수 있으므로 진짜 비밀이 아니다(Google 문서도 그렇게 본다). 보안은 PKCE와 loopback 리디렉트가 맡는다. 그래도 저장소·로그에는 남기지 않는다.

계정마다 토큰을 받은 클라이언트 ID를 기억한다. 클라이언트를 바꾸면 그 계정은 원래 클라이언트로 갱신하고, 없으면 다시 로그인해야 한다.

## OAuth 클라이언트 만들기

휴대폰 브라우저로도 된다. 5~10분.

1. https://console.cloud.google.com 에 로그인 → 위쪽 프로젝트 고르기 → **새 프로젝트** → 이름 `Foldy` → 만들기.
2. 메뉴 **API 및 서비스 → 라이브러리** → `Google Drive API` 검색 → **사용**.
3. 메뉴 **Google 인증 플랫폼**(Google Auth Platform) → **시작하기**
   - 앱 이름 `Foldy`, 사용자 지원 이메일: 내 Gmail
   - 대상: **외부**
   - 연락처 이메일: 내 Gmail → 정책 동의 → **만들기**
4. **데이터 액세스** → **범위 추가 또는 삭제** → `https://www.googleapis.com/auth/drive` ("Google Drive의 모든 파일 보기, 수정, 생성, 삭제") 체크 → 업데이트 → **저장**.
5. **대상** → **테스트 사용자 → 사용자 추가** → 내 Gmail(그리고 Foldy로 연결할 다른 계정) → 저장.
6. **클라이언트** → **클라이언트 만들기** → 애플리케이션 유형 **데스크톱 앱** → 이름 `Foldy` → 만들기.
   클라이언트 ID(`….apps.googleusercontent.com`)와 클라이언트 보안 비밀번호를 복사한다.
7. 둘 중 하나:
   - 이 PC만: Foldy 사이드바 **Google Drive → 고급: 내 OAuth 클라이언트 ID**에 붙여 넣고 **[저장]**.
   - 모든 Foldy 설치본에 넣기: GitHub 저장소 **Settings → Secrets and variables → Actions**
     - Variables 탭: `FOLDY_GOOGLE_CLIENT_ID` = 클라이언트 ID
     - Secrets 탭: `FOLDY_GOOGLE_CLIENT_SECRET` = 보안 비밀번호
     - 다음 릴리스부터 들어간다.

### 테스트 모드와 게시

- **테스트** 상태(기본)에서는 테스트 사용자만 로그인할 수 있고, Google이 **7일마다 갱신 토큰을 만료**시킨다. 그러면 Foldy가 "로그인이 만료됐습니다"라고 알리고, 연결 창에서 같은 계정으로 다시 로그인하면 된다.
- 7일 만료를 없애려면 **대상 → 앱 게시**로 "프로덕션"으로 바꾼다. 검증을 받지 않은 앱이라 로그인할 때 "Google에서 확인하지 않은 앱" 경고가 나오는데, **고급 → Foldy(으)로 이동**을 누르면 된다. 검증 전에는 사용자 100명까지다.
- Drive 전체 범위는 Google이 "제한된 범위"로 분류한다. 일반 사용자에게 널리 배포하려면 Google 앱 검증과 보안 평가가 필요하다. 개인·소수 사용에는 위 방법으로 충분하다.

## 시험

`tests/Foldy.GoogleDrive.Tests` (Linux에서도 돈다): 경로 파싱, PKCE(RFC 7636 부록 B 값), 인증 URL, 토큰 교환·갱신(동시 요청 한 번), loopback 수신기(실제 TCP, state 불일치 거부), 계정 저장소(감싼 토큰, 손상 파일), HTTP API(쿼리 이스케이프, 401 갱신, 재시도, 오류 문장, 재개 가능 업로드), Provider(같은 이름, `/`, Google 문서, 바로가기, 쪽 넘김, 이름 바꾸기·새 폴더·휴지통, 캐시), 전송(로컬↔Drive, 충돌 이름, 이동 정리, 같은 계정 서버 복사·이동, 다른 계정, 자기 안으로 복사 거부, 취소).

실제 Google 계정으로는 Windows에서 손으로 확인해야 한다(아래 확인 목록).

## 실제 PC 확인 목록

- [ ] 연결 창 → Google 계정 연결 → 브라우저 로그인 → 내 드라이브 탭이 열림
- [ ] 큰 폴더(1000개 넘음) 목록, 폴더 이동, 주소창 직접 입력
- [ ] 내 PC → Drive 복사(F5), Drive → 내 PC 이동(F6), 붙여넣기, 끌어 놓기
- [ ] 이름 바꾸기, 새 폴더, Del(휴지통), Shift+Del
- [ ] 파일 열기(캐시), Google 문서 열기(브라우저), 미리보기 [미리보기] 버튼
- [ ] 회사 프록시(PAC/인증 프록시) 환경
- [ ] 연결 끊기 후 탭이 "연결되어 있지 않습니다"로 바뀜
