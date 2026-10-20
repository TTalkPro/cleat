#!/usr/bin/env python3
# 差异归因：cleat 侧 GC 配置对照、JVM 侧 GC 时间与内联开关，以及 takl 的 JVM 复核。只计基准内部计时。
#
# ── 口径（cleat-149 补，与 run.py 的头注同）──────────────────────────────────
# 1. **双态项报区间不报 min-of-3**：JVM 的 takl 有快态 ~12 s / 慢态 34 ~ 37 s（同机同源码，取决于
#    热点方法的编译时机 / 去优化）。下面 D 段因此打印全部轮次 + min/median/max，并把与 cleat 的
#    倍数写成**区间**（cleat 最小 ÷ JVM 最大 ～ cleat 最小 ÷ JVM 最小）；max/min > 1.5 标 `双态?`。
#    几何平均要按某个态单独计算时在 notes 里注明（M15：takl 按 JVM 快态算 1.61x → 1.64x）。
# 2. **微探针结果必须参与数据依赖**（sink / 条件分支 + 实参随循环变量变化），否则 C2 / Graal 会
#    用 LICM + 标量替换把调用提出循环，**JVM 侧数字无效**（shapes3 的教训，designs/28 §1）。
#    A / B / C 段跑的是真实基准（内部有 hide 累加），不受影响；跑探针请用
#    bench/r7rs/src/r7rs/probe.cljc 的 timed 宏（环底对照 + ARTIFACT? 自检）。
# 3. 结论只在**同一台机器、同一个 JVM、同一时间窗口**内成立：先记 `java -version` 与 load，
#    GraalVM(JVMCI) 与 OpenJDK C2 的数字不可混用；本机有并发构建时只信交替对照。
import json, os, re, subprocess, sys, time
B = os.path.dirname(os.path.abspath(__file__))
SRC = os.path.join(B, 'src')
R = os.environ['R7RS_BENCH']                                  # ecraven/r7rs-benchmarks 仓库
CLEAT = os.environ.get('CLEAT', os.path.dirname(os.path.dirname(B)))
ITEMS = ['array1','gcbench','peval','dynamic','browse','cpstak','puzzle','bv2string','scheme','deriv','fft','nboyer']
os.environ.setdefault('DOTNET_ROOT', os.path.expanduser('~/.dotnet'))
os.environ['PATH'] = os.environ['DOTNET_ROOT'] + ':' + os.environ['PATH']

def parse(out):
    m = re.search(r'\+!CSVLINE!\+[^,]*,[^,]*,([0-9.eE+-]+)\s*$', out, re.M)
    return float(m.group(1)) if m else None

def cleat(name, extra):
    env = dict(os.environ, **extra)
    cmd = ['dotnet', os.path.join(CLEAT, 'src/Cleat.Host/bin/Release/net10.0/cleat.dll'), '-I', SRC, '-m', 'r7rs.' + name]
    p = subprocess.run(cmd, stdin=open(os.path.join(R, 'inputs', name + '.input')), capture_output=True, text=True, cwd=R, timeout=900, env=env, errors='replace')
    return parse(p.stdout + p.stderr)

CP = subprocess.run(['clojure', '-Spath'], cwd=B, capture_output=True, text=True, check=True).stdout.strip()
CP = ':'.join(os.path.join(B, p) if not os.path.isabs(p) else p for p in CP.split(':'))

def jvm(name, jargs):
    cmd = ['java'] + jargs + ['-cp', CP, 'clojure.main', '-m', 'r7rs.' + name]
    p = subprocess.run(cmd, stdin=open(os.path.join(R, 'inputs', name + '.input')), capture_output=True, text=True, cwd=R, timeout=900, errors='replace')
    out = p.stdout + p.stderr
    secs = parse(out)
    pauses = [float(x) for x in re.findall(r'Pause \w+ [^\n]*?([0-9.]+)ms', out)]
    n = len(set(re.findall(r'GC\((\d+)\)', out)))
    return secs, sum(pauses), n

print('== A. cleat GC 配置对照（1 次，秒）', flush=True)
print('%-10s %8s %8s' % ('item', 'ws', 'server+gen0'), flush=True)
for it in ITEMS:
    a = cleat(it, {})
    b = cleat(it, {'DOTNET_gcServer': '1', 'DOTNET_GCgen0size': '0x10000000'})
    print('%-10s %8s %8s' % (it, a, b), flush=True)

print('== B. JVM C2 GC 时间与次数（1 次，-Xss8m -Xlog:gc）', flush=True)
print('%-10s %8s %8s %6s' % ('item', 'secs', 'gc_ms', 'gcs'), flush=True)
for it in ITEMS:
    s, gcm, n = jvm(it, ['-Xss8m', '-Xlog:gc:stdout'])
    print('%-10s %8s %8.1f %6d' % (it, s, gcm, n), flush=True)

print('== C. JVM 关内联（-XX:-Inline）对照默认', flush=True)
print('%-10s %8s %8s' % ('item', 'inline', 'noinline'), flush=True)
for it in ['dynamic', 'browse', 'peval', 'array1', 'scheme', 'takl']:
    s0, _, _ = jvm(it, ['-Xss8m'])
    s1, _, _ = jvm(it, ['-XX:+UnlockDiagnosticVMOptions', '-XX:-Inline', '-Xss8m'])
    print('%-10s %8s %8s' % (it, s0, s1), flush=True)

print('== D. takl 复核（JVM 5 次 / cleat 3 次：双态项看区间与全部轮次）', flush=True)
for it in ['takl']:
    jr = [jvm(it, ['-Xss8m'])[0] for _ in range(5)]
    cr = [cleat(it, {}) for _ in range(3)]
    def rng(tag, rs):
        rs = [x for x in rs if x]
        lo, md, hi = min(rs), sorted(rs)[len(rs) // 2], max(rs)
        dual = ' 双态?' if hi / lo > 1.5 else ''
        print('%-6s rounds=%s min=%.2f median=%.2f max=%.2f%s' % (tag, ['%.2f' % x for x in rs], lo, md, hi, dual), flush=True)
        return lo, hi
    jlo, jhi = rng('jvm', jr)
    clo, chi = rng('cleat', cr)
    # 倍数写成区间：分母取 JVM 的慢 / 快两端，别把双态项的最小值当结论
    print('takl cleat/JVM 倍数区间：%.2fx (JVM 最慢轮) ~ %.2fx (JVM 最快轮)' % (clo / jhi, clo / jlo), flush=True)
print('DONE', flush=True)
