#!/usr/bin/env python3
# cleat-159 启动画像（designs/29 §3.1 第 2 条）：把 dotnet-trace 的 CPU 采样按桶归因。
#
# 采集（三场景各一次，speedscope 便于脚本读）：
#   dotnet-trace collect --profile cpu-sampling --format speedscope -o /tmp/tr-nil -- \
#       dotnet src/Cleat.Host/bin/Release/net10.0/cleat.dll -e nil
# 分析：
#   python3 bench/startup/tracebuckets.py /tmp/tr-nil.speedscope.json
#
# speedscope 是事件流（O / C）。dotnet-trace 把每个样本写成一对 CPU_TIME / UNMANAGED_CODE_TIME
# 事件（时长 = 到下一个样本的间隔）挂在叶子帧下面，所以先丢掉这些伪帧，样本时长才会折回真正在执行
# 的托管帧；再逐帧算 self（区间长度 − 直接子帧长度之和）。丢掉伪帧后仍未归因的时间落在
# "进程 / 线程"根帧上，那是原生代码与等待（后台 JIT 线程自身编译、kernel），记作 clr/native。
#
# 桶的划分先看程序集（帧名的 `模块!方法` 前缀），再看命名空间：
#   host / Cleat.Core / cleat.boot 镜像 / 编译器（Cleat.Compiler；cleat-182 之前是 cleat.compiler 包）—— 与 CLEAT_TIME 的分段一一对应；
#   框架侧再细分反射与类型系统、表达式编译、IO/解压、GC、线程。
# 采样按执行线程计时，后台 JIT 线程不被采样，所以这些桶是"启动在做什么"的下界；
# JIT 的总份额看对照组（DOTNET_TieredCompilation=0、DOTNET_ReadyToRun=0，见 profile.py）。
import collections
import json
import re
import sys

# 伪帧：丢掉，时长归给父帧
PSEUDO = re.compile(r'^(CPU_TIME|UNMANAGED_CODE_TIME|unknown\b|\?)$', re.IGNORECASE)
# 进程 / 线程根帧：单独一桶，且不列进"自身耗时 top"。
# Thread.<PollGC>g__PollGCWorker 是托管线程的入口包装帧（Program.cs 用 new Thread(…) 起大栈线程，
# 整条调用树的根就在它下面），它自己的时间 = 这段线程里"没有任何托管帧在跑"的时间
# （原生代码、后台 JIT 编译、GC、等待），所以和进程/线程根帧归一起。
ROOT = re.compile(r'^Process\d+ |^\(Non-Activities\)$|^Threads$|^Thread \(', re.IGNORECASE)
THREAD_ROOT = re.compile(r'^System\.Threading\.Thread\.(<PollGC>|Join)', re.IGNORECASE)

# (桶名, 匹配函数)。顺序即优先级。帧名形如 "cleat.boot!clojure.core$load.doInvoke(…)"。
def mk(module_rx=None, name_rx=None):
    # 模块名的大小写随 dotnet-trace 版本 / 程序集文件名而变（system.private.corelib!…），归桶一律忽略大小写
    m = re.compile(module_rx, re.IGNORECASE) if module_rx else None
    n = re.compile(name_rx, re.IGNORECASE) if name_rx else None
    return lambda mod, name: (not m or m.search(mod)) and (not n or n.search(name))


RULES = [
    ('clr/native',        mk(None, ROOT.pattern)),
    ('native/wait',       mk(r'^System\.Private\.CoreLib$', THREAD_ROOT.pattern)),
    ('native/jit',        mk(r'^$', r'^(unknown|!|\?)')),       # 未符号化的帧（含 ?!?）
    ('host',              mk(r'^cleat$|^Cleat\.Host$')),
    ('Cleat.Core',        mk(r'^Cleat\.Core$')),
    ('镜像 cleat.boot',   mk(r'^cleat\.boot$')),
    ('编译器',            mk(r'^(Cleat\.Compiler|cleat\.compiler)$')),
    ('其他程序集',        mk(r'^(?!System\.|Microsoft\.|clrjit)[^!]+$')),
    ('gc',                mk(r'^System\.Private\.CoreLib$', r'PollGC|\bGC\b|GarbageCollect|Finalizer|GCHandle')),
    ('expression-compile', mk(r'^System\.', r'Linq\.Expressions|System\.Dynamic|Emit\.|DynamicMethod|lambda_method|dynamicClass')),
    ('reflection/type',   mk(r'^System\.', r'Reflection|RuntimeType|MethodTable|TypeNameParser|MemberInfoCache|StaticsHelpers|InitHelpers')),
    ('io/decompress',     mk(r'^System\.', r'^System\.IO\.|Compression|Microsoft\.Win32')),
    # 等标准输入：-r 场景里读控制台阻塞的时间不是启动开销，单列一桶，读画像时扣掉
    ('console-wait',      mk(r'^System\.', r'^System\.Console')),
    ('threading/locks',   mk(r'^System\.Private\.CoreLib$', r'^System\.Threading\.')),
    ('framework-other',   mk(r'^(System\.|Microsoft\.)')),
]
def split_frame(name):
    """'cleat.boot!clojure.core$load.doInvoke(…)' → ('cleat.boot', 'clojure.core$load.doInvoke(…)')。
    只有前缀确实是程序集名（含点或已知名）时才剥，避免把 '(…)!x' 之类切错。"""
    mod, sep, rest = name.partition('!')
    if sep and rest and (('.' in mod) or mod in ('cleat', 'System.Private.CoreLib')):
        return mod, rest
    return '', name


