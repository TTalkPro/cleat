#!/usr/bin/env python3
# 汇总 results/cleat.jsonl 与 results/clojure.jsonl（cleat-107）：每项取各次的最小值，打印 cleat / JVM 的倍数与几何平均。
# 用法：compare.py [--md]   --md 输出 Markdown 表
import json, math, os, sys
B = os.path.dirname(os.path.abspath(__file__))

def load(impl):
    best, errs = {}, {}
    p = os.path.join(B, 'results', impl + '.jsonl')
    if not os.path.exists(p): return best, errs
    for line in open(p):
        r = json.loads(line)
        if r['secs'] is None: errs[r['name']] = (r['error'] or '')[:80].replace('\n', ' ')
        else: best[r['name']] = min(best.get(r['name'], 1e300), r['secs'])
    return best, errs

c, ce = load('cleat'); j, je = load('clojure')
names = sorted(set(c) | set(j) | set(ce) | set(je))
md = '--md' in sys.argv
rows, ratios = [], []
for n in names:
    cs, js = c.get(n), j.get(n)
    ratio = cs / js if cs and js else None
    if ratio: ratios.append(ratio)
    rows.append((n, cs, js, ratio, ce.get(n) if cs is None else '', je.get(n) if js is None else ''))
fmt = lambda x: '' if x is None else ('%.3f' % x)
if md:
    print('| 基准 | cleat (s) | JVM (s) | cleat / JVM | 备注 |'); print('|---|---:|---:|---:|---|')
    for n, cs, js, r, e1, e2 in rows:
        print('| %s | %s | %s | %s | %s |' % (n, fmt(cs), fmt(js), fmt(r), ('cleat: ' + e1 if e1 else '') + (' JVM: ' + e2 if e2 else '')))
else:
    for n, cs, js, r, e1, e2 in rows:
        print('%-12s %10s %10s %8s  %s' % (n, fmt(cs), fmt(js), fmt(r), (e1 or '') + (e2 or '')))
if ratios:
    g = math.exp(sum(map(math.log, ratios)) / len(ratios))
    print(('\n几何平均（%d 项）：cleat / JVM = **%.2f**' if md else '\ngeomean over %d: cleat/JVM = %.2f') % (len(ratios), g))
