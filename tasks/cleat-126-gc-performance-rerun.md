# cleat-126 M12 收尾：GC、分配与性能全量重测

- 里程碑：M12　依赖：cleat-125　状态：未开始
- 设计依据：[22 §7](../designs/22-gc-and-performance-followups.md)、[GC 配置实测](../notes/gc-config-and-tier0-two-machines.md)

## 工作内容
1. 双机重测（linux01 已不可用，2026-10-16 起）：本地 8 线程 i9-13900H 与 david@192.168.3.111（4 vCPU / 15GB），r7rs 56 项两边各 3 次取最小，`compare.py` 汇总；表头注明机器。
2. 官方对比表用默认配置（GC 旋钮不进默认值）；`bench/r7rs/gc_bench.py` 复跑 GC 配置敏感性矩阵（workstation / server ± gen0 256MB），确认 designs/22 §0.2 的两机结论仍成立。
3. 分配采样（dotnet-counters / perf）：array1、gcbench、nboyer、browse、cpstak、peval、mbrotZ、string 的 alloc rate 与各代次数。
4. 更新 designs/21 §1 的倍数与剩余慢区、designs/22 §6 的"不做 / 延后"清单、notes/ 的表。

## 验收标准
- 几何平均与各项数字写进任务书，双机数字并排。
- 基线、门禁、全量不变；改了 src/clojure 的种子不动点。
