#!/usr/bin/env python3
# -*- coding: utf-8 -*-
# cleat-149 回归排查跑分器：同一窗口内交替跑多个"设置"（CLEAT_NO_OPT 开关组合 / 不同构建），每项取 N 次最小。
#
# ── 口径（与 run.py / attr.py 一致）─────────────────────────────────────────
# 1. 只计基准内部计时（+!CSVLINE!+），不含启动与编译；轮次 x 设置 x 项 交替跑，机器漂移对每个设置公平。
# 2. **探针 / 基准的对照数字必须有数据依赖**：一项的结果若不参与后续计算（写进 sink / 条件分支），
#    C2 / Graal 会用 LICM + 标量替换把整个调用提出循环，JVM 侧数字只剩循环开销，不是操作的成本（shapes3 的教训）。
#    cleat 侧没有这种消除，所以死值探针上"JVM 0.7 ns vs cleat 15 ns"这类倍数是伪影。本脚本跑真实基准
#    （内部有 hide / 累加器），不受影响；跑探针请用 bench/r7rs/src/{micro,shapes2,shapes3}.cljc 的自检输出。
# 3. min-of-N 对**双态项**不可靠（takl 的 JVM 有快态 ~12 s / 慢态 34 ~ 37 s）：每格同时报 min/median 与全部轮次，
#    max/min > R7_DUAL_RATIO（默认 1.5）时标 `双态?` —— 这时只能用区间，不能取最小。
# 4. 跑前看 load（开头打印 uptime 与 dotnet 版本）；有并发时只信同窗口交替对照，不信绝对值。
# 5. 设置顺序固定本身就是偏差：并发机器上负载随时间变化，base 永远第一个跑，后面的设置整体偏挤或偏松。
#    矩阵与端点对照加 --shuffle（每轮打乱顺序），主口径用同轮配对比值的中位。
#
# ── 用法 ────────────────────────────────────────────────────────────────────
#   R7RS_BENCH=$HOME/workspace/r7rs-benchmarks CLEAT=<仓库> python3 bench/r7rs/triage149.py -r 3 parsing pi mbrot
#   --settings base,nosig=no:sigspec,norun=no:strseq,intern
#   --settings alloff=env:CLEAT_NO_OPT=1+no:known+no:loopinl+no:strseq+no:intern+no:tiered
#              （CLEAT_NO_OPT=1 只关 codegen / front 那批，analyzer 的 known / loopinl 与运行时
#               strseq / intern / tiered 要逐个列，见 notes/m16-regression-triage.md §1 的坑 1）
#   --settings old=build:/tmp/bisect-149,new=build:$PWD        # 换构建对照（bisect 用）
#   设置 spec：base 什么都不设；no:a,b -> CLEAT_NO_OPT=a,b；env:K=V;K2=V2 -> 任意环境变量；
#              build:PATH -> 用另一个仓库的 Release 构建；可用 + 串联：build:/tmp/x+no:sigspec
#   --shuffle            每轮打乱设置顺序：去掉「固定顺序 + 负载随时间漂移」的系统性偏差（base 永远第一个跑）
#   原始记录追加到 results/triage149-<R7_OUT>.jsonl（默认时间戳）；跑一半也能读：summarize149.py。
import json, os, re, random, subprocess, sys, time, statistics
from quiet import LOAD_CAP, MAXLOAD, load1, wait_quiet   # 安静窗口门槛的唯一出处（cleat-150 起 m16full.py 也用它）


B = os.path.dirname(os.path.abspath(__file__))
R = os.environ['R7RS_BENCH']
DEFAULT_CLEAT = os.environ.get('CLEAT', os.path.dirname(os.path.dirname(B)))
SRC = os.environ.get('CLEAT_R7RS_SRC', os.path.join(B, 'src'))
DUAL_STATE_RATIO = float(os.environ.get('R7_DUAL_RATIO', '1.5'))
os.environ.setdefault('DOTNET_ROOT', os.path.expanduser('~/.dotnet'))
os.environ['PATH'] = os.environ['DOTNET_ROOT'] + ':' + os.environ['PATH']


