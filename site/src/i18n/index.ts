import ko from './ko';
import en from './en';
import es from './es';
import ja from './ja';
import zhCN from './zh-cn';

// 사이트 언어. 한국어가 기본(주소 앞에 붙지 않음), 나머지는 /Foldy/<path>/.
// appLang: 앱 화면 언어 코드(assets/i18n, assets/help/images/<appLang>/).
// 새 언어: ko.ts와 같은 모양의 파일을 만들고 여기 한 줄, astro.config.mjs의 locales에 path를 더한다.
export const locales = [
  { path: 'ko', appLang: 'ko', htmlLang: 'ko', ogLocale: 'ko_KR', name: '한국어', dict: ko },
  { path: 'en', appLang: 'en', htmlLang: 'en', ogLocale: 'en_US', name: 'English', dict: en },
  { path: 'es', appLang: 'es', htmlLang: 'es', ogLocale: 'es_ES', name: 'Español', dict: es },
  { path: 'ja', appLang: 'ja', htmlLang: 'ja', ogLocale: 'ja_JP', name: '日本語', dict: ja },
  { path: 'zh-cn', appLang: 'zh-Hans', htmlLang: 'zh-CN', ogLocale: 'zh_CN', name: '简体中文', dict: zhCN },
] as const;

export const defaultLocale = 'ko';
export type Locale = (typeof locales)[number];
export type Dictionary = typeof ko;

const base = import.meta.env.BASE_URL.replace(/\/?$/, '/');
/** 그 언어 첫 페이지 주소 (base 포함) */
export const localeHref = (path: string) => (path === defaultLocale ? base : `${base}${path}/`);
