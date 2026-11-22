# Foldy MCP 서버 (AI 연결)

> 2026-11-21 처음 만듦 (0.9.23). 결정 경위는 [DECISIONS.md](DECISIONS.md)의 "AI 연결(MCP)".

## 무엇인가

Claude 같은 AI 앱이 Foldy를 통해 사용자 PC의 파일을 찾고 폴더를 볼 수 있게 하는 **MCP(Model Context Protocol) 서버**다.
사용자는 AI에게 "작년 견적서 찾아 줘", "D 드라이브에서 큰 폴더가 뭐야?", "이 폴더 설명 읽어 줘"처럼 묻고, AI가 Foldy 도구를 불러 답한다.

활용 예:

- 이름 일부만 기억나는 파일 찾기: 색인으로 PC 전체를 바로 찾고(로컬 NTFS), 회사 드라이브·Google Drive는 Foldy가 천천히 모아 둔 목록에서 찾는다.
- 디스크가 꽉 찼을 때: `folder_sizes`로 큰 폴더부터 짚어 가며 원인 찾기.
- 프로젝트 파악: `read_folder_note`(폴더 설명)와 `get_file_tags`(진행 중·완료)로 "어디까지 했지?" 정리.
- AI 코딩 도구(Claude Code·Codex·Cursor 등)에서 작업 폴더 밖 문서(회의록·사양서) 찾기.

## 사용자에게 보여 주는 예시

"이렇게 물어보세요" 예시는 `FoldyMcpTools.Examples` 한 곳에 두고 AI 연결 화면(누르면 복사)·README·도움말에 같은 것을 쓴다. 도구 하나 이상으로 답할 수 있는 것만 넣는다.

## 연결 제안 한 줄

이 PC에 AI 앱이 있고 그중 어느 것도 연결하지 않았으면(`McpClientConfig.SuggestTarget`), Foldy를 켜고 8초 뒤 창 위에 한 줄(`FoldyMcpTools.SuggestLine`, 무엇을 할 수 있는지 예시 하나를 담고 실행마다 돌아감): Claude Desktop에게 "작년 견적서 찾아 줘"라고 하면 Foldy가 내 PC에서 찾아 줘요. 읽기만 해요. [연결하기] [나중에] [다시 묻지 않기]. 45초 동안 그냥 두면 저절로 사라진다. [나중에]는 다음 실행에 다시, [다시 묻지 않기]는 설정 `AiSuggest=false`(설정 → 일반 → AI 연결에서 되돌림). 확인 창으로 묻지 않고, 첫 화면에 아이콘도 두지 않는다(더 보기·Ctrl+Space "AI 연결"). 시험 실행·문서 캡처에서는 띄우지 않는다.

## 어떻게 붙나 (사용자는 버튼 하나)

- Foldy → 더 보기(⋯) → **AI 연결 (MCP)…**, 또는 Ctrl+Space에서 "AI 연결".
- 이 PC에 깔린 AI 앱만 체크 목록으로 보인다. [AI에 연결]을 누르면 그 앱의 설정 파일에 Foldy를 넣는다(원래 파일은 `.foldy-backup`으로 남김, `foldy` 항목 하나만 건드림). 체크를 풀고 누르면 뺀다.
- 연결하면 "AI 연결 켜기"도 켜진다(기본은 꺼짐). 끄면 도구가 "꺼져 있어요"만 답한다(앱 설정은 그대로).
- 아래에 도구 목록(이름·설명·입력값, Swagger처럼)과 직접 붙이기용 설정(복사 단추)이 있다.

| AI 앱 | 설정 파일 | 형식 |
| --- | --- | --- |
| Claude Desktop | `%APPDATA%\Claude\claude_desktop_config.json` (Store판은 패키지 폴더 안) | `mcpServers.foldy = { command, args }` |
| Claude Code | `%USERPROFILE%\.claude.json` (사용자 범위) | `mcpServers.foldy = { type: "stdio", command, args }` |
| Codex (CLI·앱) | `%USERPROFILE%\.codex\config.toml` | `[mcp_servers.foldy]` `command`, `args` |
| Gemini CLI | `%USERPROFILE%\.gemini\settings.json` | `mcpServers.foldy` |
| Cursor | `%USERPROFILE%\.cursor\mcp.json` | `mcpServers.foldy` |
| VS Code | `%APPDATA%\Code\User\mcp.json` | `servers.foldy = { type: "stdio", … }` |

