# Java 프로젝트 사용법

> Maven과 Gradle이 함께 쓰는 표준 구조입니다. 폴더 위치만 맞으면 두 도구 모두 따로 설정 없이 소스를 찾습니다.

## 폴더별로 무엇을 넣나

| 폴더 | 넣는 것 | 넣지 않는 것 |
|---|---|---|
| **src/main/java** | 배포되는 코드. 안쪽은 패키지 폴더(com/조직/앱) | 테스트 코드 |
| **src/main/resources** | 설정 파일, 메시지, 템플릿 등 클래스패스 리소스 | 비밀번호·키 |
| **src/test/java** | 테스트. main과 같은 패키지 구조 | 실서비스 코드 |
| **src/test/resources** | 테스트에만 쓰는 데이터·설정 | 큰 샘플 파일 |
| **docs** | 사람이 읽는 문서 | 자동 생성된 javadoc 결과 |

패키지 폴더는 입력한 **조직**과 **앱** 이름으로 만들어집니다. 그룹 ID가 `com.example`이면 조직에 `example`, 앱에 `app`을 넣으세요.

## 예시 트리

```
my-app/
├─ src/
│  ├─ main/
│  │  ├─ java/com/example/app/App.java
│  │  └─ resources/
│  └─ test/
│     ├─ java/com/example/app/
│     └─ resources/
├─ docs/
├─ build.gradle.kts   (Maven이면 pom.xml)
├─ README.md
└─ .gitignore
```

## 이름 규칙

| 대상 | 규칙 | 예 |
|---|---|---|
| 클래스 파일 | PascalCase, 파일명 = 클래스명 | `OrderService.java` |
| 패키지 | 소문자만 | `com.example.app.order` |
| 테스트 | 대상 클래스 + Test | `OrderServiceTest.java` |

## 유지 요령

- 빌드 결과(target, build, .gradle)는 .gitignore에 넣고 커밋하지 않습니다.
- 패키지 이름과 폴더 구조는 항상 같아야 합니다. 옮길 때는 IDE의 이동 기능을 쓰세요.
- 한 번 정한 그룹 ID(조직)는 바꾸기 어려우니 처음에 정합니다.
- 이 폴더는 Foldy의 폴더 정리에서 **프로젝트 폴더로 통째로** 다뤄지며 안쪽이 쪼개지지 않습니다.


## 이럴 땐 다른 스타일을

- 언어를 정하지 않은 일반 저장소라면 → **소프트웨어 프로젝트**
- 문서·설계가 중심이라면 → **문서 작성 단계**
