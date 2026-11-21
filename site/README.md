# Foldy 소개 사이트

<https://chadolkr.github.io/Foldy/> 에 올라가는 제품 소개 페이지예요. [Astro](https://astro.build)로 만들고 GitHub Pages에 무료로 올려요.

```sh
cd site
npm install
npm run dev      # http://localhost:4321/Foldy/
npm run build    # dist/ 에 만들어짐
```

- **글자**: 모두 `src/i18n/ko.ts`에 있어요. 문구를 고칠 때는 이 파일만 고치면 돼요.
- **화면 사진**: 저장소의 `docs/images`를 그대로 가져와요(사본 없음). README의 사진을 새로 찍으면 사이트도 같이 바뀌어요.
- **다운로드 단추**: 만들 때 GitHub에서 최신 릴리스를 읽어 MSI 주소와 버전을 넣어요. 못 읽으면 Releases 최신 페이지로 보내요.
- **배포**: `main`에 `site/`가 바뀌거나 새 릴리스가 나오면 `.github/workflows/pages.yml`이 다시 만들어 올려요.

## 영어 더하기

1. `src/i18n/ko.ts`를 복사해 `src/i18n/en.ts`로 만들고 글자만 바꿔요 (`lang: 'en'`).
2. `src/i18n/index.ts`의 `dictionaries`에 `en`을 넣어요.
3. `astro.config.mjs`의 `locales`에 `'en'`을 넣어요.
4. `src/pages/en/index.astro`를 `index.astro`처럼 만들고 `dictionaries.en`을 넘겨요. → `/Foldy/en/`