def parse(out):
    m = re.search(r'\+!CSVLINE!\+[^,]*,[^,]*,([0-9.eE+-]+)\s*$', out, re.M)
    return float(m.group(1)) if m else None


def rtspec_stats(out):
    """CLEAT_RTSPEC_STATS=1 时进程退出会打印一行 `rtspec: compiled N failed M compile-ms X`（RuntimeSigSpec.cs）。
    它是"运行时特化机器本身的开销"的直接读数：compiled 次数与 compile-ms 落在后台线程上，
    与调用线程抢 CPU，所以配对比值异常而开关(sigspec)能恢复时，用这一列把因果说清楚。"""
    m = re.search(r'rtspec: compiled (\d+) failed (\d+) compile-ms (\d+)', out)
    return {'compiled': int(m.group(1)), 'failed': int(m.group(2)), 'compileMs': int(m.group(3))} if m else None


def split_setting(entry):
    """'label=spec' -> (label, spec)；没有 label 时 spec 自身作 label。env:K=V 整段是 spec。"""
    if entry.startswith('env:') or '=' not in entry:
        return entry if entry != 'base' else 'base', entry
    label, spec = entry.split('=', 1)
    return label, spec


def spec_to_env_build(spec):
    """设置 spec -> (env dict, cleat 根目录)。用 + 串联多段。"""
    env, root = {}, DEFAULT_CLEAT
    # env:K=V;K2=V2 是一整段（分号分隔多个变量，逗号会被 --settings 的逗号切分吃掉，所以用分号）；
    # 其余段用 + 串联（no:a,b / build:PATH）。
    # 段可自由串联：env: 在前时后面的 no: 会把开关追加到同一个 CLEAT_NO_OPT（"1" 只在 codegen / front 认，
    # analyzer 的 known / loopinl 与运行时 strseq / intern / tiered 必须逐个列 —— 见 notes/m16-regression-triage.md §1 的坑 1）。
    # 值里不能含 + ；多个变量用 ; 分隔：env:A=1;B=2
    parts = []
    for p in spec.split('+'):
        if p.startswith('env:'):
            parts += [('env:' + kv) for kv in p[4:].split(';') if kv]
        else:
            parts.append(p)
    for part in parts:
        if part in ('base', '', '-'):
            continue
        if part.startswith('no:'):
            prev = env.get('CLEAT_NO_OPT', '')
            env['CLEAT_NO_OPT'] = ','.join(filter(None, [prev, part[3:]]))
        elif part.startswith('build:'):
            root = os.path.abspath(part[6:])
        elif part.startswith('env:'):
            k, _, v = part[4:].partition('=')
            env[k] = v
        else:
            raise SystemExit('unknown setting spec: ' + spec)
    return env, root


def run_one(cleat_root, name, env, timeout):
    dll = os.path.join(cleat_root, 'src/Cleat.Host/bin/Release/net10.0/cleat.dll')
    cmd = ['dotnet', dll, '-I', SRC, '-m', 'r7rs.' + name]
    e = dict(os.environ, **env)
    t0 = time.time()
    try:
        p = subprocess.run(cmd, stdin=open(os.path.join(R, 'inputs', name + '.input')),
                           capture_output=True, text=True, cwd=R, timeout=timeout, env=e, errors='replace')
    except subprocess.TimeoutExpired:
        return None, 'TIMEOUT', time.time() - t0, None
    out = p.stdout + p.stderr
    secs = parse(out)
    return secs, (None if secs is not None else out.strip()[-400:]), time.time() - t0, rtspec_stats(out)


