# -*- coding: utf-8 -*-
"""
C# 파일의 한국어 문자열을 언어 파일 키로 바꾸는 도구.
  python build/i18n/cs_tool.py dump <file.cs> <접두어>  → 한국어 문자열을 표에 넣을 모양으로 출력
      보간 없는 글("…")은  <접두어>.<n>\t<글>
      보간 글($"…")은      $\t<글, 자리는 {0} {1:N0} …>\t<값 식들>
  python build/i18n/cs_tool.py apply <file.cs> ...       → 표(cs_strings.py)에 있는 글을 바꾼다
      보간 글은 Loc.F("키", 값…)로, 보간 없는 글은 Loc.T("키")로.
표의 ko는 dump가 보여 준 글과 똑같아야 한다. 주석·const·attribute 줄, 로그(Log.*) 줄은 건드리지 않는다.
"""
import os, re, sys

HERE = os.path.dirname(os.path.abspath(__file__))
LIT = re.compile(r'(?<![@$\w"])"((?:[^"\\\n]|\\.)*[가-힣](?:[^"\\\n]|\\.)*)"')
HANGUL = re.compile(r'[가-힣]')
RAW = re.compile(r'"""[\s\S]*?"""')
ESC = {'n': '\n', '"': '"', '\\': '\\', 't': '\t', '0': '\0', "'": "'"}
T = 'Foldy.Core.Localization.Loc.T'
F = 'Foldy.Core.Localization.Loc.F'


def mask_comments(s):
    out = []
    for line in s.split('\n'):
        i = line.find('//')
        # 문자열 안의 // 는 거의 없다고 보고 단순 처리 (URL은 http:// 이라 따옴표 안: 따옴표 개수로 판정)
        if i >= 0 and line[:i].count('"') % 2 == 0:
            line = line[:i] + ' ' * (len(line) - i)
        out.append(line)
    s = '\n'.join(out)
    # 원시 문자열("""…""")은 통째로 건드리지 않는다 (예: 업데이트 창의 PowerShell 스크립트)
    return RAW.sub(lambda m: re.sub(r'[^\n]', ' ', m.group(0)), s)


def line_of(s, pos):
    return s[s.rfind('\n', 0, pos) + 1:s.find('\n', pos)]


def skipped_line(line):
    t = line.strip()
    return 'const ' in line or t.startswith('[') or re.search(r'\bLog\.(Information|Warning|Debug|Error|Verbose|Fatal)\(', line)


def skip_literal(s, j, q):
    k = j + 1
    while s[k] != q:
        k += 2 if s[k] == '\\' else 1
    return k + 1


def scan_interp(s, start):
    """s[start:start+2] == '$"'. (끝 위치, 한국어 틀, [식…]) 을 돌려준다."""
    i = start + 2
    ko, args = '', []
    while True:
        c = s[i]
        if c == '"':
            return i + 1, ko, args
        if c == '\\':
            ko += ESC[s[i + 1]]
            i += 2
            continue
        if c == '{' and s[i + 1] == '{':
            ko += '{'
            i += 2
            continue
        if c == '}' and s[i + 1] == '}':
            ko += '}'
            i += 2
            continue
        if c == '{':
            j, depth, expr, fmt = i + 1, 0, '', None
            while True:
                ch = s[j]
                if ch in '"\'':
                    k = skip_literal(s, j, ch)
                    expr += s[j:k]
                    j = k
                    continue
                if ch in '([':
                    depth += 1
                elif ch in ')]':
                    depth -= 1
                elif ch == '}' and depth == 0:
                    break
                elif ch == ':' and depth == 0 and '?' not in expr:
                    fmt = ''
                    j += 1
                    while s[j] != '}':
                        fmt += s[j]
                        j += 1
                    break
                expr += ch
                j += 1
            ko += '{' + str(len(args)) + (':' + fmt if fmt else '') + '}'
            args.append(expr.strip())
            i = j + 1
            continue
        ko += c
        i += 1


def interps(raw, masked):
    """한국어가 든 보간 문자열: (시작, 끝, 틀, 식들). 안쪽 문자열에만 한국어가 있으면 바깥 틀은 넘긴다."""
    out = []
    for m in re.finditer(r'(?<![@\w])\$"(?!")', masked):
        if skipped_line(line_of(masked, m.start())):
            continue
        end, ko, args = scan_interp(raw, m.start())
        if HANGUL.search(ko):
            out.append((m.start(), end, ko, args))
    return out


