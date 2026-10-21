#!/usr/bin/env python3
# m16full.py 三臂产物的汇总（cleat-150）：把 results/<R7_OUT>.jsonl 按 label 拆成三份，
# 再用 compare.py 的口径（每项 min / median / max、全部轮次、双态区间、三档几何平均）出两张表：
#   1) cleat-on vs cleat-off  → M16 开关的贡献分解（比值 <1 才是优化在起作用）
#   2) cleat-on vs jvm        → 对 JVM Clojure 的倍数（与 M15 的 1.61x 同口径）
#   3) 同轮配对比值 → 192.168.3.111 离线后（2026-10-20 起）本机唯一可声明的口径：
#      同一基准 × 同一轮的 秒数比取中位，并报方向一致性（R 轮里同向的对数）；
#      本机噪声地板 17 ~ 43%（cleat-149 §2），所以 <10% 的单臂最小值差不算结论。
# 用法：python3 bench/r7rs/m16sum.py [R7_OUT] [--include-loaded]     （缺省 m16full）
# --include-loaded 保留跑前没等到安静窗口的轮（缺省剔除）；load 列仍全部打印，人工复核用。
# 另外打印每项各臂的采样负载 load：load 明显高的轮次要人工判污染（notes 的 M16 §4）。
import json
import os
import math
import statistics
import subprocess
import sys

B = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, B)
from quiet import MAXLOAD      # 安静窗口门槛（与 m16full.py / triage149.py 同一出处）

# 配对比值的负载筛门槛单独可指（R7_PAIR_MAXLOAD，缺省跟着 R7_MAXLOAD）：
# 汇总时忘了带跑分用的 R7_MAXLOAD 会让整项配对被静默丢光，只剩一两个格子还能出数
# （111 的 4 vCPU 上缺省门槛是 1.0，实际跑分的采样 load 常在 1.0 ~ 1.4 之间，踩过一次）
PAIR_MAXLOAD = float(os.environ.get('R7_PAIR_MAXLOAD', str(MAXLOAD)))
INCLUDE_LOADED = '--include-loaded' in sys.argv
OUT = next((a for a in sys.argv[1:] if not a.startswith('-')), None) or os.environ.get('R7_OUT', 'm16full')
SRC = os.path.join(B, 'results', OUT + '.jsonl')
if not os.path.exists(SRC):
    raise SystemExit('没有数据：' + SRC)

arms = ('cleat-on', 'cleat-off', 'jvm')
recs = {a: [] for a in arms}
loads = {}
drops = 0
for line in open(SRC):
    line = line.strip()
    if not line:
        continue
    r = json.loads(line)
    lab = r.get('label')
    loads.setdefault(r.get('name'), {})[lab] = max(
        loads.get(r.get('name'), {}).get(lab) or 0.0, r.get('load') or 0.0)
    if r.get('loaded') and not INCLUDE_LOADED:
        drops += 1
        continue
    if lab in recs:
        recs[lab].append(r)

paths = {}
for a in arms:
    p = os.path.join(B, 'results', '%s-%s.jsonl' % (OUT, a))
    with open(p, 'w') as f:
        for r in recs[a]:
            f.write(json.dumps(r, ensure_ascii=False) + '\n')
    paths[a] = p
    print('%s: %d 条记录 -> %s' % (a, len(recs[a]), os.path.basename(p)), flush=True)

# 只比"这一份数据里真的有"的臂：单项归因常是两臂跑（ARMS=cleat-on,cleat-off），
# 这时不要拿空的 jvm 表冒充读数。按臂最小值的 compare.py 表放在配对比值之后（本机先看配对）。
COMPARISONS = [(peer, key, title) for peer, key, title in (
    ('cleat-off', 'off', '开关贡献（cleat-on / cleat-off，<1 才是收益）'),
    ('jvm', 'jvm', '对 JVM（cleat-on / jvm，M15 口径的 1.61x 在这里比）')) if recs[peer]]

# 同轮配对比值：按 (基准, rep) 配对，两侧都要有这一轮才算一对（rep 号是 m16full 每轮统一打的）。
by_rep = {a: {} for a in arms}
ld_rep = {a: {} for a in arms}
for a in arms:
    for r in recs[a]:
        if r.get('secs') is not None:
            by_rep[a][(r['name'], r.get('rep'))] = r['secs']
            ld_rep[a][(r['name'], r.get('rep'))] = r.get('load') or 0.0

