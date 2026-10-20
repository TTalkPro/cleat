#!/usr/bin/env python3
# -*- coding: utf-8 -*-
# 读取 triage149.py 的 jsonl（随时可读，不必等跑完），输出 min/median/max + 同轮配对比值 + rtspec 次数。
# 用法：summarize149.py results/triage149-matrixA.jsonl [--ref base] [--items mbrot,pi] [--reps 3] [--setting pat]
#   --reps N 只用前 N 个干净轮次（并发机器上先拿已有轮次出结论，别等满 5 轮）
#   --pair    只打印配对比值（主口径：同一轮里 设置/参照 的比值，机器漂移两边同等作用）
import json, os, statistics, sys

B = os.path.dirname(os.path.abspath(__file__))
a = sys.argv[1:]
path = a[0] if a and not a[0].startswith('--') else os.path.join(B, 'results', 'triage149-%s.jsonl' % os.environ.get('R7_OUT', ''))
opt = dict()
for x in a:
    if x.startswith('--'):
        k, _, v = x[2:].partition('=')
        opt[k] = v if v else True
recs = [json.loads(l) for l in open(path) if l.strip()]
recs = [r for r in recs if r['secs'] is not None]
items = [i for i in (opt.get('items', '').split(',') if not isinstance(opt.get('items', ''), bool) else []) if i]
if not items:
    items = sorted({r['name'] for r in recs})
settings = []
for r in recs:
    if r['setting'] not in settings:
        settings.append(r['setting'])
pat = opt.get('setting')
if pat:
    settings = [s for s in settings if pat in s]
ref = opt.get('ref', settings[0])
reps = int(opt['reps']) if 'reps' in opt else None
# 每个 (setting,item) 取前 N 个**非污染**轮次（按 rep 升序），并保留 rep 号用于配对
by = {}
for r in sorted(recs, key=lambda x: x['rep']):
    if r.get('loaded') and not opt.get('include_loaded'):
        continue
    if pat and r['setting'] not in settings:
        continue
    if r['name'] not in items:
        continue
    v = by.setdefault((r['setting'], r['name']), [])
    if reps is None or len(v) < reps:
        v.append((r['rep'], r['secs'], r.get('rtspec')))
print('# %s  参照=%s  项=%s  每格≤%s 轮（污染轮默认剔除）' % (
    os.path.basename(path), ref, ','.join(items), reps or '∞'))
print('# 设置共 %d 个；配对比值 = 同一 rep 内 设置/参照 的秒数比（比值中位 >1 = 更慢）' % len(settings))
for n in items:
    rb = dict((s, by.get((s, n))) for s in settings)
    base = rb.get(ref) or []
    print('\n== %s ==' % n)
    print('%-20s %8s %8s %8s %6s  %-22s %s' % ('设置', 'min', 'median', 'max', '轮', 'vs ' + ref, 'rtspec compiled/failed/ms'))
    for s in settings:
        v = rb.get(s) or []
        if not v:
            print('%-20s (无数据)' % s); continue
        secs = [x[1] for x in v]
        rep_map = dict((x[0], x[1]) for x in v)
        paired = [rep_map[b[0]] / b[1] for b in base if b[0] in rep_map] if s != ref else []
        pr = ('配对中位 x%.3f(%d 对，逐轮 %s)' % (statistics.median(paired), len(paired),
              ' '.join('%.3f' % x for x in paired))) if paired else ('—' if s == ref else '无同轮')
        st = [x[2] for x in v if x[2]]
        sts = ('%d/%d/%d' % (statistics.median(y['compiled'] for y in st),
                             statistics.median(y['failed'] for y in st),
                             statistics.median(y['compileMs'] for y in st))) if st else ''
        flag = ' 双态?' if max(secs) / min(secs) > 1.5 else ''
        print('%-20s %8.3f %8.3f %8.3f %6d  %-22s %s%s' % (
            s, min(secs), statistics.median(secs), max(secs), len(secs), pr, sts, flag))