def main():
    a = sys.argv[1:]
    reps, timeout, items, out_tag, shuffle = 3, 900, [], os.environ.get('R7_OUT', time.strftime('%Y%m%d-%H%M%S')), False
    settings = os.environ.get('R7_SETTINGS', 'base').split(',')
    i = 0
    while i < len(a):
        v = a[i]
        if v == '-r':
            reps = int(a[i + 1]); i += 2
        elif v == '--settings':
            settings = a[i + 1].split(','); i += 2
        elif v == '--timeout':
            timeout = int(a[i + 1]); i += 2
        elif v == '--shuffle':
            shuffle = True; i += 1
        elif v.startswith('-'):
            raise SystemExit('unknown option ' + v)
        else:
            items.append(v); i += 1
    if not items:
        items = sorted(f[:-5] for f in os.listdir(os.path.join(SRC, 'r7rs'))
                       if f.endswith('.cljc') and f not in ('common.cljc', 'util.cljc'))
    eb = []
    for entry in settings:
        label, spec = split_setting(entry)
        env, root = spec_to_env_build(spec)
        eb.append({'label': label, 'env': env, 'root': root})
    res = {(s['label'], n): [] for s in eb for n in items}
    resnoisy = {(s['label'], n): [] for s in eb for n in items}   # 等待超时后仍被并发污染的轮次：不入主口径，只做参考
    per_rep = {}                                        # (rep, setting, item) -> secs：同轮配对用
    per_load = {}                                       # 同一 key -> 运行前 load1，配对时说明窗口质量
    stats_acc = {}                                      # (setting, item) -> [CLEAT_RTSPEC_STATS 读数]：特化机器自身开销
    errs = {}
    print('# load: %s' % os.popen('uptime').read().strip(), flush=True)
    print('# dotnet: %s' % os.popen('dotnet --version 2>&1').read().strip(), flush=True)
    print('# src: %s' % SRC, flush=True)
    print('# reps=%d timeout=%d items=%s' % (reps, timeout, ','.join(items)), flush=True)
    for s in eb:
        print('# setting %-20s root=%s env=%s' % (s['label'], s['root'], s['env'] or '{}'), flush=True)
    respath = os.path.join(B, 'results', 'triage149-%s.jsonl' % out_tag)
    os.makedirs(os.path.dirname(respath), exist_ok=True)
    f = open(respath, 'a')
    max_attempts = int(os.environ.get('R7_ATTEMPTS', str(reps * 4)))
    attempt = 0
    while attempt < max_attempts and any(len(v) < reps for v in res.values()):
        rep = attempt; attempt += 1
        order = list(eb)                                 # 交替顺序：轮次 x 设置 x 项
        if shuffle:                                      # 每轮打乱设置顺序：固定顺序会把"机器负载随时间漂移"
            random.Random(rep * 7919 + 11).shuffle(order) # 变成系统性偏差（base 永远排第一，后面的设置要么一直更挤要么一直更松）
        for s in order:
            for n in items:
                if len(res[(s['label'], n)]) >= reps:     # 该项这个设置已经攒够干净轮次
                    continue
                l, loaded = wait_quiet(MAXLOAD, LOAD_CAP)
                secs, err, wall, stats = run_one(s['root'], n, s['env'], timeout)
                f.write(json.dumps({'rep': rep, 'setting': s['label'], 'name': n, 'secs': secs,
                                    'wall': round(wall, 3), 'env': s['env'], 'root': s['root'],
                                    'load': round(l, 2), 'loaded': loaded, 'error': err,
                                    'rtspec': stats}, ensure_ascii=False) + '\n')
                f.flush()
                if secs is None:
                    errs[(s['label'], n)] = (err or '')[:160]
                else:
                    if loaded:
                        resnoisy[(s['label'], n)].append(secs)
                    if not loaded:                          # 安静窗口内的轮次才进 min/median 主口径
                        res[(s['label'], n)].append(secs)
                    # 配对比值对所有轮次都用：同一轮里两边受同样的并发影响，比值比各自最小值可靠
                    per_rep[(rep, s['label'], n)] = secs
                    per_load[(rep, s['label'], n)] = l
                    if stats:
                        stats_acc.setdefault((s['label'], n), []).append(stats)
                    print('  rep%d %-20s %-10s %8.3f%s%s' % (rep, s['label'], n, secs,
                                                            '  [load=%.1f 超时等待，另记 resnoisy]' % l if loaded else '',
                                                            '  rtspec compiled=%d failed=%d ms=%d' % (
                                                                stats['compiled'], stats['failed'], stats['compileMs'])
                                                            if stats else ''), flush=True)
    if any(len(v) < reps for v in res.values()):
        print('# 注意：%d 次尝试后仍有格子不足 %d 个干净轮次（机器一直在忙），下面的比值基于不等样本数'
              % (attempt, reps), flush=True)
    f.close()
    print('\n=== min/median（相对参照设置的比；同窗口交替，静默轮 ≤ %d）===' % reps, flush=True)
    print('%-10s' % 'item' + ''.join('%26s' % s['label'] for s in eb), flush=True)
    ref = eb[0]['label']                                # 第一个设置作参照（通常是 base / 旧构建）
    for n in items:
        line = '%-10s' % n
        base = min(res[(ref, n)]) if res.get((ref, n)) else None
        for s in eb:
            v = res[(s['label'], n)]
            if not v:
                alt = resnoisy[(s['label'], n)]
                line += '%26s' % (('污染轮 %d 个，最差 %.2f（不可用）' % (len(alt), min(alt))) if alt
                                  else 'ERR ' + errs.get((s['label'], n), '')[:18])
                continue
            mn, md, mx = min(v), statistics.median(v), max(v)
            cnt = '' if len(v) == reps else '<%d>' % len(v)
            dual = ' 双态?' if mx / mn > DUAL_STATE_RATIO else ''
            ratio = '' if not base else ' (x%.3f)' % (mn / base)
            line += '%26s' % ('%.3f/%.3f%s%s%s' % (mn, md, cnt, ratio, dual))
        print(line, flush=True)
    # 同轮配对：机器漂移在"同一轮的相邻两次"里几乎抵消，比"各自最小值相除"可靠（并发机器上的主口径）
    print('\n=== 同轮配对比值 median(%s / %s)，逐轮列出 ===' % (' / '.join(s['label'] for s in eb[1:]), ref), flush=True)
    for n in items:
        for s in eb[1:]:
            pr = []
            loads = []
            for rep in sorted({k[0] for k in per_rep}):
                a = per_rep.get((rep, ref, n)); b = per_rep.get((rep, s['label'], n))
                if a and b:
                    pr.append(b / a)
                    loads.append(max(per_load[(rep, ref, n)], per_load[(rep, s['label'], n)]))
            if pr:
                quiet = [r for r, ld in zip(pr, loads) if ld <= MAXLOAD]
                flag = ' 双态?' if max(pr) / min(pr) > DUAL_STATE_RATIO else ''
                qtxt = ('  静默窗口中位 %.3f(%d 对)' % (statistics.median(quiet), len(quiet))) if quiet else '  无静默配对'
                print('  %-10s %-16s 全部中位 %6.3f%s  [%s]  (load %s)%s' % (
                    n, s['label'], statistics.median(pr), qtxt,
                    ' '.join('%.3f' % x for x in pr), ' '.join('%.1f' % x for x in loads), flag), flush=True)
    if stats_acc:                                        # 每个设置的实际编译次数（开了 CLEAT_RTSPEC_STATS 才有）
        print('\n=== rtspec 运行时编译次数 / 耗时（中位，开 CLEAT_RTSPEC_STATS 时才有这列）===', flush=True)
        for s in eb:
            for n in items:
                v = stats_acc.get((s['label'], n))
                if v:
                    print('  %-20s %-10s compiled=%d failed=%d compile-ms=%d (%d 轮)' % (
                        s['label'], n, statistics.median(x['compiled'] for x in v),
                        statistics.median(x['failed'] for x in v),
                        statistics.median(x['compileMs'] for x in v), len(v)), flush=True)
    for s in eb:                                         # 全部轮次，双态项要看这个
        for n in items:
            v = res[(s['label'], n)]
            if v:
                print('  rounds %-20s %-10s %s' % (s['label'], n, ['%.3f' % x for x in v]), flush=True)
    print('# max/min > %.1f 标 `双态?`：报告区间（min~max），不要取最小参与几何平均' % DUAL_STATE_RATIO, flush=True)
    if errs:
        print('# 错误：', errs, flush=True)


if __name__ == '__main__':
    main()
