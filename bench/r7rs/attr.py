#!/usr/bin/env python3
# 差异归因：cleat 侧 GC 配置对照、JVM 侧 GC 时间与内联开关，以及 takl 的 JVM 复核。只计基准内部计时。
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

print('== D. takl 复核（JVM 与 cleat 各 3 次）', flush=True)
for it in ['takl']:
    print('jvm ', [jvm(it, ['-Xss8m'])[0] for _ in range(3)], flush=True)
    print('cleat', [cleat(it, {}) for _ in range(3)], flush=True)
print('DONE', flush=True)
