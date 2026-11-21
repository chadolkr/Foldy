// Foldy 소개 사이트 (GitHub Pages: https://chadolkr.github.io/Foldy/)
// 한국어가 기본이고 주소 앞에 붙지 않는다. 영어를 더할 때는 locales에 'en'을 넣고
// src/i18n/en.ts와 src/pages/en/index.astro를 만들면 /Foldy/en/ 이 생긴다.
import { defineConfig } from 'astro/config';

export default defineConfig({
  site: 'https://chadolkr.github.io',
  base: '/Foldy',
  trailingSlash: 'ignore',
  i18n: {
    defaultLocale: 'ko',
    locales: ['ko'],
    routing: { prefixDefaultLocale: false },
  },
  vite: {
    // 화면 사진은 저장소의 docs/images를 그대로 쓴다 (사이트에 사본을 두지 않음).
    server: { fs: { allow: ['..'] } },
  },
});
