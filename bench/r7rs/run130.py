#!/usr/bin/env python3
# cleat-130 的同窗口三方 runner：每个基准 / 每轮交替跑 cleat、OpenJDK C2 与 GraalVM。
# 用法：R7RS_BENCH=… run130.py normal|nohint 轮数 [基准名 …]
# Java 路径可用 JAVA_C2 / JAVA_GRAAL 覆盖；结果追加到 results/m130.jsonl。
import json
import os
import sys

B = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, B)
import run

C2 = os.environ.get('JAVA_C2', os.path.expanduser('~/.local/share/mise/installs/java/25.0.2/bin/java'))
GRAAL = os.environ.get('JAVA_GRAAL', os.path.expanduser('~/.local/share/mise/installs/java/graalvm-community-25.0.2/bin/java'))
NOHINT = os.path.join(B, 'results', 'nohint-src')
JAVA = {'cleat': os.environ.get('JAVA', 'java'), 'c2': C2, 'graal': GRAAL}

def one(impl, variant, rep, name):
    run.SRC = NOHINT if variant == 'nohint' else os.path.join(B, 'src')
    run.JAVA = JAVA[impl]
    run._jvm = None
    r = run.run(impl, name)
    r.update(variant=variant, label=impl, rep=rep)
    print(json.dumps(r, ensure_ascii=False), flush=True)
    out = os.path.join(B, 'results', 'm130.jsonl')
    with open(out, 'a') as f:
        f.write(json.dumps(r, ensure_ascii=False) + '\n')

if __name__ == '__main__':
    if len(sys.argv) < 3 or sys.argv[1] not in ('normal', 'nohint'):
        raise SystemExit('usage: run130.py normal|nohint REPS [BENCH …]')
    variant, reps = sys.argv[1], int(sys.argv[2])
    names = sys.argv[3:] or run.names()
    for rep in range(1, reps + 1):
        for name in names:
            for impl in ('cleat', 'c2', 'graal'):
                one(impl, variant, rep, name)
