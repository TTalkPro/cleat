#!/usr/bin/env python3
# cleat-120 同窗口 A/B：每个基准交替跑 loopinl on / off（同一二进制，CLEAT_NO_OPT=loopinl 切），
# 每项 min(on 组) vs min(off 组)。结果写 results/ab120.jsonl。
# 用法：AB_REPS=2 python3 ab120.py [基准名 …]
import json, os, subprocess, sys, time
B = os.path.dirname(os.path.abspath(__file__))
SRC = os.path.join(B, 'src')
R = os.environ['R7RS_BENCH']
CLEAT = os.environ.get('CLEAT', os.path.dirname(os.path.dirname(B)))
REPS = int(os.environ.get('AB_REPS', '2'))

def names():
    return sorted(f[:-5] for f in os.listdir(os.path.join(SRC, 'r7rs')) if f.endswith('.cljc') and f not in ('common.cljc', 'util.cljc'))

def run(name, off):
    env = dict(os.environ)
    if off: env['CLEAT_NO_OPT'] = os.environ.get('AB_SWITCH', 'loopinl')
    cmd = ['dotnet', os.path.join(CLEAT, 'src/Cleat.Host/bin/Release/net10.0/cleat.dll'), '-I', SRC, '-m', 'r7rs.' + name]
    try:
        p = subprocess.run(cmd, stdin=open(os.path.join(R, 'inputs', name + '.input')), capture_output=True, text=True,
                           cwd=R, timeout=900, errors='replace', env=env)
        out = p.stdout + p.stderr
    except subprocess.TimeoutExpired:
        return None
    import re
    m = re.search(r'\+!CSVLINE!\+[^,]*,[^,]*,([0-9.eE+-]+)\s*$', out, re.M)
    return float(m.group(1)) if m else None

if __name__ == '__main__':
    todo = sys.argv[1:] or names()
    out = open(os.path.join(B, 'results', os.environ.get('AB_OUT', 'ab120') + '.jsonl'), 'a')
    for idx, name in enumerate(todo):
        for rep in range(REPS):
            order = [('on', False), ('off', True)] if (idx + rep) % 2 == 0 else [('off', True), ('on', False)]
            for tag, off in order:
                secs = run(name, off)
                r = {'name': name, 'cfg': tag, 'secs': secs}
                print(json.dumps(r), flush=True)
                out.write(json.dumps(r) + '\n'); out.flush()
    out.close()
