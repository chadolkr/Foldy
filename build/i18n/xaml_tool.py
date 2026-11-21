# -*- coding: utf-8 -*-
"""
XAML의 한국어 글을 언어 파일 키로 바꾸는 도구.
  python build/i18n/xaml_tool.py dump <xaml> <접두어>   → 한국어 글 목록(접두어.번호)을 출력
  python build/i18n/xaml_tool.py apply <xaml>           → xaml_strings.py에 있는 한국어 글을 {loc:T 키}로 바꾼다
번역은 xaml_strings.py에 (키, ko, en, es, ja, zh-Hans)로 적는다.
"""
import os, re, sys

HERE = os.path.dirname(os.path.abspath(__file__))
ATTR = re.compile(r'((?:Header|Text|ToolTip|Content|AutomationProperties\.Name|Title|Tag)=")([^"{]*[가-힣][^"]*)(")')


def strip_comments(s):
    return re.sub(r'<!--.*?-->', lambda m: ' ' * len(m.group(0)), s, flags=re.S)


def dump(path, prefix):
    s = strip_comments(open(path, encoding='utf-8').read())
    seen = []
    for m in ATTR.finditer(s):
        if m.group(2) not in seen:
            seen.append(m.group(2))
    for i, v in enumerate(seen):
        print(f'{prefix}.{i}\t{v}')


def apply(path):
    sys.path.insert(0, HERE)
    from xaml_strings import XAML  # noqa
    from shell_strings import SHELL  # noqa
    by_ko = {}
    for k, ko, *_ in XAML + SHELL:
        by_ko.setdefault(ko, k)
    raw = open(path, encoding='utf-8').read()
    masked = strip_comments(raw)
    out, last, left = [], 0, []
    for m in ATTR.finditer(masked):
        if m.group(1).startswith('Tag='):
            continue  # Tag는 명령 id 등 데이터
        v = raw[m.start(2):m.end(2)]
        if v in by_ko:
            out.append(raw[last:m.start(2)])
            out.append('{loc:T ' + by_ko[v] + '}')
            last = m.end(2)
        else:
            left.append(v)
    out.append(raw[last:])
    t = ''.join(out)
    if 'xmlns:loc=' not in t:
        inside_ui = os.sep + 'Foldy.UI' + os.sep in os.path.abspath(path)
        ns = 'clr-namespace:Foldy.UI.Localization' + ('' if inside_ui else ';assembly=Foldy.UI')
        t = t.replace('xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"', 'xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"\n             xmlns:loc="' + ns + '"', 1)
    open(path, 'w', encoding='utf-8').write(t)
    print(os.path.basename(path), 'left', len(left))
    for v in left:
        print('   ', v)


if __name__ == '__main__':
    if sys.argv[1] == 'dump':
        dump(sys.argv[2], sys.argv[3])
    else:
        for p in sys.argv[2:]:
            apply(p)
