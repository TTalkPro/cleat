# cleat-158 源位置、序列点、PDB 与栈帧

- 里程碑：M19　依赖：无　状态：进行中（实现与语义门禁已过；56 项 5 轮性能配对未完成）
- 设计依据：[29 §1 第 1 条 / §2](../designs/29-usability-and-engineering.md)

## 问题

运行期异常的栈帧没有行号，顶层报错位置指向最外层形式。本机实测（2026-10-26）：

```clojure
(ns t)
(defn f [x]
  (/ x 0))              ; 第 3 行
(defn g [] (inc (f 1)))
(g)                     ; 第 5 行
```

输出 `Execution error (ArithmeticException) at (t.clj:5)`；`CLEAT_TRACE_ERRORS=1` 下只有 `t$g.invoke()`；
`-e` 下 `pst` 显示 `user$g.invokeStatic (:0)`、`#Module.Init0 (:0)`。JVM 报 `t/f (t.clj:3)`。

代码取证：发射器已经支持 `(source …)` 子句和 `(line l1 c1 l2 c2)` 指令（`src/Cleat.Emit/Model.cs:284`、`:314`），
`_debug` 时会 `MarkSequencePoint`（`Emitter.cs:630`、`:683`），也能写 Portable PDB（`Emitter.cs:786 ~ 804`）；
但 codegen 从不发这两种形式，`driver.clj:680` 的 `SaveData … false` 也从不要 PDB。
`f` 的帧缺失，是 analyzer 的跨 fn 内联（`inline-target` / `inline-call`）造成的。

## 工作内容

1. codegen：每个方法发 `(source 文件)`；在 analyzer 已有 `:pos` 的形式（调用、宿主调用、`throw`、
   `let` 绑定初始化、`if` 条件）处发 `(line …)`，粒度对齐 JVM Clojure。没有 `:line` 的形式沿用外层位置。
2. 驱动：包程序集、镜像、`-M` / 脚本加载的单元生成 PDB（与 dll 同名）；eval 的内存程序集走 `_debug` 路径挂文档。
   `pack` 把 PDB 打进 Cleat.Runtime / Cleat.Cli。
3. 内联：被内联的函数体保留它自己源文件的序列点。量化默认模式与"关跨 fn 内联"在 r7rs 上的差距后，
   再决定是否提供 `-g` 命令行选项（不用环境变量，29 §9）。
4. `clojure.repl/pst`、`*e`、`Throwable->map` 的 `:trace` 读到 `文件:行`；帧名 demunge 后对齐 JVM
   （`t/f`），隐藏 `invokeStatic` / `#Module.Init0` 一类实现细节。
5. 顶层 `Execution error … at (文件:行)` 取异常栈里第一个属于用户源的帧，不再取顶层形式的 `:pos`。

## 验收

- 上面的脚本输出 `t.clj:3`，`pst` 列出 `t/f (t.clj:3)`、`t/g (t.clj:4)`。
- `ErrorGoldenTests` 增加内层行号、宏展开后的行号、`-e` 行号三组黄金文件。
- PDB 不参与种子逐字节比较；`clojure-seed` 不动点仍然成立。
- r7rs 56 项同轮配对（本机 5 轮）没有可声明的回退；镜像 / 编译器包体积变化写进任务书。
- 29 §9 的全套门禁。

## 进度（2026-10-26）

- 实现已落地：IR 节点源位置与 nanopass 元数据传播、方法 `(source …)` 与节点 `(line …)`、
  Portable PDB（镜像 / 编译器包 / 库包 / 发布 / eval 内存程序集）、`-g` 关跨 fn 内联、
  顶层错误位置取最内层用户源帧、`pst` 隐藏单元入口帧。
- 驱动的源文件名放线程局部 `DriverOps.SourceFile`，不用 Clojure 动态绑定；被求值代码自己的
  `pushThreadBindings` 不会被驱动的 `binding` 弹出帧误伤。
- 黄金与包测试更新：包程序集现在同产 PDB，`tools.build` 的 `pack` 也包含 PDB。
- 语义门禁：Release build 通过；Core 123/123。全量 Host 在修复前为 283/288，5 个失败
  分别是 seed 滞后、两处 PDB 包目录期望、两处驱动动态绑定回归；修复后相关套件复跑通过：
  Bootstrap + Package 9/9、Error + ValueBoundary 42/42（修复后的全量 Host 复跑仍在待办）。
- `selfhost` / `clojure-seed` 不动点通过；seed 已重生成，`from-seed == stage3`。
- `-g` 量化：本机 10 个代表项 × 5 轮同轮配对，debug / default 几何平均 x1.406；
  destruc x2.053（5/5 慢）、fft x3.076（5/5 慢）、browse x1.253（5/5 慢）、
  takl x1.326、scheme x1.204、parsing x1.102。`-g` 因此只作 opt-in，不改默认。
- 待完成：56 项 × 5 轮完整同轮配对（外部负载导致先中止在 2 轮，另补代表项 5 轮）、
  修复后全量 Host 复跑、默认相对改动前基线的序列点 / PDB 成本确认、最终体积记录与全套门禁复核。
