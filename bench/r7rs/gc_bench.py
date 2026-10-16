#!/usr/bin/env python3
# GC 对照：cleat 的 workstation / server GC（± gen0 256MB），JVM 的默认参数 / notes 参数(-Xss8m) / 小堆。RUN_JVM=0 只跑 cleat 侧。
# 只计基准内部计时（+!CSVLINE!+），每配置 3 次取最小；JVM 侧顺带用 -Xlog:gc 统计 GC 次数。
import json, os, re, subprocess, sys, time
B = os.path.dirname(os.path.abspath(__file__))
SRC = os.path.join(B, 'src')
R = os.environ['R7RS_BENCH']
CLEAT = os.environ.get('CLEAT')
NAMES = ['array1', 'gcbench', 'nboyer', 'deriv', 'cpstak']
REPS = 3

CP = subprocess.run(['clojure', '-Spath'], cwd=B, capture_output=True, text=True, check=True).stdout.strip()
CP = ':'.join(os.path.join(B, p) if not os.path.isabs(p) else p for p in CP.split(':'))

CLEAT_CONFIGS = [
    ('cleat-ws',              {},                 []),
    ('cleat-ws-gen0-256m',    {'DOTNET_GCgen0size': '0x10000000'}, []),
    ('cleat-server',          {'DOTNET_gcServer': '1'}, []),
    ('cleat-server-gen0-256m',{'DOTNET_gcServer': '1', 'DOTNET_GCgen0size': '0x10000000'}, []),
]
JVM_CONFIGS = [
    ('jvm-notes-xss8m',    ['-Xss8m'],                      True),
    ('jvm-default',        [],                              True),
    ('jvm-xss8m-xmx1g',    ['-Xss8m', '-Xmx1g'],            True),
    ('jvm-xss8m-xmx512m',  ['-Xss8m', '-Xmx512m'],          True),
]

def cleat_cmd(name, env):
    env = dict(os.environ, **env)
    cmd = ['dotnet', os.path.join(CLEAT, 'src/Cleat.Host/bin/Release/net10.0/cleat.dll'), '-I', SRC, '-m', 'r7rs.' + name]
    return cmd, env

def jvm_cmd(name, jvm_args, gclog):
    args = list(jvm_args)
    if gclog:
        args += ['-Xlog:gc:stdout']
    return (['java'] + args + ['-cp', CP, 'clojure.main', '-m', 'r7rs.' + name], dict(os.environ))

def run_one(name, cmd, env):
    t0 = time.time()
    try:
        p = subprocess.run(cmd, stdin=open(os.path.join(R, 'inputs', name + '.input')),
                           capture_output=True, text=True, cwd=R, timeout=900, env=env, errors='replace')
        out = p.stdout + p.stderr
    except subprocess.TimeoutExpired:
        return None, None, time.time() - t0
    m = re.search(r'\+!CSVLINE!\+[^,]*,[^,]*,([0-9.eE+-]+)\s*$', out, re.M)
    if not m:
        return None, None, time.time() - t0
    secs = float(m.group(1))
    gc = len(re.findall(r'\[gc\s*,', out)) or len(re.findall(r'GC\(\d+', out))
    return secs, (gc if gc else None), time.time() - t0

results = {}
for label, env, _ in CLEAT_CONFIGS:
    for name in NAMES:
        for _ in range(REPS):
            cmd, e = cleat_cmd(name, env)
            secs, _, wall = run_one(name, cmd, e)
            results.setdefault(label, {}).setdefault(name, []).append(secs)
            print(json.dumps({'config': label, 'name': name, 'secs': secs, 'wall': round(wall, 1)}), flush=True)
for label, jvm_args, _ in (JVM_CONFIGS if os.environ.get("RUN_JVM", "1") == "1" else []):
    for name in NAMES:
        for _ in range(REPS):
            cmd, e = jvm_cmd(name, jvm_args, True)
            secs, gcs, wall = run_one(name, cmd, e)
            results.setdefault(label, {}).setdefault(name, []).append(secs)
            print(json.dumps({'config': label, 'name': name, 'secs': secs, 'gc_events': gcs, 'wall': round(wall, 1)}), flush=True)

print('=== SUMMARY (min of %d) ===' % REPS, flush=True)
for label, per in results.items():
    row = {name: round(min(v), 3) for name, v in per.items() if any(x is not None for x in v)}
    print(label, json.dumps(row), flush=True)
