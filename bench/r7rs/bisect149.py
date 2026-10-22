#!/usr/bin/env python3
# -*- coding: utf-8 -*-
# cleat-152 回归二分 / 线性扫描（git bisect run 的目标脚本）。
#
# 为什么是"配对"判定：本机与 192.168.3.111 都有并发（其它 worktree 的构建 / 测试、机器负载），
# 绝对秒数会漂。所以每一步都把**当前提交**与一个固定的**参照构建**（old 端点 6ed0da4 的产物）
# 在同一轮里交替跑同一批项；判定只认 151 定下的同轮配对口径：
#   每项配对比值中位 + 同向轮数；|中位 - 1| < R7_BISECT_MIN_RATIO 时输出"不可判定"。
# 三项（mbrot / pi / dynamic）都同向且过门槛才给 good / bad；否则不猜，退出 125 让 bisect 跳过。
#
# 用法（在被二分的临时 worktree 上跑，参照构建另放一个目录）：
#   git -C /tmp/m15-149 bisect start 65d407d 6ed0da4
#   git -C /tmp/m15-152 bisect start 65d407d 6ed0da4
#   git -C /tmp/m15-152 bisect run python3 <本仓库>/bench/r7rs/bisect149.py \
#        --worktree /tmp/m15-152 --ref /tmp/bisect-152 \
#        --items mbrot,pi,dynamic --reps 9
#   git -C /tmp/m15-152 bisect reset
# 线性扫描（检验二分的单调性假设，噪声机器上更可信）：
#   python3 bench/r7rs/bisect149.py --linear 6ed0da4..65d407d --worktree /tmp/m15-149 --ref /tmp/bisect-149
#
# 退出码：0=good 1=bad 125=跳过（构建失败 / 基准跑不起来 / 构建产物缺失）——与 git bisect run 约定一致。
# 每格的 sha、每项轮次、原始秒数、逐轮配对比值、中位、同向轮数和判定追加到 results/ 下的 jsonl。
import argparse, json, math, os, re, statistics, subprocess, sys, time

B = os.path.dirname(os.path.abspath(__file__))
R7 = os.environ['R7RS_BENCH']
DLL_TAIL = 'src/Cleat.Host/bin/Release/net10.0/cleat.dll'
DEFAULT_MIN_RATIO = float(os.environ.get('R7_BISECT_MIN_RATIO', '0.10'))


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
    """同轮交替：每个 rep 内，同一基准先 ref 后 cur 紧邻配对。返回 {item: [cur_i / ref_i]}"""
    ratios = {n: [] for n in items}
    raw = {n: {'ref': [], 'cur': []} for n in items}
    for rep in range(reps):
        for n in items:
            for lab, root in (('ref', root_ref), ('cur', root_cur)):
                s = one(root, n, src)
                if s is None:
                    log.write('RUNFAIL %s %s\n' % (lab, n))
                    return None
                raw[n][lab].append(s)
            ratios[n].append(raw[n]['cur'][-1] / raw[n]['ref'][-1])
        log.write('  rep%d %s\n' % (rep, ' '.join('%s:%.1f/%.1f=%.3f' % (n, raw[n]['ref'][-1], raw[n]['cur'][-1], ratios[n][-1]) for n in items)))
        log.flush()
    return {'ratios': ratios, 'raw': raw}


def score_of(ratios, items):
    vals = [statistics.median(ratios[n]) for n in items if ratios.get(n)]
    return math.exp(sum(map(math.log, vals)) / len(vals)) if vals else None


def summarize_paired(ratios, reps, min_ratio):
    """m16sum 的同轮配对口径：中位、方向和同向轮数；门槛内一律不可判定。"""
    md = statistics.median(ratios)
    if md > 1.0:
        direction, same_dir = 'slower', sum(x > 1.0 for x in ratios)
    elif md < 1.0:
        direction, same_dir = 'faster', sum(x < 1.0 for x in ratios)
    else:
        direction, same_dir = 'flat', sum(x == 1.0 for x in ratios)
    if abs(md - 1.0) < min_ratio:
        verdict = 'undecidable'
    elif same_dir != reps:
        verdict = 'undecidable'
    else:
        verdict = 'bad' if direction == 'slower' else 'good'
    return {
        'median_ratio': md,
        'direction': direction,
        'same_direction_rounds': same_dir,
        'rounds': reps,
        'verdict': verdict,
    }


