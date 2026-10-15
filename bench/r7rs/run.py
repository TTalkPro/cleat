#!/usr/bin/env python3
# r7rs-benchmarks 的 Clojure 移植：在同一台机器上比较 cleat 与 JVM Clojure（cleat-107）。只计程序内部计时（+!CSVLINE!+），不含启动与编译。
# 用法：R7RS_BENCH=<ecraven/r7rs-benchmarks 仓库> CLEAT=<cleat 仓库> run.py <cleat|clojure> [-r 次数] [基准名 …]   不给基准名则跑 src/r7rs 下全部
# JVM：bench/r7rs/deps.edn（Clojure 1.12.4），java -cp $(clojure -Spath) clojure.main -m r7rs.<名字>，-Xss8m（与 .NET 主线程的栈相同），其余为默认 JVM 选项。
# 工作目录为 r7rs-benchmarks 仓库（读文件的基准按相对路径读 inputs/），标准输入是 inputs/<名字>.input。结果追加到 results/<impl>.jsonl。
import json, os, re, subprocess, sys, time
B = os.path.dirname(os.path.abspath(__file__))
SRC = os.path.join(B, 'src')
R = os.environ['R7RS_BENCH']
CLEAT = os.environ.get('CLEAT', os.path.dirname(os.path.dirname(B)))

def names():
    return sorted(f[:-5] for f in os.listdir(os.path.join(SRC, 'r7rs')) if f.endswith('.cljc') and f != 'common.cljc')

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
    return ['java', '-Xss8m', '-cp', classpath(), 'clojure.main', '-m', ns]   # 栈与 .NET 主线程（Linux 上 8 MB 的 ulimit）相同：equal 的 5000 层非尾递归

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
    with open(os.path.join(B, 'results', impl + '.jsonl'), 'a') as f:
        for name in args or names():
            for _ in range(reps):
                r = run(impl, name)
                print(json.dumps(r), flush=True)
                f.write(json.dumps(r) + '\n')
                f.flush()