실행 명령은 모두 `Foldy.exe --mcp`.
**ChatGPT는 넣지 않는다**: 인터넷(원격) MCP만 받아서, PC 안의 Foldy를 밖에 열어야 한다. 보안상 하지 않는다.

## 서버 (Foldy.exe --mcp)

- 공식 MCP 사양, **stdio** 전송(JSON-RPC 2.0, 줄마다 메시지 하나, UTF-8). 사양 버전 2025-06-18 / 2025-03-26 / 2024-11-05 중 클라이언트가 말한 것을 쓴다.
- 지원 메서드: `initialize`, `notifications/initialized`, `ping`, `tools/list`, `tools/call`. 나머지는 -32601.
- 창을 띄우지 않고, 관리자 해제·단일 인스턴스·로그를 건너뛴다(`App.OnStartup` 맨 앞). AI 앱이 입력을 닫으면 끝난다.
- 코드: 프로토콜·도구 정의 `src/Foldy.Core/Mcp/` (McpServer, FoldyMcpTools, McpClientConfig), 앱 연결 `src/Foldy.App/Services/McpRunner.cs`, 화면 `src/Foldy.App/McpWindow.cs`. 도구 이름·설명·입력값은 서버와 화면이 같은 정의를 쓴다.

## 도구 (처음 버전은 모두 읽기 전용)

| 도구 | 하는 일 | 입력 |
| --- | --- | --- |
| `find_files` | 이름에 낱말이 든 파일·폴더 찾기 (색인 → 천천히 모은 목록 → 폴더 직접 훑기) | `query`(꼭), `folder`, `limit`(기본 50, 최대 500) |
| `list_folder` | 폴더 안 보기 (숨김 파일은 Foldy 설정을 따름) | `path`(꼭), `limit`(기본 200, 최대 2000) |
| `folder_sizes` | 하위 폴더·파일을 큰 순서로 (색인 드라이브) | `path`(꼭), `limit`(기본 30) |
| `read_folder_note` | 폴더 설명(.folder_description.md 또는 이 PC 저장) | `path`(꼭) |
| `get_file_tags` | 파일 상태 태그 | `paths`(꼭, 목록) |
| `drive_status` | 드라이브·남은 공간·색인 상태 | 없음 |

모든 도구에 annotations(`readOnlyHint: true`, `destructiveHint: false`, `openWorldHint: false`)를 단다. 결과는 사람이 읽는 글 + `structuredContent`.
옮기기·지우기·이름 바꾸기 같은 쓰기 도구는 넣지 않았다. 넣게 되면 Foldy 확인 창을 거치게 한다(위험한 일만 확인 원칙).

## 보안

- **권한 우회 없음**: 서버는 AI 앱이 띄운 사용자 계정으로 돈다. 폴더 보기는 그 계정의 권한 그대로, 색인 검색은 색인 서비스가 파이프로 연결한 사용자 기준으로 거른다(다른 사용자 프로필은 안 보임).
- 경로는 드라이브 문자로 시작하는 전체 경로만. 네트워크 경로(`\\서버\공유`, `//서버/공유`), 장치 경로(`\\?\`, `\\.\`), 상대 경로는 거절 (2026-11-22 보안 리뷰 V2: 네트워크 경로는 확인만 해도 Windows가 그 서버에 로그인 정보(NTLM)를 보낸다. 숨은 지시에 속은 AI가 모르는 서버를 넣어도 새지 않게). 회사 드라이브는 연결된 드라이브 문자(`K:\…`)로 쓴다. `get_file_tags`의 경로도 같은 검사를 거친다.
- 네트워크로 열지 않는다(stdio만). 결과는 그 AI 앱으로만 간다. AI 앱이 결과를 어디로 보내는지는 그 앱의 정책을 따른다(README·PRIVACY에 적음).
- 기본은 꺼짐. 사용자가 [AI에 연결]을 누르거나 켜기를 체크해야 답한다.

## 시험

- 자동: `McpServerTests`(사양 버전 맞추기, 알림, 오류 코드, 도구 목록·스키마·annotations, 끄면 답하지 않음, 입력값 전달, 경로 거절, 숨김 파일, stdio 줄 단위), `McpClientConfigTests`(다른 설정 보존·백업, 고장 난 JSON은 그대로, 앱마다 형식, Codex TOML 넣기·바꾸기·빼기, 깔린 앱 찾기).
- 수동: TESTING.md M16.
