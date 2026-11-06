#!/usr/bin/env python3
# cleat-158 的跨构建同轮配对 runner：比较两个 cleat 构建（如改动前基线 vs 当前 HEAD），
# 替代 m16full.py 的 cleat-on/cleat-off（那个用 CLEAT_NO_OPT 在同一构建上切换，这里没开关可比）。
# 判定口径与 m16full.py / m16sum.py 完全一致（同轮配对比值、quiet 窗口、load 记录、断点续跑）；
# 产物的 label 沿用 cleat-on（新构建）/ cleat-off（基线构建），m16sum.py 直接可用。
#
# cleat-159 的复核加了**臂序轮换**：每轮的两臂先后顺序交替（奇数轮新构建先跑，偶数轮基线先跑），
# 记录的 order 字段是 first=new / first=base。111（4 vCPU）上短基准（sum / ray）在分层 JIT 下落在
# 两个量化档上（慢档 ≈ 快档 × 1.32），固定"新构建先跑"会把这种抽样偏置放大成看起来的单边回退。
# 汇总时可以用 R7_PAIR_SAMEORDER=1 回到旧行为（固定次序）。
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
SAMEORDER = os.environ.get('R7_PAIR_SAMEORDER', '0') == '1'
# 同快照 A/B（cleat-160）：R7_OFF_ENV="DOTNET_ReadyToRun=0,..." 只作用于 cleat-off 臂。
OFF_ENV = dict(item.split('=', 1) for item in os.environ.get('R7_OFF_ENV', '').split(',') if '=' in item)
# 同构建换镜像 / 环境（cleat-181）：R7_ON_ENV="CLEAT_BOOT=…,CLEAT_NO_OPT=…" 只作用于 cleat-on 臂（两臂都要各自的环境时用它，
# 不要把一臂的变量放进进程环境：跑完另一臂后 pop 会把同名键一起删掉）。
ON_ENV = dict(item.split('=', 1) for item in os.environ.get('R7_ON_ENV', '').split(',') if '=' in item)


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


def one(arm, rep, name, first):
    _, loaded = wait_quiet(MAXLOAD, LOAD_CAP)
    saved = run.CLEAT
    expected_host = run.host(BASE if arm == 'cleat-off' else saved)
    try:
        if arm == 'cleat-off':
            if OFF_ENV:
                run.env.update(OFF_ENV)
            run.CLEAT = BASE
        elif ON_ENV:
            run.env.update(ON_ENV)
        r = run.run('cleat', name)
    finally:
        for key in list(OFF_ENV) + list(ON_ENV):
            run.env.pop(key, None)
        run.CLEAT = saved
    r.update(label=arm, rep=rep, nopt=None)
    r['host'] = expected_host
    r['off_env'] = OFF_ENV if arm == 'cleat-off' else {}
    r['on_env'] = ON_ENV if arm == 'cleat-on' else {}
    r['order'] = ('new' if arm == 'cleat-on' else 'base') + ('-first' if first else '-second')
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
    print('# arms=cleat-on,cleat-off reps=%d items=%d new=%s base=%s on_env=%s off_env=%s quiet<=%.1f cap=%ds'
          % (reps, len(names), run.host(), run.host(BASE), ON_ENV, OFF_ENV, MAXLOAD, LOAD_CAP), flush=True)
    out_path = os.path.join(B, 'results', OUT + '.jsonl')
    done = completed(out_path)
    if done:
        print('# resume: skipping %d completed (label, rep, name) cells in %s' % (len(done), out_path), flush=True)
    for rep in range(1, reps + 1):
        # 臂序轮换：偶数轮先跑基线（SAMEORDER=1 时保持旧的固定次序）
        arms = ('cleat-on', 'cleat-off') if SAMEORDER or rep % 2 == 1 else ('cleat-off', 'cleat-on')
        for name in names:
            for pos, arm in enumerate(arms):
                if (arm, rep, name) in done:
                    continue
                one(arm, rep, name, pos == 0)
