// Foldy 소개 사이트 (GitHub Pages: https://chadolkr.github.io/Foldy/)
// 한국어가 기본이고 주소 앞에 붙지 않는다. 다른 언어는 /Foldy/en/ 처럼. 언어 목록은 src/i18n/index.ts.
import { defineConfig } from 'astro/config';
import sitemap from '@astrojs/sitemap';

const locales = { ko: 'ko', en: 'en', es: 'es', ja: 'ja', 'zh-cn': 'zh-CN' };

export default defineConfig({
  site: 'https://chadolkr.github.io',
  base: '/Foldy',
  trailingSlash: 'ignore',
  i18n: {
    defaultLocale: 'ko',
    locales: Object.keys(locales),
    routing: { prefixDefaultLocale: false },
  },
  integrations: [sitemap({ i18n: { defaultLocale: 'ko', locales } })],
  vite: {
    // 화면 사진은 저장소의 docs/images를 그대로 쓴다 (사이트에 사본을 두지 않음).
    server: { fs: { allow: ['..'] } },
  },
});
