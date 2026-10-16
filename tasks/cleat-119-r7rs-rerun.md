# cleat-119 M11 收尾：r7rs 56 项与 bench/ 全部重测，更新 cleat-107 的表与 designs/21

- 里程碑：M11　依赖：cleat-118　状态：未开始
- 设计依据：[21](../designs/21-r7rs-gaps.md)

## 工作内容
1. linux01 已不可用：在 david@192.168.3.111（4 vCPU / 15 GB，dotnet 在 ~/.dotnet，`ssh` 后 `export PATH=$HOME/.dotnet:$PATH`）上两边各 3 次，compare.py。
   JVM 两种都测并在表头注明：OpenJDK 25.0.4（HotSpot C2，该机默认 `java`）与 GraalVM CE（cleat-107 ~ 117 的"JVM"列是 GraalVM）。
   116 之前 linux01 上的数字标为"历史，不可直接比较"。
2. 更新 designs/21 §1 的倍数与剩余慢区。

## 验收标准
- 几何平均与各项数字写进任务书。
- 基线、门禁、全量不变；改了 src/clojure 的种子不动点。