PEERS = tuple((key, peer) for peer, key, _ in COMPARISONS)
dropped_by_load = 0


def paired_ratios(name, peer_arm):
    """cleat-on 与该臂在同一 (基准, 轮) 里的秒数比；>1 = cleat-on 更慢。
    两侧任一边的采样 load 超过配对门槛（R7_PAIR_MAXLOAD，缺省 = R7_MAXLOAD）就不算一对：
    loaded 只记"跑前没等到窗口"，跑分中途被别人的构建挤掉要靠这列事后筛（111 离线后本机常见）。"""
    peer = by_rep[peer_arm]
    lds = ld_rep[peer_arm]
    all_pairs = []
    out = []
    for (nm, rp), v in sorted(by_rep['cleat-on'].items()):
        if nm != name or (nm, rp) not in peer:
            continue
        all_pairs.append(v / peer[(nm, rp)])
        if not INCLUDE_LOADED and max(ld_rep['cleat-on'][(nm, rp)], lds[(nm, rp)]) > PAIR_MAXLOAD:
            global dropped_by_load
            dropped_by_load += 1
            continue
        out.append(v / peer[(nm, rp)])
    # 筛完一对不剩时退回"全部配对"的中位，但打上星号：静默丢光整项会让表里只剩个别格子，
    # 读者以为那是全部轮次的结论（本轮踩过：browse 只报了 1 对，手工按 (name, rep) 配是 5 对）
    return out or all_pairs, len(all_pairs)


print('\n===== 同轮配对比值（本机口径：判定只认这张表） =====', flush=True)
print('剔除 loaded 记录 %d 条；load > %.1f 的配对剔除 %d 对%s（R7_PAIR_MAXLOAD 可另指门槛；带 * 的格子 = 筛后无配对、退回全部配对）' % (
    drops, PAIR_MAXLOAD, dropped_by_load,
    '（--include-loaded 已保留全部轮）' if INCLUDE_LOADED else ''), flush=True)
print('%-12s %s' % ('基准', ' '.join('%-30s %-6s' % ('on/' + key + ' 配对比值', '同向<1')
                                    for key, _ in PEERS)), flush=True)
meds = {k: [] for k, _ in PEERS}
for n in sorted({k[0] for k in by_rep['cleat-on']}):
    cells = []
    for key, peer_arm in PEERS:
        rs, total = paired_ratios(n, peer_arm)
        if not rs:
            cells.append(('—', '—'))
            continue
        md = statistics.median(rs)
        meds[key].append(md)
        star = '*' if len(rs) != total else ''
        cells.append(('x%.3f%s (%d/%d 对: %s)' % (md, star, len(rs), total, ' '.join('%.3f' % x for x in rs)),
                      '%d/%d' % (sum(1 for x in rs if x < 1.0), len(rs))))
    print('%-12s %s' % (n, ' '.join('%-30s %-6s' % c for c in cells)), flush=True)

# 几何平均按“各项配对比值中位”算（compare.py 的几何平均按各臂最小值，本机噪声下会漂）
for key, label in ((k, lbl) for k, lbl in (('off', 'M16 开关贡献 on/off'), ('jvm', '对 JVM 的倍数 on/jvm'))
                   if k in meds):
    if meds[key]:
        g = math.exp(sum(math.log(m) for m in meds[key]) / len(meds[key]))
        print('%s：配对比值中位的几何平均 x%.3f（%d 项；<1 = 开优化更快 / cleat 更快）' % (
            label, g, len(meds[key])), flush=True)

for peer, key, title in COMPARISONS:
    print('\n===== %s（compare.py 按臂最小值口径，与历史表并列用） =====' % title, flush=True)
    subprocess.run([sys.executable, os.path.join(B, 'compare.py'), '--md',
                    '--cleat', paths['cleat-on'], '--jvm', paths[peer]])

print('\n===== 采样负载 load（每项各臂最大值） =====', flush=True)
print('| 基准 | on | off | jvm |')
print('|---|---:|---:|---:|')
for n in sorted(loads):
    v = loads[n]
    print('| %s | %s | %s | %s |' % (n, v.get('cleat-on'), v.get('cleat-off'), v.get('jvm')))
