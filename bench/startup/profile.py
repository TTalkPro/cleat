#!/usr/bin/env python3
# cleat-159 启动画像（designs/29 §3.1）：三种场景 × 对照组环境变量，每个组合跑 N 次，报中位与极差。
# 场景：nil（-e nil）、pprint（-e '(require (quote clojure.pprint))'）、repl（-r 到首个提示符后退出）。
# 计时用宿主自己的 CLEAT_TIME 分段（total）与进程墙钟（wall）各记一份：total 不含 dotnet CLI 与 CLR 自身起来的时间。
# 分段的每格取"中位那次运行"的值没有意义（各段独立波动），所以逐段取中位。
#
# 用法：
#   python3 bench/startup/profile.py [-n 5] [-g 秒] [场景名…] [臂名…]
#   CLEAT=~/workspace/cleat 覆盖仓库路径。臂见 ARMS（DOTNET_* 环境变量是对照组，29 §3.1 第 3 条）。
#   结果：JSON 到 stdout（留档到 bench/startup/results/，不入库），汇总表到 stderr。
import json
import os
import re
import statistics
import subprocess
import sys
import time

REPO = os.environ.get('CLEAT', os.path.abspath(os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', '..')))
# 默认量仓库的宿主 DLL；CLEAT_HOST 可指向 apphost / 发布目录（例如 artifacts/r2r/cleat）。
# apphost 与 dotnet <dll> 的启动路径不同：R2R 的量化必须走 apphost，不能混用两种口径。
HOST = os.environ.get('CLEAT_HOST', os.path.join(REPO, 'src', 'Cleat.Host', 'bin', 'Release', 'net10.0', 'cleat.dll'))

SCENARIOS = {
    'nil': {'args': ['-e', 'nil'], 'stdin': None},
    'pprint': {'args': ['-e', "(require (quote clojure.pprint))"], 'stdin': None},
    'repl': {'args': ['-r'], 'stdin': ':repl/quit\n'},
    # prompt：只量"首个提示符出现"为止（29 §3.1 的第 2 条要的就是这个数），随后才喂 :repl/quit。
    # repl 那一行量的是整个进程，包含退出路径，两者相差几百毫秒（REPL 自己的初始化与编译器）。
    'prompt': {'args': ['-r'], 'stdin': 'until-prompt'},
}

# 对照组（29 §3.1 第 3 条）：default 是本来的分层编译 + 框架 R2R；其余逐个关掉/打开作反证
ARMS = {
    'default': {},
    'tier0-only': {'DOTNET_TieredCompilation': '0'},
    'quickjit': {'DOTNET_TC_QuickJitForLoops': '1'},
    'no-r2r': {'DOTNET_ReadyToRun': '0'},
}

SEG = re.compile(r'cleat: segments host (\d+) / cli (\d+) / paths (\d+) / runtime (\d+) / image (\d+) '
                 r'\(assembly (\d+) \+ register (\d+) \+ ifc (\d+) \+ entry (\d+)\) / packages (\d+) / '
                 r'compiler (\d+) \(require (\d+) \+ hooks (\d+)\) / other (\d+) = total (\d+) ms')
SEG_NAMES = ['host', 'cli', 'paths', 'runtime', 'image', 'assembly', 'register', 'ifc', 'entry',
             'packages', 'compiler', 'require', 'hooks', 'other', 'total']
# REPL 的第二行（cleat-159）：启动报告之后到首个提示符
REPL = re.compile(r'cleat: repl setup (\d+) ms \(user (\d+) \+ repl-requires (\d+) \+ prompt (\d+)\) '
                  r'= first prompt (\d+) ms after start')


def host_command():
    return [HOST] if not HOST.endswith('.dll') else ['dotnet', HOST]


def run_once(sc, env):
    if SCENARIOS[sc]['stdin'] == 'until-prompt':
        return run_until_prompt(env)
    e = dict(os.environ, CLEAT_TIME='1', **env)
    t0 = time.perf_counter()
    p = subprocess.run(host_command() + SCENARIOS[sc]['args'], input=SCENARIOS[sc]['stdin'],
                       capture_output=True, text=True, env=e, cwd=REPO)
    wall = (time.perf_counter() - t0) * 1000
    m = SEG.search(p.stderr)
    if not m:
        raise RuntimeError('no CLEAT_TIME segments:\n' + p.stderr[-2000:])
    return [wall] + [int(x) for x in m.groups()]


def run_until_prompt(env):
    """起 REPL，读到 user=> 为止记墙钟；然后喂 :repl/quit 收进程，再从 stderr 取分段。"""
    e = dict(os.environ, CLEAT_TIME='1', **env)
    t0 = time.perf_counter()
    p = subprocess.Popen(host_command() + ['-r'], stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                         stderr=subprocess.PIPE, text=True, env=e, cwd=REPO)
    buf = ''
    while 'user=>' not in buf:
        ch = p.stdout.read(1)
        if ch == '':
            break
        buf += ch
    prompt_ms = (time.perf_counter() - t0) * 1000
    p.stdin.write(':repl/quit\n')
    p.stdin.close()
    # 等进程退出拿完整 stderr（提示符之后的 repl setup 行也在里面）
    _, err = p.communicate(timeout=60)
    m = SEG.search(err)
    r = REPL.search(err)
    if not m:
        raise RuntimeError('no CLEAT_TIME segments:\n' + err[-2000:])
    # wall 这一列对 prompt 场景就是"到首个提示符"，退出时间不计；
    # 末列给 repl 行的"自启动起到首个提示符"（宿主自己量的，不含 dotnet CLI 起进程的时间）
    # 分段列之后附加 REPL 行的五列：repl setup、user、repl-requires、prompt、首个提示符（自启动起）
    return [prompt_ms] + [int(x) for x in m.groups()] + ([int(x) for x in r.groups()] if r else [0, 0, 0, 0, 0])


def med(xs):
    return statistics.median(xs), min(xs), max(xs)


def main():
    argv = sys.argv[1:]
    n = 5
    if '-n' in argv:
        i = argv.index('-n')
        n = int(argv[i + 1]); del argv[i:i + 2]
    # -g：两次运行之间静默的秒数。紧跑（-g 0）时页缓存与 CPU 频率都是热的；静默一段时间再跑
    # 更接近"用户按下回车"的那一刻。两种口径都记，notes 里分别叫热与冷。
    gap = 0.0
    if '-g' in argv:
        i = argv.index('-g')
        gap = float(argv[i + 1]); del argv[i:i + 2]
    scs = [a for a in argv if a in SCENARIOS] or list(SCENARIOS)
    arms = [a for a in argv if a in ARMS] or list(ARMS)
    rows = []
    for sc in scs:
        for arm in arms:
            runs = []
            for k in range(n):
                if gap > 0:
                    time.sleep(gap)
                runs.append(run_once(sc, ARMS[arm]))
            cols = list(zip(*runs))
            # cols[0] 是 wall，分段列从 cols[1] 起
            wall, total = med(cols[0]), med(cols[SEG_NAMES.index('total') + 1])
            segs = {name: med(col)[0] for name, col in zip(SEG_NAMES, cols[1:])}
            row = {'scenario': sc, 'arm': arm, 'n': n, 'gap': gap,
                   'first': {'wall': round(cols[0][0], 1),
                             'total': int(cols[SEG_NAMES.index('total') + 1][0])},
                   'wall_med': round(wall[0], 1), 'wall_min': round(wall[1], 1), 'wall_max': round(wall[2], 1),
                   'total_med': round(total[0], 1), 'total_min': round(total[1], 1), 'total_max': round(total[2], 1),
                   'wall_raw': [round(x, 1) for x in cols[0]],
                   'total_raw': [int(x) for x in cols[SEG_NAMES.index('total') + 1]],
                   'segments': {k: v for k, v in segs.items()}}
            if len(cols) > len(SEG_NAMES) + 1:        # prompt 场景多出的四列（见 run_until_prompt）
                extras = ['repl_setup', 'repl_user', 'repl_requires', 'repl_prompt', 'prompt_total']
                row['repl'] = {name: med(col)[0] for name, col in zip(extras, cols[len(SEG_NAMES) + 1:])}
            rows.append(row)
            print(f"{sc:7s} {arm:11s} wall {wall[0]:6.0f} ms（极差 {wall[2] - wall[1]:4.0f}）  "
                  f"total {total[0]:5.0f}  runtime {segs['runtime']:4.0f}  image {segs['image']:4.0f}"
                  f"（ifc {segs['ifc']:3.0f} + entry {segs['entry']:4.0f}）  packages {segs['packages']:3.0f}  "
                  f"compiler {segs['compiler']:4.0f}（require {segs['require']:4.0f} + hooks {segs['hooks']:3.0f}）"
                  + (f"  首个提示符 {row['repl']['prompt_total']:5.0f}"
                     f"（user {row['repl']['repl_user']:.0f} + requires {row['repl']['repl_requires']:.0f}"
                     f" + 提示符 {row['repl']['repl_prompt']:.0f}）" if 'repl' in row else ''),
                  file=sys.stderr)
    print(json.dumps(rows, ensure_ascii=False, indent=1))


if __name__ == '__main__':
    main()
