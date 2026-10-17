#!/usr/bin/env python3
# cleat-130：汇总 run130 / run.py 交错写入的 JSONL。每个 variant / label / 基准取最小值，
# 打 Markdown 表；cleat / C2、cleat / GraalVM 与 GraalVM / C2 的几何平均只含有完整三项的基准。
# 用法：summarize130.py [JSONL]（缺省 results/m130.jsonl）
import json
import math
import os
import sys

B = os.path.dirname(os.path.abspath(__file__))
LABELS = ('cleat', 'c2', 'graal')

def load(path):
    best, errs = {}, {}
    for line in open(path):
        r = json.loads(line)
        key = (r['variant'], r['label'], r['name'])
        if r.get('secs') is None:
            errs[key] = (r.get('error') or '')[:100].replace('\n', ' ')
        else:
            old = best.get(key)
            if old is None or r['secs'] < old:
                best[key] = r['secs']
    return best, errs

if __name__ == '__main__':
    path = sys.argv[1] if len(sys.argv) > 1 else os.path.join(B, 'results', 'm130.jsonl')
    best, errs = load(path)
    variants = sorted({v for v, _, _ in best} | {v for v, _, _ in errs})
    for variant in variants:
        names = sorted({n for v, _, n in best if v == variant})
        if not names:
            continue
        print(f'\n## {variant}\n')
        print('| 基准 | cleat | C2 | GraalVM | cleat / C2 | cleat / GraalVM | GraalVM / C2 |')
        print('|---|---:|---:|---:|---:|---:|---:|')
        ratios = {'cc': [], 'cg': [], 'gc': []}
        for name in names:
            c, j, g = (best.get((variant, label, name)) for label in LABELS)
            cc = c / j if c is not None and j is not None else None
            cg = c / g if c is not None and g is not None else None
            gc = g / j if g is not None and j is not None else None
            if cc: ratios['cc'].append(cc)
            if cg: ratios['cg'].append(cg)
            if gc: ratios['gc'].append(gc)
            fmt = lambda x: '' if x is None else f'{x:.3f}'
            notes = []
            for label, value in zip(LABELS, (c, j, g)):
                if value is None:
                    e = errs.get((variant, label, name))
                    notes.append(label + ': ' + e if e else label + ': missing')
            print(f'| {name} | {fmt(c)} | {fmt(j)} | {fmt(g)} | {fmt(cc)} | {fmt(cg)} | {fmt(gc)} |'
                  + (f' {"；".join(notes)}' if notes else ''))
        geo = lambda xs: math.exp(sum(map(math.log, xs)) / len(xs)) if xs else None
        print(f'| **几何平均** | | | | **{geo(ratios["cc"]):.3f}** | **{geo(ratios["cg"]):.3f}** | **{geo(ratios["gc"]):.3f}** |')
