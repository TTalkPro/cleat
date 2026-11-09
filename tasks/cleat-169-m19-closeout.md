# cleat-169 M19 收口：文档、CHANGELOG、全量门禁与 r7rs 复核

- 里程碑：M19　依赖：cleat-158 ~ 168、cleat-170　状态：完成（2026-11-08）
- 设计依据：[29 §1 第 8 条 / §8 / §9](../designs/29-usability-and-engineering.md)

## 工作内容

1. README：刷新现状（里程碑、测试规模、命令行与 CI），补上 M10 ~ M19 摘要。
2. 新增 `CHANGELOG.md`：从 1.0.0 起按里程碑汇总用户可见的变化。
3. README 新增"已知限制"一节：行号 / 启动 / 缺失库 / 实验开关的现状，由 158 ~ 168、170 的结果填写。
4. 全量门禁（29 §9）与 r7rs 56 项三臂复核（本机 5 轮；111 在线时复核），确认 M19 整体没有可声明的性能回退。
   - 从 cleat-158 移交：序列点 / PDB 的默认成本没有可比的跨构建基线——92d831c 与 158 之后的镜像 IL 形状差 3.4×
     （`(stloc` 66,004 → 19,481，`(call` 77,102 → 59,380，方法与类型计数相同，`boot.inl` 反而变大），
     两棵树各自都是自洽不动点，机制未查明（[158 文末](cleat-158-source-locations-and-stack-frames.md)）。
     158 的 56 项 × 5 轮跨构建配对（跑到 172 / 560 格，0 个污染轮）作废；改用三臂复核覆盖 M19 的整体回退判定，
     顺带定位那个形状差是收益还是损失。runner `bench/r7rs/pairbuild.py` 已入库。
5. TASK.md、designs/README、designs/29 的状态同步。

## 验收

- 门禁全绿；r7rs 汇总表进 notes；文档与代码现状一致。

## 结果（2026-11-05 ~ 11-08）

### 门禁（修复后）

Core 116 / Compiler 50 / 全量 Host **289 / 289**（含 Determinism）；`image-set --twice --ilverify` 逐字节相同、全部通过 ilverify；
conformance `--gate` OK（521 / 521）；CTS 基线 OK（6868 / 29 / 6）；test_clojure 基线 OK（3877 / 25 / 4）。镜像 13 与 111 逐字节相同。

### r7rs 复核：当前树 vs `be10492`（M19 之前），56 项 × 5 轮同轮配对

第一遍（修复前）两台机器都有可声明回退 diviter x1.271 / x1.244、divrec x1.182 / x1.165（5/5）。分段定位（111，各段 5 轮）：
① 158 ~ 164 x1.404 / x1.204，② 170、③ 换 C# 编译器、④ 182 与之后都在噪声内；再按 111 上保留的中间构建细分，落在
158-ref → 158-cur 一步（x1.332 / x1.193，3/3）。

**原因**：158 让直接链接与 KnownCall 也看 `inline-calls?`（为 `-g` 保留物理帧），而镜像构建本来就把 `inline-calls?` 关掉
（不做新的内联决定）——core.clj 内部的调用全部退化为对 Var 根值的 `IFn.invoke`（`nnext` 两次虚调用、没有 isinst 守卫）。
这就是 158 记下的"镜像 IL 形状差 3.4×"。C# 编译器忠实移植了这一行为。用户代码与包不受影响。
**修复**：`-g` / `CLEAT_NO_INLINE` 改用独立的 `FormAnalyzer.PhysicalFrames`，直接链接与 KnownCall 只看它（`Context.cs`、`CallSites.cs`）；
镜像 1,674,240 → 2,077,696 B（158 之前 2,074,624 B）。

修复后的全表（判定口径 `m16sum.py`，`floor-Debian-VM01.json`，5% / 同向 5 轮）：

| | 本机（13） | 111 |
|---|---|---|
| 几何平均（56 项配对中位） | **x0.966** | **x0.975** |
| diviter / divrec | x1.002 / x1.034（不可声明） | x0.952 / x0.966 |
| 可声明改善 | ctak x0.854、cat x0.921、nucleic x0.927、ray x0.931、destruc x0.935、deriv x0.944、string x0.949（array1 x0.674、slatex x0.775、parsing x0.816 在逐项地板下不判） | nucleic x0.851、ctak x0.900、bv2string x0.914、graphs x0.915、quicksort x0.912、mbrotZ x0.920、chudnovsky x0.921、destruc x0.929、cat x0.930、simplex x0.936 |
| 可声明回退 | triangl x1.065、fibfp x1.106 | triangl x1.089、matrix x1.057 |

- **triangl**：分段（111）① x1.032、② x1.052（170 删 sigspec，已接受的代价）、③ x1.005、④ x1.026；② 之外的部分各自在门槛下。
  用户裁决接受（2026-11-08）。
- **matrix（只在 111）**：主要来自 ③。两个编译器给 matrix 生成的 IL 相同（包：除 MVID 外相同；eval：360 个方法的 IL 长度 / 局部 / 栈深 /
  AggressiveOptimization 全同），同编译器换镜像 x0.983、同镜像换编译器 x1.074，关分层编译时 C# 一侧反而更快——是进程里驻留哪个编译器
  带来的堆 / JIT 时机差异，不是代码生成；本机 x0.963。记为环境性。
- **fibfp（只在本机）**：修复前 x1.002，修复后 x1.106。fibfp 只用内联的算术，eval 下修复前后生成的 IL 相同（eval 里 `InlineCalls` 原本就开），
  变的只有镜像大小（+400 KB）带来的布局 / 分层时机；111 上修复前后对照 x1.077 但只有 6/9 同向（两档：约 1.08 与约 0.97），111 全表不可声明。
  记为环境性的双态，未单独裁决。

数据：本机 `bench/r7rs/results/pair169-local.jsonl`（修复前）、`pair169kc-local.jsonl`（修复后）；111 `~/workspace/cleat-169-new/bench/r7rs/results/`
下 `pair169-111`、`pair169kc-111`、`bis169-s*`、`bis169b-a*`、`bis169c-*`、`kc169*`、`mx-*`、`fibfp-kc`。

### 其他移交

- cleat-160 移交的默认路径启动缺口：R2R 只作 opt-in，默认 `-e nil` 约 0.66 s、REPL 首提示符约 0.83 s（161 / 181 之后），记入 README "已知限制"，不再单列任务。
- cleat-162 剩 push 后的 GitHub Actions 实测，不阻塞本节。

另：`m16sum.py` 剔除 load > `R7_PAIR_MAXLOAD` 的配对后，剩余不足 5 对时报"不可判定"——第一遍本机 diviter 就是这样（第 5 轮 load 2.02，
剩 4 对全部同向），看到"不可判定"时要回看原始配对。
