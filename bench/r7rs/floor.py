#!/usr/bin/env python3
# -*- coding: utf-8 -*-
# cleat-151 的本机噪声地板标定 runner：单臂（cleat-on，即不设 CLEAT_NO_OPT）连续跑 N 轮，
# 每轮前共用 quiet.py 的安静窗口；原始记录追加到 results/floor.jsonl，汇总每次从 jsonl 重算。
#
# 用法：
#   R7RS_BENCH=~/workspace/r7rs-benchmarks CLEAT=~/workspace/cleat \
#     python3 bench/r7rs/floor.py 9 wc browse lattice slatex peval mbrot pi parsing array1 takl
#   轮数也可用 --reps N 指定；不给基准名则跑 run.py 看到的全部 56 项。
#
# 输出：
#   - results/floor.jsonl：原始数据（追加式，不删被 load 筛掉的轮，事后可追溯）。
#   - results/floor-<host>.json：本 host 的门槛与符号检验近似建议（m16sum.py 可直接用 R7_FLOOR 指它）。
#   - stdout：max/min、p50、p90 与“检出 5% / 10% 需要多少同轮配对”的经验表。
#
# 建议口径（是近似，不是精确功效分析）：把单臂秒数取 log，用 log(p90)/log(p50) 之间
# 的标准正态 90% 分位距离反推一项的相对噪声 sigma；两种臂独立时配对差近似 sigma_pair=sqrt(2)*sigma。
# 符号检验里一对能落在正确方向的概率 p=NormalCDF(delta/sigma_pair)。按 null p=0.5 选显著性 <=5%
# 的最小同向数 k，再按 alternative p 找 power>=80% 的最小轮数 R。双态项（如 takl）会自然要求
# 很大 R；这类项仍应报告区间，不要把表里的 R 当成“再跑这么多轮就一定可判定”的保证。
import argparse
import json
import math
import os
import socket
import statistics
import sys
import time

B = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, B)
from quiet import LOAD_CAP, MAXLOAD, wait_quiet

HOST = socket.gethostname()
Z90 = statistics.NormalDist().inv_cdf(0.90)


def binom_ge(rs, n, p):
    """P(Binomial(n,p) >= rs)，不用 scipy。"""
    if rs <= 0:
        return 1.0
    if rs > n:
        return 0.0
    return sum(math.comb(n, k) * p ** k * (1.0 - p) ** (n - k)
               for k in range(rs, n + 1))


def sign_test_rounds(delta, sigma, power=0.80, alpha=0.05, limit=5000):
    """符号检验近似：返回 (最小同轮配对数 R, 该 R 下需要的最小同向数 k)。
    sigma=0 时无法标定；保护上限避免高噪声项把工具挂死。"""
    if sigma <= 0 or delta <= 0:
        return None, None
    p = statistics.NormalDist().cdf(delta / (math.sqrt(2.0) * sigma))
    if p <= 0.5:
        return None, None
    for n in range(1, limit + 1):
        # null 下最小 k 使“至少 k 同向”的显著性 <= alpha；两向都有可能，所以近似用两侧 5%。
        k = next((x for x in range(1, n + 1) if binom_ge(x, n, 0.5) <= alpha), None)
        if k is None:
            continue
        if binom_ge(k, n, p) >= power:
            return n, k
    return limit, None


def extract_reps(argv):
    """让 --reps 可放在基准名前或后；argparse 的位置解析做不到这一点。"""
    out = []
    reps = None
    i = 0
    while i < len(argv):
        arg = argv[i]
        if arg == '--reps':
            if i + 1 >= len(argv):
                raise SystemExit('argument --reps: expected one argument')
            reps = argv[i + 1]
            i += 2
        elif arg.startswith('--reps='):
            reps = arg.partition('=')[2]
            i += 1
        else:
            out.append(arg)
            i += 1
    return reps, out


def load_records(path, names, host, include_loaded):
    by = {n: [] for n in names}
    loaded = {n: 0 for n in names}
    if not os.path.exists(path):
        return by, loaded
    with open(path, encoding='utf-8') as f:
        for line in f:
            line = line.strip()
            if not line or line.startswith('#'):
                continue
            try:
                r = json.loads(line)
            except json.JSONDecodeError:
                continue
            if r.get('name') not in by or r.get('host') != host:
                continue
            if r.get('loaded'):
                loaded[r['name']] += 1
                if not include_loaded:
                    continue
            if r.get('secs') is not None and r.get('secs') > 0:
                by[r['name']].append(r)
    return by, loaded


