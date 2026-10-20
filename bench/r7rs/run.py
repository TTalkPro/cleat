#!/usr/bin/env python3
# r7rs-benchmarks 的 Clojure 移植：在同一台机器上比较 cleat 与 JVM Clojure（cleat-107）。只计程序内部计时（+!CSVLINE!+），不含启动与编译。
# 用法：R7RS_BENCH=<ecraven/r7rs-benchmarks 仓库> CLEAT=<cleat 仓库> run.py <cleat|clojure> [-r 次数] [基准名 …]   不给基准名则跑 src/r7rs 下全部
# JVM：bench/r7rs/deps.edn（Clojure 1.12.4），java -cp $(clojure -Spath) clojure.main -m r7rs.<名字>，-Xss8m（与 .NET 主线程的栈相同），其余为默认 JVM 选项。
# JAVA=<java 可执行文件>（缺省 PATH 里的 java）；JVM 的结果记下 `java -version` 的版本行（GraalVM 与 HotSpot C2 的数字可能差很多，表头必须注明）。
# R7_OUT=<名字> 把结果写到 results/<名字>.jsonl（缺省 <impl>.jsonl），便于并排保存两种 JVM。
# 工作目录为 r7rs-benchmarks 仓库（读文件的基准按相对路径读 inputs/），标准输入是 inputs/<名字>.input。结果追加到 results/<impl>.jsonl。
#
# ── 口径（cleat-149 补，两条都是踩过的坑）────────────────────────────────────
# 1. **双态项不能取 min-of-3**：JVM 的 takl 有快态 ~12 s / 慢态 34 ~ 37 s 两种状态（同机同源码，
#    取决于热点方法的编译时机 / 去优化），最小值落在哪个态是随机的。这类项要报告**区间**
#    （min ~ max，连同全部轮次），并在几何平均里注明取了哪个态；compare.py 按 max/min > 1.5 自动标 `双态?`。
#    开关矩阵 / A/B 用 bench/r7rs/triage149.py（同窗口交替 + 每格 min/median + 双态标记）。
# 2. **微探针的结果必须参与数据依赖**（写进 sink / 条件分支，实参随循环变量变化），否则 C2 / Graal
#    会用 LICM + 标量替换把整个调用提出循环，**JVM 侧的数字只是循环开销，不是操作的成本**；
#    cleat 侧不做这种消除，于是死值探针上的跨宿主倍数全是伪影（shapes3 的教训，designs/28 §1）。
#    探针一律用 bench/r7rs/src/r7rs/probe.cljc 的 timed 宏（含环底对照与 ARTIFACT? 自检），
#    micro.cljc / shapes2.cljc / shapes3.cljc 的 JVM 侧数字以 net 列为准，标 ARTIFACT? / HOISTED? 的行不可用。
# 3. 跑前看 load（本机若有并发构建，只信同窗口交替对照）；里程碑收尾把 micro / shapes2 / shapes3
#    三个探针与全量 56 项一起重跑（designs/28 §5.2）。
import json, os, re, subprocess, sys, time
B = os.path.dirname(os.path.abspath(__file__))
SRC = os.environ.get('CLEAT_R7RS_SRC', os.path.join(B, 'src'))
R = os.environ['R7RS_BENCH']
CLEAT = os.environ.get('CLEAT', os.path.dirname(os.path.dirname(B)))
JAVA = os.environ.get('JAVA', 'java')

_jvm = None
def jvm_version():
    """`java -version` 的第二行（运行时名与版本，如 "OpenJDK Runtime Environment GraalVM CE 25.0.2+10.1"）"""
    global _jvm
    if _jvm is None:
        lines = subprocess.run([JAVA, '-version'], capture_output=True, text=True).stderr.strip().splitlines()
        _jvm = lines[1].strip() if len(lines) > 1 else (lines[0] if lines else '?')
    return _jvm

def names():
    return sorted(f[:-5] for f in os.listdir(os.path.join(SRC, 'r7rs')) if f.endswith('.cljc') and f not in ('common.cljc', 'util.cljc'))   # 共用的辅助命名空间不是基准

_cp = None
def classpath():
    global _cp
    if _cp is None:
        _cp = subprocess.run(['clojure', '-Spath'], cwd=B, capture_output=True, text=True, check=True).stdout.strip()
        _cp = ':'.join(os.path.join(B, p) if not os.path.isabs(p) else p for p in _cp.split(':'))
    return _cp

def command(impl, name):
    ns = 'r7rs.' + name
    if impl == 'cleat':
        return ['dotnet', os.path.join(CLEAT, 'src/Cleat.Host/bin/Release/net10.0/cleat.dll'), '-I', SRC, '-m', ns]
    return [JAVA, '-Xss8m', '-cp', classpath(), 'clojure.main', '-m', ns]   # 栈与 .NET 主线程（Linux 上 8 MB 的 ulimit）相同：equal 的 5000 层非尾递归

def run(impl, name, timeout=900):
    cmd = command(impl, name)
    t0 = time.time()
    try:
        p = subprocess.run(cmd, stdin=open(os.path.join(R, 'inputs', name + '.input')), capture_output=True, text=True,
                           cwd=R, timeout=timeout, errors='replace')
        out = p.stdout + p.stderr
    except subprocess.TimeoutExpired:
        return {'impl': impl, 'name': name, 'secs': None, 'wall': time.time() - t0, 'error': 'TIMEOUT'}
    m = re.search(r'\+!CSVLINE!\+[^,]*,[^,]*,([0-9.eE+-]+)\s*$', out, re.M)
    if m:
        return {'impl': impl, 'name': name, 'secs': float(m.group(1)), 'wall': time.time() - t0, 'error': None}
    return {'impl': impl, 'name': name, 'secs': None, 'wall': time.time() - t0, 'error': out.strip()[-600:]}

if __name__ == '__main__':
    impl, args, reps = sys.argv[1], sys.argv[2:], 1
    if args[:1] == ['-r']:
        reps, args = int(args[1]), args[2:]
    os.makedirs(os.path.join(B, 'results'), exist_ok=True)
    with open(os.path.join(B, 'results', os.environ.get('R7_OUT', impl) + '.jsonl'), 'a') as f:
        for name in args or names():
            for _ in range(reps):
                r = run(impl, name)
                if impl != 'cleat':
                    r['jvm'] = jvm_version()
                print(json.dumps(r), flush=True)
                f.write(json.dumps(r) + '\n')
                f.flush()
