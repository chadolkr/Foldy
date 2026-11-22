# -*- coding: utf-8 -*-
"""
C# 파일의 한국어 문자열을 언어 파일 키로 바꾸는 도구.
  python build/i18n/cs_tool.py dump <file.cs> <접두어>  → 보간 없는 한국어 문자열("…")과 보간 문자열($"…")을 나눠 출력
  python build/i18n/cs_tool.py apply <file.cs> ...       → cs_strings.py에 있는 보간 없는 문자열을 Loc.T("키")로 바꾼다
보간 문자열은 손으로 Loc.F("키", 값…)로 바꾼다 ({0} 자리 표시자). 주석·const·attribute 안의 글은 건드리지 않는다.
"""
import os, re, sys

HERE = os.path.dirname(os.path.abspath(__file__))
LIT = re.compile(r'(?<![@$\w"])"((?:[^"\\\n]|\\.)*[가-힣](?:[^"\\\n]|\\.)*)"')
INTERP = re.compile(r'\$@?"((?:[^"\\\n]|\\.)*[가-힣](?:[^"\\\n]|\\.)*)"')


def mask_comments(s):
    out = []
    for line in s.split('\n'):
        i = line.find('//')
        # 문자열 안의 // 는 거의 없다고 보고 단순 처리 (URL은 http:// 이라 따옴표 안: 따옴표 개수로 판정)
        if i >= 0 and line[:i].count('"') % 2 == 0:
            line = line[:i] + ' ' * (len(line) - i)
        out.append(line)
    return '\n'.join(out)


def dump(path, prefix):
    s = mask_comments(open(path, encoding='utf-8').read())
    plain, interp = [], []
    for m in LIT.finditer(s):
        line = s[s.rfind('\n', 0, m.start()) + 1:s.find('\n', m.start())]
        if 'const ' in line or line.strip().startswith('['):
            continue
        if m.group(1) not in plain:
            plain.append(m.group(1))
    for m in INTERP.finditer(s):
        if m.group(1) not in interp:
            interp.append(m.group(1))
    for i, v in enumerate(plain):
        print(f'{prefix}.{i}\t{v}')
    for v in interp:
        print(f'$\t{v}')


def unescape(v):
    return v.encode('utf-8').decode('unicode_escape').encode('latin-1').decode('utf-8') if '\\' in v else v


def apply(path):
    sys.path.insert(0, HERE)
    from cs_strings import CS  # noqa
    by_ko = {}
    for k, ko, *_ in CS:
        by_ko.setdefault(ko, k)
    raw = open(path, encoding='utf-8').read()
    masked = mask_comments(raw)
    out, last, n, left = [], 0, 0, []
    for m in LIT.finditer(masked):
        line = masked[masked.rfind('\n', 0, m.start()) + 1:masked.find('\n', m.start())]
        if 'const ' in line or line.strip().startswith('['):
            continue
        v = unescape(raw[m.start(1):m.end(1)])
        if v in by_ko:
            out.append(raw[last:m.start()])
            out.append('Foldy.Core.Localization.Loc.T("' + by_ko[v] + '")')
            last = m.end()
            n += 1
        else:
            left.append(v)
    out.append(raw[last:])
    open(path, 'w', encoding='utf-8').write(''.join(out))
    print(os.path.basename(path), 'replaced', n, 'left', len(left))


if __name__ == '__main__':
    if sys.argv[1] == 'dump':
        dump(sys.argv[2], sys.argv[3])
    else:
        for p in sys.argv[2:]:
            apply(p)
