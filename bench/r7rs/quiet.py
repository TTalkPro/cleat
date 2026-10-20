#!/usr/bin/env python3
# -*- coding: utf-8 -*-
# 跑分前的"安静窗口"门槛（cleat-149 定的口径；cleat-150 起 triage149.py 与 m16full.py 共用这一份）。
# 理由：同机并发只会把我们的时间变长、不会变短，所以宁可等窗口，也不要拿被污染的轮次去算最小值。
# 门槛的唯一出处是这里：R7_MAXLOAD（缺省 核数 x 25%，本机 8 线程即 2.0）、R7_LOAD_CAP（最长等待秒数，缺省 1800）。
# 等不到窗口不中止跑分，只把 loaded 记进 jsonl，由汇总脚本（summarize149.py / m16sum.py）默认剔除这些轮。
import os
import sys
import time

MAXLOAD = float(os.environ.get('R7_MAXLOAD', str(0.25 * (os.cpu_count() or 4))))
LOAD_CAP = int(os.environ.get('R7_LOAD_CAP', '1800'))


def load1():
    return os.getloadavg()[0]


def wait_quiet(maxload=MAXLOAD, cap=LOAD_CAP, log=sys.stdout):
    """等到 1 分钟平均负载 <= maxload（或等满 cap 秒），返回 (跑前 load1, 是否仍然超门槛)。"""
    waited = 0
    while load1() > maxload and waited < cap:
        time.sleep(5); waited += 5
    l = load1()
    if waited or l > maxload:
        print('# load=%.1f waited=%ds limit=%.1f%s' % (
            l, waited, maxload, ' 仍然忙（本轮标记 loaded）' if l > maxload else ''), file=log, flush=True)
    return l, l > maxload
