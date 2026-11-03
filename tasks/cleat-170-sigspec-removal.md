# cleat-170 运行时 sigspec 体系删除

- 里程碑：M19　依赖：cleat-163　状态：未开始
- 设计依据：[29 §5.1](../designs/29-usability-and-engineering.md)、[24](../designs/24-runtime-signature-specialization.md) ~ [27](../designs/27-jit-style-runtime-specialization.md)

## 问题

[cleat-163](cleat-163-sigspec-ablation.md) 三组 56 × 5 同轮配对都没有可声明的整体收益，
三组几何平均 x1.003 / x0.999 / x0.997，可声明方向只有两个约 5% 的默认成本格。按 29 §5.1
裁决为 (c)：整体删除运行时观察、签名表、后台重编与运行时反馈设施，把复杂度换回可维护性。

## 工作内容

1. 删除 `RuntimeSigSpec` / `RuntimeSigTable` / `RuntimeSigKey` / `TypeFeedback` 的 C# 实现与测试；
   移除 codegen / driver 的安装、过滤、SeedCompiled、RtSpec 入口与 `CLEAT_RTSPEC_*` 读取点。
2. 逐项清点 `CLEAT_NO_OPT=typed` 相关入口，区分 cleat-104 的编译期类型化入口与 24 ~ 27 的
   运行时生成 / 守卫路径：只删后者；若两者纠缠导致必须动编译期 typed 行为，先列出语义 / 种子差异并裁决。
3. 同步 designs/24 ~ 27 为历史 / 被 170 取代的状态，designs/README 的环境变量与 `CLEAT_NO_OPT`
   记号表删除对应项；README 已知限制留给 169 汇总。
4. 重走 `clojure-seed`：stage2 == stage3，from-seed 产物逐字节一致。
5. 全套门禁：Release 0 warning、Core / 全量 Host、conformance `--gate`、CTS / test_clojure 基线、
   selfhost 不动点；镜像 / 编译器 ilverify 按 162 现行门禁。
6. 默认 vs 删除后构建的 r7rs 56 项 × 5 轮同轮配对（本机，111 在线时复核）；用 `m16sum.py` +
   `R7_FLOOR` 判定。允许中性或可声明改善，不允许可声明回退；几何平均与逐项异常写进本任务书。
7. `slatex` / 短启动各做一次 RSS、线程与 wall 冒烟，确认后台线程与相关常驻面消失。

## 验收

- 产品代码与种子中不再有运行时 sigspec / TypeFeedback 入口；诊断开关表与实现一致。
- typed 的保留 / 删除边界有明确记账，不把编译期类型化入口静默删掉。
- 门禁全绿；种子不动点通过；56 × 5 配对表进任务书，无超地板回退。