def percentile(xs, q):
    """小样本下的线性分位（N=2 时 p90 会偏向较大观测，这正是标定想要的保守方向）。"""
    if not xs:
        return None
    ys = sorted(xs)
    pos = (len(ys) - 1) * q
    lo = math.floor(pos)
    hi = math.ceil(pos)
    if lo == hi:
        return ys[lo]
    return ys[lo] + (ys[hi] - ys[lo]) * (pos - lo)


def summarize(name, rs, requested, loaded_excluded=0):
    secs = [r['secs'] for r in rs]
    p50 = percentile(secs, 0.50)
    p90 = percentile(secs, 0.90)
    ratio = max(secs) / min(secs)
    # 用 p90 相对 p50 的距离估 log 空间 sigma；比 max/min 稳，也比单条 stdev 少受一个离群轮支配。
    sigma = max((math.log(p90) - math.log(p50)) / Z90, sys.float_info.epsilon) if p50 > 0 and p90 > p50 else 0.0
    r5, k5 = sign_test_rounds(0.05, sigma)
    r10, k10 = sign_test_rounds(0.10, sigma)
    # 交给 m16sum 的量级：5% 达不到就放宽到 10%；仍达不到时用 N 对中位数的 95% 近似半宽。
    if r5 is not None and r5 <= len(rs):
        declared_delta, declared_k = 0.05, k5
    elif r10 is not None and r10 <= len(rs):
        declared_delta, declared_k = 0.10, k10
    elif len(rs) >= 2:
        declared_delta = min(0.50, max(0.05, statistics.NormalDist().inv_cdf(0.975) * math.sqrt(2.0 / len(rs)) * sigma))
        _, declared_k = sign_test_rounds(declared_delta, sigma)
        declared_delta = round(declared_delta, 4)
    else:
        declared_delta, declared_k = None, None
    return {
        'rounds_used': len(rs),
        'rounds_requested': requested,
        'min': min(secs),
        'max': max(secs),
        'max_over_min': ratio,
        'p50': p50,
        'p90': p90,
        'sigma_log_relative': sigma,
        'loaded_excluded': loaded_excluded,
        'recommend_5pct_rounds': r5,
        'recommend_5pct_same_direction': k5,
        'recommend_10pct_rounds': r10,
        'recommend_10pct_same_direction': k10,
        'min_declared_ratio_delta': declared_delta,
        'min_same_direction': declared_k,
        'bimodal': ratio > 1.5,
    }


