#!/usr/bin/env python3
# cleat-130：生成 r7rs 的无数组提示变体。只删除 cleat 专用的元素数组标注，
# 保留 ^long / ^double 等标量提示，避免把数组推测以外的 M11 收益混进对照。
# 用法：nohint.py OUTPUT_DIR [--force]；然后 CLEAT_R7RS_SRC=OUTPUT_DIR run.py cleat …
import argparse
import re
import shutil
from pathlib import Path

B = Path(__file__).resolve().parent
ARRAY_TAGS = ('objects', 'longs', 'doubles', 'bytes', 'chars',
              'shorts', 'ints', 'floats')
TAG_RE = re.compile(r'(?<![\\])\^(?:' + '|'.join(ARRAY_TAGS) + r')(?=\s)')
META_RE = re.compile(r" :tag '(?:" + '|'.join(ARRAY_TAGS) + r')\b')

if __name__ == '__main__':
    ap = argparse.ArgumentParser()
    ap.add_argument('output')
    ap.add_argument('--force', action='store_true')
    a = ap.parse_args()
    src = B / 'src'
    dst = Path(a.output)
    if not a.force and dst.exists() and any(dst.iterdir()):
        raise SystemExit(f'output exists and is not empty: {dst}')
    shutil.copytree(src, dst, dirs_exist_ok=True)
    count = 0
    for p in (dst / 'r7rs').glob('*.cljc'):
        s = p.read_text()
        s2 = TAG_RE.sub('', META_RE.sub('', s))
        if s2 != s:
            count += 1
            p.write_text(s2)
    print(f'nohint source: {dst}, files changed={count}')
