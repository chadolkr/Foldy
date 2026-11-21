import ko from './ko';

// 새 언어: 같은 모양의 파일(en.ts)을 만들고 여기에 넣는다.
export const dictionaries = { ko } as const;
export type Locale = keyof typeof dictionaries;
export type Dictionary = typeof ko;