def main():
    ap = argparse.ArgumentParser(
        description='本机 cleat 单臂噪声地板标定（原始数据追加 results/floor.jsonl）')
    ap.add_argument('reps_pos', nargs='?', default=None,
                    help='每项轮数（缺省 9；也可用 --reps）')
    ap.add_argument('--reps', dest='reps_opt', type=int,
                    help='每项轮数（优先于位置参数）')
    ap.add_argument('names', nargs='*', help='r7rs 基准名；缺省全部')
    ap.add_argument('--jsonl', default=os.path.join(B, 'results', 'floor.jsonl'))
    ap.add_argument('--summary', default=None,
                    help='门槛 json 路径（缺省 results/floor-<host>.json）')
    ap.add_argument('--include-loaded', action='store_true',
                    help='汇总时保留跑前未等到安静窗口的轮（原始 jsonl 始终保留）')
    flag_reps, argv = extract_reps(sys.argv[1:])
    a = ap.parse_args(argv)
    a.reps_opt = int(flag_reps) if flag_reps is not None else None
    if a.reps_opt is not None and a.reps_pos is not None:
        # “pi --reps 2”里 argparse 只能先看到 pi 这个可选位置槽；放回 names。
        a.names.insert(0, str(a.reps_pos))
        a.reps_pos = None
    reps = a.reps_opt if a.reps_opt is not None else (
        int(a.reps_pos) if a.reps_pos is not None else 9)
    if reps < 2:
        ap.error('轮数至少为 2（地板/功效近似需要分布）')
    import run
    names = a.names or run.names()
    unknown = sorted(set(names) - set(run.names()))
    if unknown:
        ap.error('未知基准: ' + ', '.join(unknown))

    os.makedirs(os.path.dirname(a.jsonl) or '.', exist_ok=True)
    print('# host=%s arms=cleat-on reps=%d items=%d quiet<=%.1f cap=%ds CLEAT=%s' % (
        HOST, reps, len(names), MAXLOAD, LOAD_CAP,
        os.path.abspath(os.environ.get('CLEAT', os.path.dirname(os.path.dirname(B))))))
    saved_nopt = os.environ.get('CLEAT_NO_OPT')
    try:
        os.environ.pop('CLEAT_NO_OPT', None)
        for rep in range(1, reps + 1):
            for name in names:
                # 与 m16full.one 相同：loaded 记“跑前没等到窗口”，load 记该轮结束后的采样值。
                _, loaded = wait_quiet(MAXLOAD, LOAD_CAP)
                r = run.run('cleat', name)
                rec = {
                    'name': name,
                    'rep': rep,
                    'secs': r.get('secs'),
                    'load': round(os.getloadavg()[0], 2),
                    'loaded': loaded,
                    'host': HOST,
                }
                if r.get('error'):
                    rec['error'] = r['error']
                print(json.dumps(rec, ensure_ascii=False), flush=True)
                with open(a.jsonl, 'a', encoding='utf-8') as f:
                    f.write(json.dumps(rec, ensure_ascii=False) + '\n')
    finally:
        if saved_nopt is None:
            os.environ.pop('CLEAT_NO_OPT', None)
        else:
            os.environ['CLEAT_NO_OPT'] = saved_nopt

    by, loaded_counts = load_records(a.jsonl, names, HOST, a.include_loaded)
    summaries = {n: summarize(n, by[n], reps, loaded_counts[n]) for n in names}
    print('\n===== 轮内极差（secs；load 污染轮默认剔除，原始 jsonl 保留） =====', flush=True)
    print('%-12s %5s %5s %10s %10s %10s %10s  %s' % (
        '基准', '轮', 'max/min', 'p50', 'p90', '5%R/k', '10%R/k', '备注'), flush=True)
    for n in names:
        s = summaries[n]
        if not s['rounds_used']:
            print('%-12s %5d %5s %10s %10s %10s %10s  %s' % (
                n, 0, '—', '—', '—', '—', '—', '无干净轮'), flush=True)
            continue
        def cell(rk):
            return '%d/%s' % (rk[0], rk[1] if rk[1] is not None else '?') if rk[0] is not None else '>'
        print('%-12s %5d %5.3f %10.4f %10.4f %10s %10s  %s' % (
            n, s['rounds_used'], s['max_over_min'], s['p50'], s['p90'],
            cell((s['recommend_5pct_rounds'], s['recommend_5pct_same_direction'])),
            cell((s['recommend_10pct_rounds'], s['recommend_10pct_same_direction'])),
            ('双态? ' if s['bimodal'] else '') + ('loaded剔除 %d' % s['loaded_excluded'] if s['loaded_excluded'] else '')), flush=True)
    print('# R/k 为符号检验近似（本工具注释口径）：R=检出该量级需要的同轮配对数，k=该 R 下 5% 显著性所需最小同向数；上限 5000。', flush=True)
    print('# 建议/门槛从 %s 的当前记录重算；双态项按区间报告，不要用 min-of-N。' % os.path.abspath(a.jsonl), flush=True)

    out = a.summary or os.path.join(B, 'results', 'floor-%s.json' % HOST)
    doc = {
        'schema': 'floor-v1',
        'host': HOST,
        'generated': time.strftime('%FT%T%z'),
        'source': os.path.abspath(a.jsonl),
        'default': {'min_declared_ratio_delta': 0.05, 'min_same_direction': 5},
        'items': {
            n: {
                'min_declared_ratio_delta': summaries[n]['min_declared_ratio_delta'],
                'min_same_direction': summaries[n]['min_same_direction'],
                'rounds_used': summaries[n]['rounds_used'],
                'recommendations': {
                    '0.05': {
                        'rounds': summaries[n]['recommend_5pct_rounds'],
                        'same_direction': summaries[n]['recommend_5pct_same_direction'],
                    },
                    '0.10': {
                        'rounds': summaries[n]['recommend_10pct_rounds'],
                        'same_direction': summaries[n]['recommend_10pct_same_direction'],
                    },
                },
            } for n in names if summaries[n]['min_declared_ratio_delta'] is not None
        },
    }
    with open(out, 'w', encoding='utf-8') as f:
        json.dump(doc, f, ensure_ascii=False, indent=2)
        f.write('\n')
    print('# 门槛建议：%s' % os.path.abspath(out), flush=True)


if __name__ == '__main__':
    main()
