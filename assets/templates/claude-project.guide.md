# Claude Code 프로젝트 사용법

> Claude가 이 프로젝트를 잘 이해하고 반복 작업을 스스로 꺼내 쓰게 만드는 기본 폴더예요.

| 위치 | 넣는 것 |
|---|---|
| **CLAUDE.md** | Claude가 가장 먼저 읽는 안내: 프로젝트 설명, 빌드·테스트 명령, 지켜 줄 것 |
| **.claude/skills/이름/SKILL.md** | 작업 설명서. 맨 위에 `name`, `description`을 적으면 Claude가 필요할 때 꺼내 써요 |
| **.claude/agents/** | 따로 일을 맡길 보조 에이전트 정의 (.md 파일) |
| **.mcp.json** | 이 프로젝트에서 쓸 MCP 서버 목록 |

Foldy는 `.claude`, `CLAUDE.md`, `AGENTS.md`가 있는 폴더를 AI 프로젝트로 보고, 정리 도구가 구조를 건드리지 않아요.

## 이럴 땐 다른 스타일을

- 스킬 하나만 따로 만들고 싶다면 → **Claude 스킬 하나**
- 프롬프트·데이터·결과를 모으는 실험이라면 → **AI 작업 폴더**
