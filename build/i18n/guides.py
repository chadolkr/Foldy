# -*- coding: utf-8 -*-
"""템플릿 사용법 번역: build/i18n/guides_<언어>.py 의 G[id] → assets/templates/<id>.guide.<언어>.md
한국어 원본은 assets/templates/<id>.guide.md. 원본을 고치면 언어판도 같은 커밋에서 고친다."""
import glob, os, sys

HERE = os.path.dirname(os.path.abspath(__file__))
TPL = os.path.join(os.path.dirname(os.path.dirname(HERE)), 'assets', 'templates')
sys.path.insert(0, HERE)
ids = sorted(os.path.basename(p)[:-len('.guide.md')] for p in glob.glob(os.path.join(TPL, '*.guide.md')) if os.path.basename(p).count('.') == 2)
for f in sorted(glob.glob(os.path.join(HERE, 'guides_*.py'))):
    mod = __import__(os.path.splitext(os.path.basename(f))[0])
    missing = [i for i in ids if i not in mod.G]
    if missing:
        sys.exit(f'{mod.LANG}: 사용법 번역이 빠짐: {missing}')
    for i in ids:
        with open(os.path.join(TPL, f'{i}.guide.{mod.LANG}.md'), 'w', encoding='utf-8', newline='\n') as fp:
            fp.write(mod.G[i].lstrip('\n'))
    print(mod.LANG, len(ids), 'guides')
