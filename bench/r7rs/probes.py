#!/usr/bin/env python3
# -*- coding: utf-8 -*-
# 里程碑收尾用的探针对照跑器（cleat-149）：把 micro / shapes2 / shapes3 在同一窗口里对两个宿主各跑 N 次，
# 汇总成一张 net 列对照表，并**按自检标记剔除不可用的行**。
#
# 为什么要脚本化（designs/28 §1 / §5.2，M15 的教训）：
#   探针结果只要不参与数据依赖，C2 / Graal 就用 LICM + 标量替换把整个调用提出循环，JVM 侧剩下的只是循环开销；
#   cleat 侧不做这种消除，于是"JVM 0.7 ns vs cleat 15 ns"这类倍数是伪影。探针文件已经把形状改成
#   r7rs/probe.cljc 的 timed（写 sink + 不透明实参 + 环底），本脚本负责把两侧的读数配对出表，
#   并把标了 ARTIFACT? / HOISTED? / DEAD-VALUE 的行**排除在倍数之外**（这些行只说明自检在工作）。
#
# 用法：R7RS_BENCH=… python3 bench/r7rs/probes.py [--reps 3] [--modules micro,shapes2,shapes3] [--cleat PATH]
#   原始行存 results/probes-<时间戳>.jsonl；表格按 min-of-N（每侧每个形状取最小），倍数只算 net 列。
import json, os, re, statistics, subprocess, sys, time

B = os.path.dirname(os.path.abspath(__file__))
SRC = os.path.join(B, 'src')
CLEAT = os.environ.get('CLEAT', os.path.dirname(os.path.dirname(B)))
JAVA = os.environ.get('JAVA', 'java')
os.environ.setdefault('DOTNET_ROOT', os.path.expanduser('~/.dotnet'))
os.environ['PATH'] = os.environ['DOTNET_ROOT'] + ':' + os.environ['PATH']
BADFLAGS = ('ARTIFACT?', 'HOISTED?', 'DEAD-VALUE')


def jvm_classpath():
    out = subprocess.run(['clojure', '-Spath'], cwd=B, capture_output=True, text=True, check=True).stdout.strip()
    return ':'.join(os.path.join(B, p) if not os.path.isabs(p) else p for p in out.split(':'))


def parse_line(line):
    """两种探针出图格式（probe.cljc 的 report! 与 micro.cljc 的 line）：
         PROBE-FLOOR skeleton        4.52 ns
         PROBE mcar-read     raw=  12.34 net=   6.38 ok chk=987654
         MICRO-FLOOR empty/load       1.45 /   3.20 ns
         MICRO mcar-read        12.3 ns  net-empty=  10.9 net-load=   9.1 SUSPECT(<=环底)
       -> (形状名, {字段: 值}, [自检标记], 是否环底行)。printf 的 %7.2f 会在 = 后留空格，先压缩。"""
    m = re.match(r'^(PROBE-FLOOR|MICRO-FLOOR|PROBE|MICRO)\s+(.*)$', line.strip())
    if not m:
        return None
    kind, rest = m.group(1), re.sub(r'([a-zA-Z_-]+=)\s+', r'\1', m.group(2))
    toks = rest.split()
    if not toks:
        return None
    name, vals, flags = toks[0], {}, []
    bare = []
    for t in toks[1:]:
        kv = re.match(r'^([a-zA-Z_-]+)=(-?[0-9.]+)$', t)
        if kv:
            vals[kv.group(1)] = float(kv.group(2))
        elif re.match(r'^-?[0-9.]+$', t):
            bare.append(float(t))
        elif t in ('ns', '/', 'ok', 'ok)'):
            continue
        elif 'chk' in t:
            continue
        else:
            flags.append(t)
    floor = kind.endswith('FLOOR')
    if bare:                                             # PROBE 的环底是裸数字；MICRO 给 empty / load 两个
        vals['raw' if not floor else 'floor'] = bare[0]
        if floor and len(bare) > 1:
            vals['floor2'] = bare[1]
    return name, vals, flags, floor


def run_probe(impl, mod, cleat_root, timeout=900):
    if impl == 'cleat':
        cmd = ['dotnet', os.path.join(cleat_root, 'src/Cleat.Host/bin/Release/net10.0/cleat.dll'),
               '-I', SRC, '-m', mod]
    else:
        cmd = [JAVA, '-Xss8m', '-cp', SRC + ':' + jvm_classpath(), 'clojure.main', '-m', mod]
    try:
        p = subprocess.run(cmd, capture_output=True, text=True, timeout=timeout, errors='replace')
    except subprocess.TimeoutExpired:
        return None, 'TIMEOUT'
    out = p.stdout + p.stderr
    rows = {}
    floors = []
    for line in out.splitlines():
        r = parse_line(line)
        if not r:
            continue
        name, vals, flags, floor = r
        if floor:
            floors.append(vals)
        else:
            rows[name] = (vals, flags)
    if not rows:
        return None, out.strip()[-300:]
    return rows, None


