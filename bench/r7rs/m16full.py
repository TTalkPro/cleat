#!/usr/bin/env python3
# cleat-150 的整体重跑 runner：三臂（cleat 开优化 / cleat 关优化 / JVM Clojure）在**同一个基准 × 同一轮**的窗口里交替跑，
# 使"开关贡献分解"与"对 JVM 的倍数"用的是同一台机器、同一时段的数字，而不是跨轮次拼出来的。
#
# 用法：
#   R7RS_BENCH=~/workspace/r7rs-benchmarks CLEAT=~/workspace/cleat \
#     python3 bench/r7rs/m16full.py REPS [基准名 …]
#   不给基准名则跑 src/r7rs 下全部 56 项；结果追加到 results/m16full.jsonl（用 R7_OUT 改名）。
#
# 关优化臂的环境变量是 CLEAT_NO_OPT，默认一次全关 M16 落地的六项开关
# （charbox,strseq,intern,nthfast,crossnstype,accinline），回到 M15 收口（cleat-141）的行为。
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
#   - 同机并发会污染数字：跑之前确认没有别的构建 / 测试在同一台机器上。每条记录都带 load
#     （os.getloadavg()[0]，采样于该次运行结束）与 loaded（跑前等不到安静窗口），事后按这两列筛轮。
#   - 探针（micro.cljc / shapes*.cljc）必须让结果参与数据依赖，否则 JVM 侧会被 LICM + 标量替换消掉。
#
# ── 192.168.3.111 自 2026-10-20 下午起离线（ping 100% 丢包、ssh 连不上），三臂全量改在本机跑 ──
#     本机 i9-13900H / 8 线程 / .NET SDK 10.0.401；cleat-149 §2 实测本机等过 load 之后
#     同一构建同一项的 5 轮极差仍达 17 ~ 43%（远端 4 vCPU 上极差 < 3%）。百分比精度在这里没有，
#     所以本机口径改成三条：
#   1. **判定只认同轮配对比值**（同一基准 × 同一轮的 on/off 与 on/jvm 秒数比，取中位 + 方向一致性）：
#      R 轮全部同向才声明方向，量级只给区间；单臂最小值之差 < 10% 记为"本机不可判定"，不写进结论。
#      汇总表见 m16sum.py 的"同轮配对比值"一节。
#   2. 每格跑前先等安静窗口（bench/r7rs/quiet.py：load1 <= R7_MAXLOAD，缺省 核数 x 25%，
#      最多等 R7_LOAD_CAP 秒），等不到照样跑但标 loaded=True，由 m16sum.py 缺省剔除。
#   3. 轮数按配对来取：收口至少 -r 5（远端的 3 轮取最小在本机太薄）。
#     JVM 臂还要显式指 HotSpot：本机 mise 的缺省 java 是 GraalVM CE 25，与 M15 的 1.61x
#     （111，OpenJDK 25 C2）不同口径。收口跑法：
#       JAVA=/usr/lib/jvm/java-25-openjdk-amd64/bin/java \
#       R7RS_BENCH=~/workspace/r7rs-benchmarks CLEAT=~/workspace/cleat \
#         python3 bench/r7rs/m16full.py 5
#     要 GraalVM 的上限读数就另跑一遍 R7_OUT=m16full-graal，两套不要混进同一张表。
#     跨机比较绝对值时仍要注明 runtime 版本（111 只有 10.0.0，本机 10.0.12）。
import json
import os
import sys

B = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, B)
import run
from quiet import LOAD_CAP, MAXLOAD, wait_quiet

DEFAULT_NOPT = 'charbox,strseq,intern,nthfast,crossnstype,accinline'
NOPT = os.environ.get('NOPT', DEFAULT_NOPT)
ARMS = os.environ.get('ARMS', 'cleat-on,cleat-off,jvm').split(',')
OUT = os.environ.get('R7_OUT', 'm16full')


def one(arm, rep, name):
    saved = os.environ.get('CLEAT_NO_OPT')
    try:
        # 跑前等安静窗口（本机口径第 2 条）：等不到也跑，只把污染记进 loaded
        _, loaded = wait_quiet(MAXLOAD, LOAD_CAP)
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
    # 日志开头自述机器与口径，否则事后分不清哪一轮是 HotSpot、哪一轮是 GraalVM
    print('# arms=%s reps=%d items=%d NOPT=%s quiet<=%.1f cap=%ds JAVA=%s JVM=%s' % (
        ','.join(ARMS), reps, len(names), NOPT, MAXLOAD, LOAD_CAP,
        os.environ.get('JAVA', 'java（PATH 缺省）'),
        run.jvm_version() if 'jvm' in ARMS else '-'), flush=True)
    for rep in range(1, reps + 1):
        for name in names:
            for arm in ARMS:
                one(arm, rep, name)
