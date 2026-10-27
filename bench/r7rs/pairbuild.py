#!/usr/bin/env python3
# cleat-158 的跨构建同轮配对 runner：比较两个 cleat 构建（如改动前基线 vs 当前 HEAD），
# 替代 m16full.py 的 cleat-on/cleat-off（那个用 CLEAT_NO_OPT 在同一构建上切换，这里没开关可比）。
# 判定口径与 m16full.py / m16sum.py 完全一致（同轮配对比值、quiet 窗口、load 记录、断点续跑）；
# 产物的 label 沿用 cleat-on（新构建）/ cleat-off（基线构建），m16sum.py 直接可用。
#
# 用法：
#   R7RS_BENCH=~/workspace/r7rs-benchmarks CLEAT=~/workspace/cleat \
#     R7_BASE=~/workspace/cleat-158-base python3 bench/r7rs/pairbuild.py -r 5 [基准名 …]
#   R7_BASE = 基线构建的仓库路径（其 src/Cleat.Host/bin/Release/net10.0/cleat.dll 必须已构建）。
#   结果追加到 results/pairbuild.jsonl（R7_OUT 改名）。
import json
import os
import sys

B = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, B)
import run
from quiet import LOAD_CAP, MAXLOAD, wait_quiet

BASE = os.environ['R7_BASE']
OUT = os.environ.get('R7_OUT', 'pairbuild')


def completed(out_path):
    done = set()
    if os.environ.get('R7_RESUME', '1') == '0' or not os.path.exists(out_path):
        return done
    with open(out_path, encoding='utf-8') as f:
        for line in f:
            line = line.strip()
            if not line or line.startswith('#'):
                continue
            try:
                r = json.loads(line)
            except json.JSONDecodeError:
                continue
            done.add((r.get('label'), r.get('rep'), r.get('name')))
    return done


def one(arm, rep, name):
    _, loaded = wait_quiet(MAXLOAD, LOAD_CAP)
    saved = run.CLEAT
    try:
        if arm == 'cleat-off':
            run.CLEAT = BASE
        r = run.run('cleat', name)
    finally:
        run.CLEAT = saved
    r.update(label=arm, rep=rep, nopt=None)
    r['load'] = round(os.getloadavg()[0], 2)
    r['loaded'] = loaded
    print(json.dumps(r, ensure_ascii=False), flush=True)
    with open(os.path.join(B, 'results', OUT + '.jsonl'), 'a') as f:
        f.write(json.dumps(r, ensure_ascii=False) + '\n')


if __name__ == '__main__':
    reps, args = 1, sys.argv[1:]
    if args[:1] == ['-r']:
        reps, args = int(args[1]), args[2:]
    elif args and args[0].isdigit():
        reps, args = int(args[0]), args[1:]
    names = args or run.names()
    print('# arms=cleat-on,cleat-off reps=%d items=%d new=%s base=%s quiet<=%.1f cap=%ds'
          % (reps, len(names), run.CLEAT, BASE, MAXLOAD, LOAD_CAP), flush=True)
    out_path = os.path.join(B, 'results', OUT + '.jsonl')
    done = completed(out_path)
    if done:
        print('# resume: skipping %d completed (label, rep, name) cells in %s' % (len(done), out_path), flush=True)
    for rep in range(1, reps + 1):
        for name in names:
            for arm in ('cleat-on', 'cleat-off'):
                if (arm, rep, name) in done:
                    continue
                one(arm, rep, name)
