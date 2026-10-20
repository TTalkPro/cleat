#!/usr/bin/env python3
# m16full.py 三臂产物的汇总（cleat-150）：把 results/<R7_OUT>.jsonl 按 label 拆成三份，
# 再用 compare.py 的口径（每项 min / median / max、全部轮次、双态区间、三档几何平均）出两张表：
#   1) cleat-on vs cleat-off  → M16 开关的贡献分解（比值 <1 才是优化在起作用）
#   2) cleat-on vs jvm        → 对 JVM Clojure 的倍数（与 M15 的 1.61x 同口径）
# 用法：python3 bench/r7rs/m16sum.py [R7_OUT]     （缺省 m16full）
# 另外打印每项各臂的采样负载 load：load 明显高的轮次要人工判污染（notes 的 M16 §4）。
import json
import os
import subprocess
import sys

B = os.path.dirname(os.path.abspath(__file__))
OUT = sys.argv[1] if len(sys.argv) > 1 else os.environ.get('R7_OUT', 'm16full')
SRC = os.path.join(B, 'results', OUT + '.jsonl')
if not os.path.exists(SRC):
    raise SystemExit('没有数据：' + SRC)

arms = ('cleat-on', 'cleat-off', 'jvm')
recs = {a: [] for a in arms}
loads = {}
for line in open(SRC):
    line = line.strip()
    if not line:
        continue
    r = json.loads(line)
    lab = r.get('label')
    if lab in recs:
        recs[lab].append(r)
    loads.setdefault(r.get('name'), {})[lab] = r.get('load')

paths = {}
for a in arms:
    p = os.path.join(B, 'results', '%s-%s.jsonl' % (OUT, a))
    with open(p, 'w') as f:
        for r in recs[a]:
            f.write(json.dumps(r, ensure_ascii=False) + '\n')
    paths[a] = p
    print('%s: %d 条记录 -> %s' % (a, len(recs[a]), os.path.basename(p)), flush=True)

for pair, title in ((('cleat-on', 'cleat-off'), '开关贡献（cleat-on / cleat-off，<1 才是收益）'),
                    (('cleat-on', 'jvm'), '对 JVM（cleat-on / jvm，M15 口径的 1.61x 在这里比）')):
    print('\n===== %s =====' % title, flush=True)
    subprocess.run([sys.executable, os.path.join(B, 'compare.py'), '--md',
                    '--cleat', paths[pair[0]], '--jvm', paths[pair[1]]])

print('\n===== 采样负载 load（每项各臂最大值） =====', flush=True)
print('| 基准 | on | off | jvm |')
print('|---|---:|---:|---:|')
for n in sorted(loads):
    v = loads[n]
    print('| %s | %s | %s | %s |' % (n, v.get('cleat-on'), v.get('cleat-off'), v.get('jvm')))
