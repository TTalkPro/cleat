#!/usr/bin/env python3
# 汇总 results/<impl>.jsonl（cleat-107 的 run.py 产物）：每项报 min / median / max 与**全部轮次**，打印 cleat / JVM 倍数与几何平均。
# 用法：compare.py [--md] [--cleat FILE] [--jvm FILE] [--dual-ratio R] [--override=[侧:]名=秒]…
#   默认读 results/cleat.jsonl 与 results/clojure.jsonl；FILE 可给绝对路径（例如收尾时换一轮新数据）。
#
# ── 口径（cleat-149）────────────────────────────────────────────────────────
# 1. **双态项不取 min-of-N**：某一侧同一程序存在两种稳定状态（JVM 的 takl：快态 ~12 s / 慢态 34 ~ 37 s，
#    取决于热点方法的编译时机 / 去优化）时，最小值落在哪个态是随机的，比值就没有意义。
#    这类项按 max/min > --dual-ratio（默认 1.5）自动标 `双态?`，报**区间** [cleat_min / jvm_max, cleat_max / jvm_min]。
# 2. 几何平均给出三档：min/min（历史口径，可与旧表直接比）、median/median、以及双态项取另一态的上下界。
#    单项双态翻转对 N 项几何平均的影响是 (r_hi / r_lo)^(1/N)：takl 0.50 → 1.43、N=56 时是 1.019
#    （M15 记录的 1.61x → 1.64x 就是这个数）。所以双态项必须写明取了哪个态，否则整表差 2% 说不清来源。
# 3. --override 名=秒 把某项钉成显式值（按历史口径选态时用；备注里会标 OVERRIDE）。
# 4. 探针（micro / shapes2 / shapes3）**不进这张表**：它们是 ns/次的单操作对照，跨宿主倍数只在
#    结果参与数据依赖时成立（见 run.py 头注与 src/r7rs/probe.cljc）。收尾用 probes.py 单独出表。
import json, math, os, statistics, sys

B = os.path.dirname(os.path.abspath(__file__))


def arg(flag, default=None):
    """'--cleat FILE' / '--cleat=FILE' 取参数"""
    a = sys.argv
    for i, v in enumerate(a):
        if v == flag and i + 1 < len(a):
            return a[i + 1]
        if v.startswith(flag + '='):
            return v.split('=', 1)[1]
    return default


def load(path):
    """jsonl -> ({名: [各轮秒数]}, {名: 错误}, {名: JVM 版本行})"""
    vals, errs, jvm = {}, {}, {}
    if not os.path.exists(path):
        return vals, errs, jvm
    for line in open(path):
        line = line.strip()
        if not line:
            continue
        r = json.loads(line)
        if r['secs'] is None:
            errs[r['name']] = (r['error'] or '')[:80].replace('\n', ' ')
        else:
            vals.setdefault(r['name'], []).append(r['secs'])
            if r.get('jvm'):
                jvm[r['name']] = r['jvm']
    return vals, errs, jvm


def state(v, dual_ratio):
    """一轮以上的数据 -> (min, median, max, 是否双态)"""
    mn, md, mx = min(v), statistics.median(v), max(v)
    return mn, md, mx, mx / mn > dual_ratio