def net_of(vals, flags):
    """用于倍数的净成本：PROBE 用 net，MICRO 用 net-empty（环底是纯循环骨架）。"""
    for k in ('net', 'net-empty', 'net-load'):
        if k in vals:
            return vals[k]
    return None


def main():
    a = sys.argv[1:]
    reps, mods = 3, ['micro', 'shapes2', 'shapes3']
    cleat = CLEAT
    i = 0
    while i < len(a):
        v = a[i]
        if v == '--reps':
            reps = int(a[i + 1]); i += 2
        elif v.startswith('--reps='):
            reps = int(v.split('=', 1)[1]); i += 1
        elif v == '--modules':
            mods = a[i + 1].split(','); i += 2
        elif v.startswith('--modules='):
            mods = v.split('=', 1)[1].split(','); i += 1
        elif v == '--cleat':
            cleat = a[i + 1]; i += 2
        else:
            raise SystemExit('unknown option ' + v)
    print('# probes：reps=%d modules=%s cleat=%s' % (reps, ','.join(mods), cleat), flush=True)
    print('# load: %s' % os.popen('uptime').read().strip(), flush=True)
    respath = os.path.join(B, 'results', 'probes-%s.jsonl' % time.strftime('%Y%m%d-%H%M%S'))
    os.makedirs(os.path.dirname(respath), exist_ok=True)
    f = open(respath, 'w')
    best = {}                                          # (mod, side, name) -> 最小 raw / 最小 net
    floors = {}                                        # (mod, side) -> [环底最小值]
    flags = {}                                         # (mod, side, name) -> 标记
    order = {}                                         # (mod, side) -> 形状出现顺序
    for rep in range(reps):
        for mod in mods:
            for side in ('cleat', 'jvm'):
                rows, err = run_probe(side, mod, cleat)
                if rows is None:
                    print('  rep%d %-8s %-5s 失败：%s' % (rep, mod, side, str(err)[:120]), flush=True)
                    continue
                f.write(json.dumps({'rep': rep, 'module': mod, 'side': side,
                                    'rows': {k: [v[0], v[1]] for k, v in rows.items()}}) + '\n')
                f.flush()
                for nm, (vals, fl) in rows.items():
                    for k in ('raw', 'net', 'net-empty', 'net-load'):
                        if k in vals:
                            key = (mod, side, nm, k)
                            best[key] = min(best.get(key, 1e300), vals[k])
                    if fl:
                        flags[(mod, side, nm)] = fl
                    order.setdefault((mod, side), []).append(nm)
                print('  rep%d %-8s %-5s %d 个形状' % (rep, mod, side, len(rows)), flush=True)
    f.close()
    print('# 原始行：%s' % respath, flush=True)

    for mod in mods:
        names = []
        for side in ('cleat', 'jvm'):                 # 以 cleat 侧的顺序为准，保持形状原有次序
            for nm in order.get((mod, side), []):
                if nm not in names:
                    names.append(nm)
        rows_out, usable, bad = [], 0, 0
        for nm in names:
            def g(side, k):
                return best.get((mod, side, nm, k))
            c_raw, c_net = g('cleat', 'raw'), g('cleat', 'net') or g('cleat', 'net-empty')
            j_raw, j_net = g('jvm', 'raw'), g('jvm', 'net') or g('jvm', 'net-empty')
            fl = flags.get((mod, 'cleat', nm), []) + flags.get((mod, 'jvm', nm), [])
            dead = any(any(b in x for b in BADFLAGS) for x in fl)
            ratio = (c_net / j_net) if (c_net and j_net and c_net > 0 and j_net > 0 and not dead) else None
            if ratio:
                usable += 1
            else:
                bad += 1
            rows_out.append((nm, c_raw, c_net, j_raw, j_net, ratio, ' '.join(fl)))
        if not rows_out:
            print('\n## %s：没有数据' % mod, flush=True)
            continue
        print('\n## %s（min-of-%d，ns/次；倍数 = cleat net / JVM net，被标记的行不出倍数）' % (mod, reps), flush=True)
        print('%-22s %10s %10s %10s %10s %8s  %s' % ('形状', 'cleat raw', 'cleat net', 'JVM raw', 'JVM net', '倍数', '自检'))
        for nm, cr, cn, jr, jn, r, fl in rows_out:
            fmt = lambda x: ('%10.2f' % x) if x is not None else ('%10s' % '—')
            print('%-22s %s %s %s %s %8s  %s' % (nm, fmt(cr), fmt(cn), fmt(jr), fmt(jn),
                                                 ('%.2fx' % r) if r else '不可用', fl), flush=True)
        print('  可用 %d / 共 %d（%d 行被自检排除——它们存在的意义就是证明伪影能被抓出来）'
              % (usable, len(rows_out), bad), flush=True)
    print('# 提醒：本表只做单操作对照，跨宿主的端到端倍数以 compare.py（r7rs 全量）为准；'
          '双态项（如 takl）在 compare.py 里报区间。', flush=True)


if __name__ == '__main__':
    main()
