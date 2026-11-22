# Foldy 알리기 계획

목표: 전 세계 사람들이 "Windows file manager", "File Explorer alternative", "disk space analyzer" 같은 말로 검색했을 때 Foldy를 찾게 하기. 많은 사람에게 도움이 되도록.

원칙
- 대표 문구는 그대로: **"빠르고 똑똑한 파일 관리자, 오프라인에서도"** + 작은 줄 "AI와 연결하면 더 편하게". "AI 폴더관리툴"이라 부르지 않는다.
- 공개 글·그림에 회사 정보 금지 ([no-company-info.md](no-company-info.md)).
- 정직하게: 코드 서명 전이라 SmartScreen 경고가 뜬다는 것, 영어 화면은 번역 중이라는 것을 숨기지 않는다.
- 외부 사이트에 글을 올리거나 제출하는 일은 **사용자가 확인한 뒤에만** 한다. 계정이 필요한 곳은 사용자 계정으로.
- 커뮤니티마다 자기 홍보 규칙이 있다. 올리기 전에 규칙을 읽고, 같은 글을 여러 곳에 한꺼번에 뿌리지 않는다. 댓글에는 직접 답한다.

## 1단계: 저장소와 사이트 (끝, 이 PR)

| 무엇 | 어디 |
| --- | --- |
| 언어별 제목·설명에 "Windows 파일 관리자" 넣기, 검색어(keywords) | `site/src/i18n/*.ts` `meta` |
| 구조화 데이터: 앱(SoftwareApplication: 버전·다운로드·기능·무료·라이선스), 사이트(WebSite), 자주 묻는 질문(FAQPage) | `site/src/layouts/Base.astro` |
| 공유 미리보기: og:image:alt, twitter 제목·설명·그림 | 같은 파일 |
| Google·Bing 소유 확인 메타 태그 (저장소 변수 `GOOGLE_SITE_VERIFICATION`, `BING_SITE_VERIFICATION`) | `Base.astro`, `.github/workflows/pages.yml` |
| 언어별 주소(hreflang)·sitemap·robots.txt | 이미 있음 |
| README 첫머리 영어 한 줄, 영문 README에 검색어·사이트 링크·SmartScreen 안내 | `README.md`, `README.en.md` |
| 릴리스 설명 맨 위에 무엇인지 영어·한국어 한 줄과 사이트 주소 | `.github/workflows/release.yml` |

## 2단계: 사용자 계정으로 한 번만 (5분)

1. **GitHub 저장소 정보** (저장소 첫 화면 오른쪽 About 옆 ⚙):
   - Description: `Fast, smart file manager for Windows, even offline. Ctrl+Space search, disk space analyzer, Desktop & Downloads cleanup. Free & open source.`
   - Website: `https://chadolkr.github.io/Foldy/`
   - Topics (20개까지): `file-manager` `windows` `file-explorer` `explorer-alternative` `dual-pane` `windows-11` `windows-10` `file-search` `desktop-search` `disk-space-analyzer` `disk-usage` `treemap` `file-organizer` `sftp-client` `mcp-server` `wpf` `csharp` `rust` `offline-first` `gpl3`
   - Social preview (Settings → General → Social preview): `site/public/og/en.png` 올리기.
2. **Google Search Console** (search.google.com/search-console): "URL 접두어"로 `https://chadolkr.github.io/Foldy/` 추가 → "HTML 태그" 방법의 content 값을 저장소 Settings → Secrets and variables → Actions → Variables에 `GOOGLE_SITE_VERIFICATION`으로 넣기 → Site 작업 다시 실행 → 확인 → Sitemaps에 `sitemap-index.xml` 제출.
3. **Bing Webmaster Tools** (bing.com/webmasters): Search Console에서 가져오기를 고르면 따로 할 게 없다. 아니면 메타 태그 값을 `BING_SITE_VERIFICATION`에. Bing에 들어가면 DuckDuckGo·Yahoo·ChatGPT 검색에도 잡힌다.
4. **Naver 서치어드바이저** (searchadvisor.naver.com): 한국어 검색용. 사이트 등록 → HTML 태그 확인이 필요하면 알려 주면 변수 하나 더 만든다.