def main():
    dual_ratio = float(arg('--dual-ratio', '1.5'))
    cpath = arg('--cleat', os.path.join(B, 'results', 'cleat.jsonl'))
    jpath = arg('--jvm', os.path.join(B, 'results', 'clojure.jsonl'))
    c, ce, cjvm = load(cpath)
    j, je, jver = load(jpath)
    overrides = {}
    # --override=[侧:]名=秒（可重复）：侧 = cleat（缺省）或 jvm。双态项按历史口径选态时用，例如
    # --override=jvm:takl=12.05 把 JVM 的 takl 钉成快态，用来复现 M15 的 1.61x → 1.64x。
    for a in sys.argv[1:]:
        if a.startswith('--override='):
            k, _, v = a[len('--override='):].partition('=')
            side = 'c'
            if k.startswith('cleat:'):
                k, side = k[6:], 'c'
            elif k.startswith('jvm:'):
                k, side = k[4:], 'j'
            overrides.setdefault(side, {})[k] = float(v)
    md = '--md' in sys.argv
    names = sorted(set(c) | set(j) | set(ce) | set(je))
    if not names:
        raise SystemExit('没有数据：%s / %s（先跑 run.py cleat / run.py clojure）' % (cpath, jpath))

    rows = []
    for n in names:
        cv, jv = list(c.get(n, [])), list(j.get(n, []))
        note = []
        if n in overrides.get('c', {}):
            cv = [overrides['c'][n]]; note.append('OVERRIDE cleat=%g' % overrides['c'][n])
        if n in overrides.get('j', {}):
            jv = [overrides['j'][n]]; note.append('OVERRIDE jvm=%g' % overrides['j'][n])
        cs = state(cv, dual_ratio) if cv else None
        js = state(jv, dual_ratio) if jv else None
        r_min = cs[0] / js[0] if cs and js else None
        r_med = cs[1] / js[1] if cs and js else None
        r_lo = cs[0] / js[2] if cs and js else None          # cleat 最小 / JVM 最大：cleat 最好看的一档
        r_hi = cs[2] / js[0] if cs and js else None          # 最不利的一档
        dual = bool(cs and cs[3]) or bool(js and js[3])
        err = ('cleat: ' + ce[n] if n in ce else '') + (' JVM: ' + je[n] if n in je else '')
        rows.append(dict(name=n, c=cs, j=js, rmin=r_min, rmed=r_med, rlo=r_lo, rhi=r_hi,
                         dual=dual, note=' '.join(note), err=err, rounds=len(cv) + len(jv)))

    f3 = lambda x: '' if x is None else '%.3f' % x
    rng = lambda s: '' if s is None else ('%.3f/%.3f/%.3f' % s[:3] + (' 双态?' if s[3] else ''))
    if md:
        print('| 基准 | cleat min/med/max (s) | JVM min/med/max (s) | cleat/JVM (min) | 双态区间 | 备注 |')
        print('|---|---|---|---:|---|---|')
    else:
        print('%-12s %-30s %-30s %13s %-22s %s' % ('基准', 'cleat min/med/max', 'JVM min/med/max', 'ratio(min)', '双态区间', '备注 / 错误'))
    for r in rows:
        band = '' if not r['rmin'] else ('%.3f ~ %.3f' % (r['rlo'], r['rhi'])) if r['dual'] else ''
        tail = ' '.join(x for x in [r['note'], r['err']] if x)
        if md:
            print('| %s | %s | %s | %s | %s %s |' % (r['name'], rng(r['c']), rng(r['j']), f3(r['rmin']),
                                                      band + (' 双态?' if r['dual'] else ''), tail))
        else:
            print('%-12s %-30s %-30s %13s %-22s %s' % (r['name'], rng(r['c']), rng(r['j']), f3(r['rmin']),
                                                        band + (' 双态?' if r['dual'] else ''), tail))

    ok = [r for r in rows if r['rmin']]
    if ok:
        N = len(ok)
        g = lambda key: math.exp(sum(math.log(r[key]) for r in ok) / N)
        g_min, g_med = g('rmin'), g('rmed')
        g_lo = math.exp(sum(math.log(r['rlo']) for r in ok) / N)
        g_hi = math.exp(sum(math.log(r['rhi']) for r in ok) / N)
        duals = [r for r in ok if r['dual']]
        print('\n项数 %d；JVM 版本：%s' % (N, ' / '.join(sorted(set(jver.values()))) or '(未记录，--cleat/--jvm 指向同一轮数据)'), )
        print('几何平均 cleat/JVM：min/min **%.3f**  median/median **%.3f**  双态上下界 **%.3f ~ %.3f**' % (g_min, g_med, g_lo, g_hi))
        if duals:
            print('双态项（max/min > %.1f）：报区间，不要只取最小参与上面的几何平均' % dual_ratio)
            for r in duals:
                imp = (r['rhi'] / r['rlo']) ** (1.0 / N)
                print('  %-12s ratio %.3f ~ %.3f（cleat %.3f ~ %.3f / JVM %.3f ~ %.3f，轮次 %d+%d）'
                      '  仅这一项换态 → 整体几何平均 ×%.3f（%+.1f%%）' % (
                          r['name'], r['rlo'], r['rhi'], r['c'][0] if r['c'] else 0,
                          r['c'][2] if r['c'] else 0, r['j'][0] if r['j'] else 0, r['j'][2] if r['j'] else 0,
                          len(c.get(r['name'], [])), len(j.get(r['name'], [])), imp, (imp - 1) * 100))
        else:
            print('无双态项（全部 max/min ≤ %.1f）' % dual_ratio)


if __name__ == '__main__':
    main()
