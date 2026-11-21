# -*- coding: utf-8 -*-
"""코드·XAML에 남은 한국어 글(사용자에게 보일 수 있는 문자열) 세기. 주석은 뺀다. 다국어 옮기기 진행 확인용."""
import glob, os, re, sys

root = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
rows = []
for f in glob.glob(os.path.join(root, 'src', '**', '*.cs'), recursive=True) + glob.glob(os.path.join(root, 'src', '**', '*.xaml'), recursive=True):
    parts = f.replace('/', os.sep).split(os.sep)
    if 'obj' in parts or 'bin' in parts:
        continue
    s = open(f, encoding='utf-8').read()
    s = re.sub(r'//.*', '', s)
    s = re.sub(r'<!--.*?-->', '', s, flags=re.S)
    s = re.sub(r'/// .*', '', s)
    n = len(re.findall(r'"[^"\n]*[가-힣][^"\n]*"', s))
    if n:
        rows.append((n, os.path.relpath(f, root)))
rows.sort(reverse=True)
print('total', sum(n for n, _ in rows), 'files', len(rows))
for n, f in rows[: int(sys.argv[1]) if len(sys.argv) > 1 else 40]:
    print(n, f)
