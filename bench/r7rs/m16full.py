#!/usr/bin/env python3
# cleat-150 的整体重跑 runner：三臂（cleat 开优化 / cleat 关优化 / JVM Clojure）在**同一个基准 × 同一轮**的窗口里交替跑，
# 使"开关贡献分解"与"对 JVM 的倍数"用的是同一台机器、同一时段的数字，而不是跨轮次拼出来的。
#
# 用法：
#   R7RS_BENCH=~/workspace/r7rs-benchmarks CLEAT=~/workspace/cleat \
#     python3 bench/r7rs/m16full.py REPS [基准名 …]
#   不给基准名则跑 src/r7rs 下全部 56 项；结果追加到 results/m16full.jsonl（用 R7_OUT 改名）。
#
# 关优化臂的环境变量是 CLEAT_NO_OPT，默认一次全关 M16 落地的五项开关
# （charbox,strseq,intern,nthfast,crossnstype），回到 M15 收口（cleat-141）的行为。
# 名单的唯一出处是 notes/m16-regression-triage.md §1 的开关表（cleat-149 待办第 3 条）：
# 加新开关时两边一起改，否则关侧残留新优化，"开关贡献分解"就是错的（本轮已踩过一次）。
# 不进名单的两项：gcprofile 关掉只关掉声明校验、不改任何 GC 配置（对时间零影响）；
# tiered / known / loopinl / infer 那些是 M14 / M15 的开关，关下去就不是与 M15 的对照了。
# 单项归因：ARMS=cleat-on,cleat-off NOPT=charbox python3 bench/r7rs/m16full.py 3 browse wc lattice
# （只关一项时开臂 / 关臂只差那一项，JVM 臂不用重复跑）。
#
# 口径（与 notes/r7rs-performance.md 的 M15 §6、M16 §4 一致）：
#   - 每项取各轮最小值；**轮内极差 > 3% 的差异一律视为噪声**，不据此声明收益或回退。
#   - 双态项（takl）不要只看 min-of-3，要报告区间：JVM takl 快态约 12 s、慢态 34 ~ 37 s。
#   - 远端机只有 .NET runtime 10.0.0（本地 10.0.12）：跨机比较绝对值前先对齐 runtime 版本。
#   - 同机并发会污染数字（远端 4 vCPU）：跑之前确认没有别的构建 / 测试在同一台机器上。
#     每条记录都带 load（os.getloadavg()[0]，采样于该次运行结束后），事后按 load 筛掉被污染的轮；
#     cleat-149 §2 实测本机等过 load 之后同一构建同一项的 5 轮极差仍达 17 ~ 43%，
#     所以**任何 <10% 的判定只能在 192.168.3.111 上做**，且要看配对而不是单臂最小值。
#   - 探针（micro.cljc / shapes*.cljc）必须让结果参与数据依赖，否则 JVM 侧会被 LICM + 标量替换消掉。
import json
import os
import sys

B = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, B)
import run

DEFAULT_NOPT = 'charbox,strseq,intern,nthfast,crossnstype'
NOPT = os.environ.get('NOPT', DEFAULT_NOPT)
ARMS = os.environ.get('ARMS', 'cleat-on,cleat-off,jvm').split(',')
OUT = os.environ.get('R7_OUT', 'm16full')


def one(arm, rep, name):
    saved = os.environ.get('CLEAT_NO_OPT')
    try:
        if arm == 'cleat-on':
            os.environ.pop('CLEAT_NO_OPT', None)
            r = run.run('cleat', name)
        elif arm == 'cleat-off':
            os.environ['CLEAT_NO_OPT'] = NOPT
            r = run.run('cleat', name)
        elif arm == 'jvm':
            r = run.run('clojure', name)
            r['jvm'] = run.jvm_version()
        else:
            raise SystemExit('unknown arm: ' + arm)
    finally:
        if saved is None:
            os.environ.pop('CLEAT_NO_OPT', None)
        else:
            os.environ['CLEAT_NO_OPT'] = saved
    r.update(label=arm, rep=rep, nopt=(NOPT if arm == 'cleat-off' else None))
    # 采样时刻的 1 分钟负载：并行 / 并发污染的事后判据（见头部口径）
    r['load'] = round(os.getloadavg()[0], 2)
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
    for rep in range(1, reps + 1):
        for name in names:
            for arm in ARMS:
                one(arm, rep, name)
