// 언어별 공유 그림(1200×630)을 public/og/<언어>.png 로 만든다. 문구를 바꿨을 때만 다시 돌린다.
//   cd site && node scripts/make-og.mjs      (Playwright 필요: npx playwright install chromium)
import { readFileSync, writeFileSync, mkdirSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { createRequire } from 'node:module';

const require = createRequire(import.meta.url);
const { chromium } = require('playwright');
const here = (p) => fileURLToPath(new URL(p, import.meta.url));
const dataUri = (p) => `data:image/png;base64,${readFileSync(here(p)).toString('base64')}`;
const icon = dataUri('../../assets/icons/png/foldy-256.png');
const shot = dataUri('../../docs/images/main.png');

// 사전은 TypeScript라 여기서는 필요한 두 줄만 꺼낸다.
const pick = (file) => {
  const src = readFileSync(here(`../src/i18n/${file}.ts`), 'utf8');
  const hero = src.slice(src.indexOf('hero: {'));
  const get = (k) => JSON.parse(`"${hero.match(new RegExp(`${k}: '((?:[^'\\\\]|\\\\.)*)'`))[1].replace(/"/g, '\\"')}"`);
  return { title: get('title'), sub: get('sub'), lang: src.match(/lang: '([^']+)'/)[1] };
};
const langs = { ko: 'ko', en: 'en', es: 'es', ja: 'ja', 'zh-cn': 'zh-cn' };

mkdirSync(here('../public/og'), { recursive: true });
const browser = await chromium.launch();
const page = await browser.newPage({ viewport: { width: 1200, height: 630 } });
for (const [path, file] of Object.entries(langs)) {
  const { title, sub, lang } = pick(file);
  const cjk = lang === 'ja' ? "'Noto Sans CJK JP','Noto Sans JP','Yu Gothic UI','IPAGothic'" : lang === 'zh-CN' ? "'Noto Sans CJK SC','Noto Sans SC','Microsoft YaHei','WenQuanYi Zen Hei'" : "'Noto Sans CJK KR'";
  const font = `'Pretendard',${cjk}`;
  const size = { es: 46, ja: 38, 'zh-CN': 44 }[lang] ?? 52;
  await page.setContent(`<!doctype html><html lang="${lang}"><body style="margin:0;width:1200px;height:630px;overflow:hidden;background:#f7f6f3;font-family:${font},sans-serif;color:#1d1b18">
    <div style="position:absolute;left:72px;top:72px;width:620px">
      <div style="display:flex;align-items:center;gap:16px;font-size:40px;font-weight:800"><img src="${icon}" width="72" height="72">Foldy</div>
      <div style="margin-top:44px;font-size:${size}px;line-height:1.25;font-weight:800;letter-spacing:-0.02em;word-break:${lang === 'ko' ? 'keep-all' : 'normal'}">${title.replace('\n', '<br>')}</div>
      <div style="margin-top:24px;font-size:28px;font-weight:600;color:#c75f00">${sub}</div>
      <div style="margin-top:28px;font-size:22px;color:#57524a">Windows 10 · 11 · GPL-3.0</div>
    </div>
    <img src="${shot}" style="position:absolute;left:700px;top:96px;width:820px;border-radius:14px;border:1px solid #e2ded6;box-shadow:0 20px 50px -10px rgba(30,20,10,.25)">
    <div style="position:absolute;left:0;right:0;bottom:0;height:10px;background:linear-gradient(90deg,#ffb24d,#f57f0f)"></div>
  </body></html>`);
  await page.waitForTimeout(150);
  writeFileSync(here(`../public/og/${path}.png`), await page.screenshot({ type: 'png' }));
  console.log('og', path);
}
await browser.close();
