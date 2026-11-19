# Claude 스킬 하나 사용법

> 스킬은 Claude가 필요할 때 꺼내 쓰는 작업 설명서예요. 폴더 하나에 SKILL.md 하나가 기본이에요.

| 위치 | 넣는 것 |
|---|---|
| **SKILL.md** | 맨 위 `---` 사이에 `name`, `description`. 그 아래 언제 쓰는지와 순서 |
| **reference.md** | 긴 설명. SKILL.md에서 링크해 두면 필요할 때만 읽혀요 |
| **scripts/** | 스킬이 실행하는 도우미 스크립트 |

프로젝트의 `.claude/skills/` 안에 두면 그 프로젝트에서, 사용자 폴더의 `.claude/skills/`에 두면 어디서나 쓰여요.

## 이럴 땐 다른 스타일을

- 프로젝트 전체를 Claude와 함께 쓰려면 → **Claude Code 프로젝트**
- 프롬프트 실험을 모으려면 → **AI 작업 폴더**