def plains(raw, masked):
    out = []
    for m in LIT.finditer(masked):
        if skipped_line(line_of(masked, m.start())):
            continue
        v = raw[m.start(1):m.end(1)]
        out.append((m.start(), m.end(), re.sub(r'\\(.)', lambda e: ESC.get(e.group(1), '\\' + e.group(1)), v)))
    return out


def blank(s, a, b):
    return s[:a] + ' ' * (b - a) + s[b:]


def show(v):
    return v.replace('\\', '\\\\').replace('\n', '\\n')


def dump(path, prefix):
    raw = open(path, encoding='utf-8').read()
    masked = mask_comments(raw)
    ips = interps(raw, masked)
    m2 = masked
    for a, b, _, _ in ips:  # 보간 글은 따로: 안쪽 식에 든 "…"만 보간 없는 글로 센다
        m2 = blank(m2, a, b)
    found = [v for _, _, v in plains(raw, m2)]
    for _, _, _, args in ips:
        for x in args:
            found += [v for _, _, v in plains(x, x)]
    seen, n = set(), 0
    for v in found:
        if v not in seen:
            seen.add(v)
            print(f'{prefix}.{n}	{show(v)}')
            n += 1
    seen = set()
    for _, _, ko, args in ips:
        if ko not in seen:
            seen.add(ko)
            print(f'$\t{show(ko)}\t' + ' | '.join(args))


def unwrap_string_create(raw):
    """string.Create(CultureInfo.X, Loc.F(...)) → Loc.F(...) (보간 처리기 자리에는 Loc.F를 넘길 수 없다. Loc.F는 지금 문화권으로 쓴다)."""
    pat = re.compile(r'string\.Create\(\s*(?:System\.Globalization\.)?CultureInfo\.\w+,\s*(?=' + re.escape(F) + r'\()')
    while True:
        m = pat.search(raw)
        if not m:
            return raw
        i = m.end()
        depth = 0
        j = i
        while True:
            c = raw[j]
            if c in '"\'':
                j = skip_literal(raw, j, c)
                continue
            if c == '(':
                depth += 1
            elif c == ')':
                depth -= 1
                if depth == 0:
                    break
            j += 1
        # raw[i:j+1] 은 Loc.F(...), 그 뒤의 ')' 는 string.Create 닫는 괄호
        k = j + 1
        while raw[k] in ' \r\n\t':
            k += 1
        assert raw[k] == ')', raw[m.start():k + 1]
        raw = raw[:m.start()] + raw[i:j + 1] + raw[k + 1:]


def table():
    sys.path.insert(0, HERE)
    from cs_strings import CS  # noqa
    by_ko = {}
    for k, ko, *_ in CS:
        by_ko.setdefault(ko, k)
    return by_ko


def apply(path):
    by_ko = table()
    raw = open(path, encoding='utf-8').read()
    masked = mask_comments(raw)
    left = []
    ni = 0
    # 1) 보간 글: 하나 바꿀 때마다 다시 찾는다 (보간 글 안의 보간 글도 차례로)
    while True:
        todo = [x for x in interps(raw, mask_comments(raw)) if x[2] in by_ko]
        if not todo:
            break
        a, b, ko, args = todo[0]
        raw = raw[:a] + F + '("' + by_ko[ko] + '"' + ''.join(', ' + x for x in args) + ')' + raw[b:]
        ni += 1
    left += ['$' + x[2] for x in interps(raw, mask_comments(raw))]
    # 2) 보간 없는 글 (바뀐 보간 글 안쪽의 "…"도 여기서)
    masked = mask_comments(raw)
    np_ = 0
    for a, b, v in reversed(plains(raw, masked)):
        if v in by_ko:
            raw = raw[:a] + T + '("' + by_ko[v] + '")' + raw[b:]
            np_ += 1
        else:
            left.append(v)
    raw = unwrap_string_create(raw)
    open(path, 'w', encoding='utf-8').write(raw)
    print(os.path.basename(path), 'interp', ni, 'plain', np_, 'left', len(left))
    for v in reversed(left):
        print('  left:', show(v)[:100])


if __name__ == '__main__':
    if sys.argv[1] == 'dump':
        dump(sys.argv[2], sys.argv[3])
    else:
        for p in sys.argv[2:]:
            apply(p)