def judge_paired(ratios, items, reps, min_ratio):
    """三项判定：每一项都要过门槛且全部轮次同向；三项方向一致才给 good / bad。"""
    summaries = {n: summarize_paired(ratios[n], reps, min_ratio) for n in items}
    verdicts = [summaries[n]['verdict'] for n in items]
    verdict = ('bad' if all(v == 'bad' for v in verdicts)
               else 'good' if all(v == 'good' for v in verdicts)
               else 'undecidable')
    return summaries, verdict


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--worktree', default='/tmp/m15-149')
    ap.add_argument('--ref', default='/tmp/bisect-149')
    ap.add_argument('--src', default=os.path.join(B, 'src'))
    ap.add_argument('--items', default='mbrot,pi,dynamic')
    ap.add_argument('--reps', type=int, default=3)
    ap.add_argument('--min-ratio', type=float, default=DEFAULT_MIN_RATIO,
                    help='最小可声明量级；|中位-1| 低于它判不可判定（R7_BISECT_MIN_RATIO，缺省 0.10）')
    ap.add_argument('--linear', help='线性扫描该区间（如 6ed0da4..65d407d），逐项打印配对比值，不做二分')
    ap.add_argument('--log', default='/tmp/bisect149.log')
    ap.add_argument('--jsonl', default=os.path.join(B, 'results', 'bisect152.jsonl'))
    a = ap.parse_args()
    items = [n.strip() for n in a.items.split(',') if n.strip()]
    if not items:
        ap.error('--items 不能为空')
    if a.reps < 1:
        ap.error('--reps 至少为 1')
    if a.min_ratio <= 0:
        ap.error('--min-ratio 必须大于 0')
    log = open(a.log, 'a')
    jsonl = open(a.jsonl, 'a', encoding='utf-8')
    log.write('=== %s items=%s reps=%d min_ratio=%.3f ref=%s\n' % (
        time.strftime('%F %T'), items, a.reps, a.min_ratio, a.ref))

    def save(record):
        jsonl.write(json.dumps(record, ensure_ascii=False) + '\n')
        jsonl.flush()

    base = {
        'schema': 'bisect-paired-v1',
        'task': 'cleat-152',
        'time': time.strftime('%F %T'),
        'worktree': os.path.abspath(a.worktree),
        'ref_worktree': os.path.abspath(a.ref),
        'ref_sha': sha_of(a.ref),
        'items': items,
        'reps': a.reps,
        'min_ratio': a.min_ratio,
    }

    def step():
        sha = sha_of(a.worktree)
        record = dict(base, sha=sha, time=time.strftime('%F %T'))
        if not build(a.worktree, log):
            record.update(verdict='skip', reason='build', paired_ratios=None)
            save(record)
            return sha, 'skip', 'skip(build)'
        got = paired(os.path.abspath(a.worktree), os.path.abspath(a.ref), items, a.reps, a.src, log)
        if got is None:
            record.update(verdict='skip', reason='run', paired_ratios=None)
            save(record)
            return sha, 'skip', 'skip(基准跑不起来)'
        summaries, verdict = judge_paired(got['ratios'], items, a.reps, a.min_ratio)
        sc = score_of(got['ratios'], items)
        per = ' '.join(
            '%s=x%.3f %d/%d %s' % (
                n, summaries[n]['median_ratio'], summaries[n]['same_direction_rounds'],
                a.reps, summaries[n]['verdict'])
            for n in items)
        record.update(verdict=verdict, raw_seconds=got['raw'], paired_ratios=got['ratios'],
                      paired_summaries=summaries, geomean_median_ratio=sc)
        save(record)
        log.write('%s geomean=%.4f %s -> %s\n' % (sha, sc, per, verdict))
        log.flush()
        shown = '不可判定' if verdict == 'undecidable' else verdict
        return sha, verdict, '%s(%s) geomean=%.3f' % (shown, per, sc)

    if a.linear:
        shas = subprocess.run(['git', '-C', a.worktree, 'rev-list', '--reverse', a.linear],
                              capture_output=True, text=True).stdout.split()
        tip = shas[-1]
        print('# 线性扫描 %d 个提交，items=%s，最小可声明量级 %.3f（同轮配对比值 vs 参照 %s）' % (
            len(shas), items, a.min_ratio, a.ref), flush=True)
        for sha in shas:
            subprocess.run(['git', '-C', a.worktree, 'checkout', '-q', '--detach', sha])
            s, verdict, txt = step()
            print('%s %s' % (s[:8], txt), flush=True)
        subprocess.run(['git', '-C', a.worktree, 'checkout', '-q', '--detach', tip])
        return 0

    s, verdict, txt = step()
    print('%s %s' % (s[:8], txt), flush=True)
    return {'bad': 1, 'good': 0}.get(verdict, 125)


if __name__ == '__main__':
    sys.exit(main())
