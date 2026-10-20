#!/usr/bin/env python3
# -*- coding: utf-8 -*-
# cleat-149 回归二分 / 线性扫描（git bisect run 的目标脚本）。
#
# 为什么是"配对"判定：本机与 192.168.3.111 都有并发（其它 worktree 的构建 / 测试、机器负载），
# 绝对秒数会漂。所以每一步都把**当前提交**与一个固定的**参照构建**（good 端点 6ed0da4 的产物）
# 在同一轮里交替跑同一批项，判定用同轮配对比值的中位数（几何平均）—— 漂移对两边同等作用，
# 比值是可直接比较的量。阈值 --ratio-threshold（默认 1.05，即比参照慢 5% 判 bad）。
#
# 用法（在被二分的临时 worktree 上跑，参照构建另放一个目录）：
#   git -C /tmp/m15-149 bisect start 65d407d 6ed0da4
#   git -C /tmp/m15-149 bisect run python3 <本仓库>/bench/r7rs/bisect149.py \
#        --worktree /tmp/m15-149 --ref /tmp/bisect-149 --items pi,mbrot --reps 3
#   git -C /tmp/m15-149 bisect reset
# 线性扫描（检验二分的单调性假设，噪声机器上更可信）：
#   python3 bench/r7rs/bisect149.py --linear 6ed0da4..65d407d --worktree /tmp/m15-149 --ref /tmp/bisect-149
#
# 退出码：0=good 1=bad 125=跳过（构建失败 / 基准跑不起来 / 构建产物缺失）——与 git bisect run 约定一致。
# 每次的 sha、每项轮次、配对比值、score、判定都追加到 --log（默认 /tmp/bisect149.log），作为证据留档。
import argparse, json, math, os, re, statistics, subprocess, sys, time

B = os.path.dirname(os.path.abspath(__file__))
R7 = os.environ['R7RS_BENCH']
DLL_TAIL = 'src/Cleat.Host/bin/Release/net10.0/cleat.dll'


def sha_of(worktree):
    return subprocess.run(['git', '-C', worktree, 'rev-parse', 'HEAD'],
                          capture_output=True, text=True).stdout.strip()


def build(worktree, log):
    t0 = time.time()
    p = subprocess.run(['dotnet', 'build', '-c', 'Release', '-v', 'minimal', '-nologo'],
                       cwd=worktree, capture_output=True, text=True, timeout=2400, errors='replace')
    ok = p.returncode == 0 and os.path.exists(os.path.join(worktree, DLL_TAIL))
    log.write('build %s @%s rc=%d %.0fs ok=%s\n' % (worktree, sha_of(worktree)[:8], p.returncode, time.time() - t0, ok))
    if not ok:
        log.write((p.stdout or '')[-2500:] + (p.stderr or '')[-1500:] + '\n')
    log.flush()
    return ok


def one(root, item, src, timeout=900):
    cmd = ['dotnet', os.path.join(root, DLL_TAIL), '-I', src, '-m', 'r7rs.' + item]
    try:
        p = subprocess.run(cmd, stdin=open(os.path.join(R7, 'inputs', item + '.input')),
                           capture_output=True, text=True, cwd=R7, timeout=timeout, errors='replace')
    except subprocess.TimeoutExpired:
        return None
    m = re.search(r'\+!CSVLINE!\+[^,]*,[^,]*,([0-9.eE+-]+)\s*$', p.stdout + p.stderr, re.M)
    return float(m.group(1)) if m else None


def paired(root_cur, root_ref, items, reps, src, log):
    """同轮交替：每个 rep 依次跑 ref / cur 的每一项。返回 {item: [cur_i / ref_i]}"""
    ratios = {n: [] for n in items}
    raw = {n: {'ref': [], 'cur': []} for n in items}
    for rep in range(reps):
        for lab, root in (('ref', root_ref), ('cur', root_cur)):
            for n in items:
                s = one(root, n, src)
                if s is None:
                    log.write('RUNFAIL %s %s\n' % (lab, n))
                    return None
                raw[n][lab].append(s)
        for n in items:
            ratios[n].append(raw[n]['cur'][-1] / raw[n]['ref'][-1])
        log.write('  rep%d %s\n' % (rep, ' '.join('%s:%.1f/%.1f=%.3f' % (n, raw[n]['ref'][-1], raw[n]['cur'][-1], ratios[n][-1]) for n in items)))
        log.flush()
    return {'ratios': ratios, 'raw': raw}


def score_of(ratios, items):
    vals = [statistics.median(ratios[n]) for n in items if ratios.get(n)]
    return math.exp(sum(map(math.log, vals)) / len(vals)) if vals else None


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--worktree', default='/tmp/m15-149')
    ap.add_argument('--ref', default='/tmp/bisect-149')
    ap.add_argument('--src', default=os.path.join(B, 'src'))
    ap.add_argument('--items', default='pi,mbrot')
    ap.add_argument('--reps', type=int, default=3)
    ap.add_argument('--ratio-threshold', type=float, default=1.05)
    ap.add_argument('--fuzzy', type=float, default=0.02, help='score 距阈值不超过此值时判不确定（退出 125，让 bisect 跳过）')
    ap.add_argument('--linear', help='线性扫描该区间（如 6ed0da4..65d407d），逐项打印配对比值，不做二分')
    ap.add_argument('--log', default='/tmp/bisect149.log')
    a = ap.parse_args()
    items = a.items.split(',')
    log = open(a.log, 'a')
    log.write('=== %s items=%s reps=%d thr=%.3f ref=%s\n' % (time.strftime('%F %T'), items, a.reps, a.ratio_threshold, a.ref))

    def step():
        sha = sha_of(a.worktree)
        if not build(a.worktree, log):
            return sha, None, 'skip(build)'
        got = paired(os.path.abspath(a.worktree), os.path.abspath(a.ref), items, a.reps, a.src, log)
        if got is None:
            return sha, None, 'skip(基准跑不起来)'
        sc = score_of(got['ratios'], items)
        per = ' '.join('%s=%.3f' % (n, statistics.median(got['ratios'][n])) for n in items)
        verdict = 'bad' if sc >= a.ratio_threshold + a.fuzzy else ('good' if sc <= a.ratio_threshold - a.fuzzy else 'uncertain')
        log.write('%s score=%.4f %s -> %s\n' % (sha, sc, per, verdict))
        log.flush()
        return sha, sc, '%s(%s) %s' % (verdict, per, 'score=%.3f' % sc)

    if a.linear:
        shas = subprocess.run(['git', '-C', a.worktree, 'rev-list', '--reverse', a.linear],
                              capture_output=True, text=True).stdout.split()
        tip = shas[-1]
        print('# 线性扫描 %d 个提交，items=%s，判定阈值 %.3f（配对比值 vs 参照 %s）' % (len(shas), items, a.ratio_threshold, a.ref), flush=True)
        for sha in shas:
            subprocess.run(['git', '-C', a.worktree, 'checkout', '-q', '--detach', sha])
            s, sc, txt = step()
            print('%s %s' % (s[:8], txt), flush=True)
        subprocess.run(['git', '-C', a.worktree, 'checkout', '-q', '--detach', tip])
        return 0

    s, sc, txt = step()
    print('%s %s' % (s[:8], txt), flush=True)
    return {'bad': 1, 'good': 0}.get(txt.split('(')[0], 125)


if __name__ == '__main__':
    sys.exit(main())