## 3단계: 설치 도구 등록 (사용자 확인 뒤)

| 어디 | 어떻게 | 추천 |
| --- | --- | --- |
| **winget** (Windows 기본 설치 도구) | microsoft/winget-pkgs에 매니페스트 PR. `wingetcreate new <MSI 주소>`로 만들고, 다음부터는 릴리스 작업에서 `wingetcreate update` 자동화. 서명 없는 MSI도 받아 준다. `winget install Foldy` 한 줄로 설치되고 SmartScreen 화면도 안 거친다. | ★ 먼저 |
| **Microsoft Store** | 이미 예약(Foldy File Manager, 9NS1PWL5QGP6). Store 판이 나오면 가장 믿음을 주는 경로. 사이트·README에 Store 배지 추가. | ★ 진행 중 ([store.md](store.md)) |
| **Chocolatey** | community.chocolatey.org에 패키지 올리기. 사람이 검토해서 며칠 걸림. | 보통 |
| **Scoop** | 설치 없이 푸는 앱 위주라, SYSTEM 색인 서비스가 있는 Foldy MSI와는 잘 맞지 않는다. | 나중에 |

## 4단계: 목록·커뮤니티 (사용자 확인 뒤, 영어 화면 번역이 끝난 뒤가 좋음)

지금 앱 화면은 번역이 진행 중이고 사이트 그림도 한국어다. 영어권 큰 커뮤니티(Hacker News, Reddit, Product Hunt)는 **영어 화면과 영어 그림이 준비된 뒤 한 번에** 올리는 게 좋다. 첫인상은 한 번뿐이기 때문이다. 한국어 커뮤니티와 목록 등록은 지금 해도 된다.

| 언어 | 곳 | 메모 |
| --- | --- | --- |
| 공통 | **AlternativeTo** | File Explorer, Total Commander, Directory Opus, Files, WizTree, Everything의 대안으로 등록. 무료·오픈소스·Windows 태그. |
| 공통 | **awesome 목록** | Awesome-Windows/Awesome (File Management), punkpeye/awesome-mcp-servers (File Systems, Foldy에는 읽기 전용 MCP 서버가 있음). 목록마다 넣는 규칙(별 개수·형식)을 따른다. |
| en | Show HN, r/software, r/opensource, r/Windows11, Product Hunt | 화요일~목요일 미국 오전. 자기 홍보 규칙 확인. |
| ko | GeekNews(Show GN), 클리앙, 디스콰이엇 | 지금 가능. |
| ja | 窓の杜 소프트웨어 정보 보내기, Vector 등록, Zenn·Qiita 소개 글 | 일본은 프리웨어 소개 사이트 영향이 크다. |
| es | r/programacion, r/software(영어), Genbeta 제보 | |
| zh-CN | V2EX 分享创造, 小众软件 发现频道, 少数派 Matrix | |

게시 초안(5개 언어)은 프로젝트 파일 `promotion/posts.md`에 있다. 올릴 때 버전·숫자를 최신으로 바꾼다.

## 알아 두기

- **SmartScreen**: 코드 서명(Certum 예정)이나 Store 판이 나오기 전에는 "Windows의 PC 보호" 화면이 뜬다. 글마다 한 줄로 미리 알리고, winget 설치를 함께 안내한다.
- **숫자는 실측만**: 0.003초(217만 개, 노트북), 10만 개 폴더 0.03초.
- 반응 보기: GitHub Insights → Traffic(어디서 왔는지), Search Console(무슨 말로 찾았는지). 광고·방문 통계 스크립트는 사이트에 넣지 않는다.