def bucket(name):
    mod, meth = split_frame(name)
    for label, pred in RULES:
        if pred(mod, meth):
            return label
    return 'other'


def self_ms(profile, frame_names):
    """逐帧 self 毫秒：闭合帧时自身时间 = 区间长度 − 已闭合子帧长度之和，整段区间再记给父帧。
    丢 close 时一路弹到匹配帧，误差只落在少数帧上。"""
    stack = []                                  # [帧号, 起始, 已闭合子帧时间]
    out = collections.Counter()
    pseudo_at = [i for i, n in enumerate(frame_names) if PSEUDO.match(n)]
    skip = set(pseudo_at)
    for ev in profile['events']:
        if ev['frame'] in skip:
            continue
        if ev['type'] in ('O', 'open'):
            stack.append([ev['frame'], ev['at'], 0.0])
        elif stack:
            while stack:
                fr, t0, kids = stack.pop()
                span = ev['at'] - t0
                out[fr] += max(0.0, span - kids)
                if stack:
                    stack[-1][2] += span
                if fr == ev['frame']:
                    break
    return out


def report(path, top_n=14):
    d = json.load(open(path))
    names = [f.get('name', '?') for f in d['shared']['frames']]
    print(f'== {path}')
    threads = []
    for prof in d['profiles']:
        st = self_ms(prof, names)
        by_bucket = collections.Counter()
        by_frame = collections.defaultdict(list)
        for ix, ms in st.items():
            b = bucket(names[ix])
            by_bucket[b] += ms
            by_frame[b].append((ms, names[ix]))
        # 陪跑线程（只会 Thread.Join / 等信号）的托管时间接近 0，用"非线程等待、非根帧"的时间排序
        busy = sum(ms for b, ms in by_bucket.items()
                   if b not in ('clr/native', 'threading/locks', 'native/wait', 'console-wait'))
        threads.append((busy, prof, by_bucket, by_frame))
    threads.sort(key=lambda t: -t[0])
    for rank, (busy, prof, by_bucket, by_frame) in enumerate(threads):
        span = prof['endValue'] - prof['startValue']
        tag = '  ← 工作线程' if rank == 0 else ''
        print(f'-- 线程 {prof["name"]}：跨度 {span:7.0f} ms，非等待的托管时间 {busy:7.1f} ms{tag}')
        for b, ms in by_bucket.most_common():
            print(f'   {b:20s} {ms:8.1f} ms')
        print(f'   自身耗时 top {top_n}：')
        frames = sorted((ms, n) for b, lst in by_frame.items() if b != 'clr/native' for ms, n in lst)
        for ms, name in reversed(frames[-top_n:]):
            print(f'     {ms:6.1f} ms  {name[:118]}')
    busy, prof, by_bucket, by_frame = threads[0]
    skip = ('clr/native', 'native/wait', 'console-wait')
    total = sum(ms for b, ms in by_bucket.items() if b not in skip) or 1.0
    print(f'-- 工作线程按桶占比（{prof["name"]}，不含原生/等待/控制台输入，合计 {total:.0f} ms）')
    for b, ms in by_bucket.most_common():
        if b in skip:
            continue
        print(f'   {b:20s} {ms:8.1f} ms  {100 * ms / total:5.1f}%')
    print('   （原生/等待一桶：' + '，'.join(f'{b} {by_bucket[b]:.1f} ms' for b in skip) + '）')
    if '-j' in sys.argv:
        rows = {b: round(ms, 1) for b, ms in by_bucket.items()}
        print('JSON ' + json.dumps({'file': path, 'buckets': rows}, ensure_ascii=False))


if __name__ == '__main__':
    for p in (sys.argv[1:] or ['/tmp/tr-nil.speedscope.json']):
        report(p)
