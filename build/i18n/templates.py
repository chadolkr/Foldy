# -*- coding: utf-8 -*-
"""
내장 템플릿(assets/templates/*.foldy-template.json, wizard.json)의 글을 다른 언어로.
  python build/i18n/templates.py          → assets/templates/i18n/<언어>.json (한국어 글 → 번역) 을 만든다
  python build/i18n/templates.py dump     → 번역이 빠진 한국어 글을 출력
원칙 (DECISIONS 2026-11-22): 폴더는 만드는 그 시점의 앱 언어로 이름을 붙인다. 이미 만든 폴더는 바꾸지 않는다.
- 폴더 이름(name, target, folder)은 '/'로 나눈 조각마다 번역한다 (트리 이름과 정리 규칙 target이 늘 같게).
- {{변수}} 자리는 그대로 둔다 (변수 키는 바꾸지 않는다).
- id, key, when, file, regex, extensions, nameEn, weights 같은 값은 건드리지 않는다.
- keywords(파일 이름에서 찾는 말)는 바꾸지 않고 번역을 덧붙인다 (Foldy.Core TemplateI18n).
번역 표는 build/i18n/templates_tr*.py 의 TR: {한국어: (en, es, ja, zh-Hans)}.
"""
import glob, json, os, re, sys

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(os.path.dirname(HERE))
TPL = os.path.join(ROOT, 'assets', 'templates')
LANGS = ['en', 'es', 'ja', 'zh-Hans']
HANGUL = re.compile(r'[가-힣]')
SKIP = {'groups', 'id', 'key', 'when', 'file', 'regex', 'extensions', 'nameEn', 'weights', 'schemaVersion', 'value', 'templates', 'template'}
PATHS = {'name', 'target', 'folder'}


def is_path(key, in_tree):
    return key in ('target', 'folder') or (key == 'name' and in_tree)


def pieces(key, s, in_tree=False):
    """번역 단위: 폴더 이름은 '/'·'\' 조각, {{…}}만 있는 조각은 뺀다."""
    if is_path(key, in_tree):
        return [p for p in re.split(r'[/\\]', s) if HANGUL.search(re.sub(r'\{\{[^}]*\}\}', '', p))]
    return [s] if HANGUL.search(re.sub(r'\{\{[^}]*\}\}', '', s)) else []


def collect():
    out = []
    files = sorted(glob.glob(os.path.join(TPL, '*.foldy-template.json'))) + [os.path.join(TPL, 'wizard.json')]
    for f in files:
        d = json.load(open(f, encoding='utf-8'))

        def walk(o, key=None, in_tree=False):
            if isinstance(o, dict):
                for k, v in o.items():
                    if k not in SKIP:
                        walk(v, k, in_tree or k == 'tree')
            elif isinstance(o, list):
                for v in o:
                    walk(v, key, in_tree)
            elif isinstance(o, str):
                for p in pieces(key, o, in_tree):
                    if p not in out:
                        out.append(p)
        walk(d)
    return out


def table():
    sys.path.insert(0, HERE)
    tr = {}
    for f in sorted(glob.glob(os.path.join(HERE, 'templates_tr*.py'))):
        mod = __import__(os.path.splitext(os.path.basename(f))[0])
        for k, v in mod.TR.items():
            assert len(v) == 4, k
            tr[k] = v
    return tr


def main():
    texts = collect()
    tr = table()
    if len(sys.argv) > 1 and sys.argv[1] == 'dump':
        for t in texts:
            if t not in tr:
                print(t)
        return
    missing = [t for t in texts if t not in tr]
    if missing:
        sys.exit('번역이 빠진 템플릿 글 %d개 (python build/i18n/templates.py dump): %s' % (len(missing), missing[:5]))
    os.makedirs(os.path.join(TPL, 'i18n'), exist_ok=True)
    for i, lang in enumerate(LANGS):
        m = {t: tr[t][i] for t in texts}
        for k, v in m.items():
            if HANGUL.search(re.sub(r'\{\{[^}]*\}\}', '', v)):
                sys.exit(f'{lang}: 한국어가 남음: {k} → {v}')
            if re.findall(r'\{\{[^}]*\}\}', k) != re.findall(r'\{\{[^}]*\}\}', v):
                sys.exit(f'{lang}: {{{{변수}}}} 자리가 다름: {k} → {v}')
        with open(os.path.join(TPL, 'i18n', lang + '.json'), 'w', encoding='utf-8', newline='\n') as fp:
            json.dump(m, fp, ensure_ascii=False, indent=1)
            fp.write('\n')
    print(len(texts), 'template texts x', len(LANGS), 'languages')


if __name__ == '__main__':
    main()
