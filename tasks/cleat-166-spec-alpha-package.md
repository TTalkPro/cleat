# cleat-166 clojure.spec.alpha 包

- 里程碑：M19　依赖：无　状态：完成（2026-11-05）
- 设计依据：[29 §1 第 3 条 / §7](../designs/29-usability-and-engineering.md)、[18](../designs/18-tools-build.md)

## 问题

`(require '[clojure.spec.alpha :as s])` 报 FileNotFoundException。spec 是很多库的依赖，没有它，移植 cljc 库会卡在第一步。

## 工作内容

1. 新建 `packages/spec.alpha`（cleat 项目，与 nrepl / neorepl 同样的结构）：以上游 `clojure/spec.alpha`、`core.specs.alpha` 为蓝本，
   宿主差异参照 ClojureCLR 的 clr.spec.alpha。
2. `test.check` 生成器部分：spec 的 `gen` 依赖 test.check，先以延迟加载的形式支持（与上游一致，没有 test.check 时 `gen` 报错）。
3. 上游测试全部移植（同 cleat-062 的做法），失败项逐条归类。
4. 裁决 core 的宏 spec（`defn` / `let` / `ns` 的 :args 检查）是否默认启用：量宏展开时间和镜像构建时间，
   检查错误黄金文件的变化，再决定。
5. 打 NuGet 包，README 写依赖方式。

## 验收

- 上游测试通过率与失败分类写进任务书；`s/def`、`s/valid?`、`s/conform`、`s/explain`、`s/fdef` + `instrument` 可用。
- 宏 spec 裁决有数据支撑；启用时 CTS / test_clojure / conformance 基线的变化列清楚。

## 实施记录（2026-11-05）

### 1. 两个包

- **`packages/spec.alpha`**（包程序集 `Cleat.SpecAlpha` 0.6.250）：`clojure.spec.alpha` / `.gen.alpha` / `.test.alpha` 取自 ClojureCLR 的
  [clr.spec.alpha](https://github.com/clojure/clr.spec.alpha)（0.6.250，对应上游 0.6.249），`clojure.core.specs.alpha` 取自
  clr.core.specs.alpha（0.6.133）；上游测试 3 个文件（spec / instr / multi-spec）。
- **`packages/test.check`**（`Cleat.TestCheck` 1.1.4）：取自 clr.test.check。spec 的生成器（`s/gen`、`s/exercise`、`stest/check`）与上游一样
  **延迟加载**它——没有它时只有这些函数报错，其余可用（工作内容第 2 条）；要生成器就把它也放上加载路径 / 加依赖。
- 两个包都有 `cleat.edn`、`build.clj`（`cleat -T:build pack | install`）、`test/run.clj`、README（含依赖写法与差异清单）、EPL 许可文件。
- `PackageTests.SpecAlphaAndTestCheck`：两套上游测试各跑源码一遍、编出的包程序集一遍（加载路径上没有源码）。`tools/build.cs image-set` 也编这两个包，
  `--twice` 20 个文件逐字节相同、`--ilverify` 9 个程序集全过（含 Cleat.SpecAlpha / Cleat.TestCheck）。

### 2. 上游测试（工作内容第 3 条）

| 套件 | 结果 | 改了什么 |
|---|---|---|
| spec.alpha | **13 个测试、174 个断言全过**（源码与包程序集） | 起初 170 / 174：instrument 找调用者的 4 个断言（见下） |
| test.check | **104 个测试全过**（断言 226 ~ 231 个，随随机种子变；源码与包程序集） | 起初 1 失败 1 错误（见下） |

失败项归类与处理（都标了 `;;; CLEAT-166` 注释）：

1. **ClojureCLR 移植版的缺陷**（JVM 与 ClojureCLR 上同样会错，不是 cleat 的问题）：
   - test.check `shrink-long` 对 `Int64/MinValue` 调 `Math/Abs`——.NET 抛 OverflowException，Java 原样返回；改成对最小值直接取 0 个前导零。
   - spec 的 `instr_test.clj` 把命名空间改名为 `instr-test`，期望的 `:var-scope` 符号却仍写 `instr/…`，跟着改。
2. **cleat 与 JVM 的宿主差异**：
   - spec 的 instrument 只把 `invoke` / `invokeStatic` 帧当 Clojure 帧；cleat 的变长函数体在 `doInvoke` 里（JVM 1.9+ 另有 `invokeStatic`），加上 `doInvoke`。
   - `clojure.lang.Compiler/demunge` → `clojure.lang.Munge/demunge`（同 clojure.main 的做法）。
   - test.check 的 `gen/byte` 断言 `(instance? Byte %)`：cleat 的**整数装箱模型**把装箱的整数统一成 Int64（偏离表 integer-box-model，
     旧 Clojure 编译器的构建也一样，不是回退），断言改成"0..255 的整数"。用户裁决不追求 Java 完全兼容（见 cleat-168），模型不改；
     README"与 Clojure 的偏离"里原来写的"`(int x)` 是 `Int32`、`(float x)` 是 `Single`"与实际不符，更正。
3. **运行时补回**：`RT.checkSpecAsserts`（cleat-012 随"没有 spec"删掉；`s/assert` 展开成对它的读取），开关 `CLOJURE_SPEC_CHECK_ASSERTS`（ClojureCLR 同名）。

### 3. 宏的 spec 检查（工作内容第 4 条的裁决）

**实现**：C# 分析器的 `CallMacro` 展开前调 `clojure.spec.alpha/macroexpand-check`（Clojure 的 `Compiler.checkSpecs`），条件是
spec 已经加载（找得到这个 Var；缓存）且 `RT.instrumentMacros`（`CLOJURE_SPEC_SKIP_MACROS=true` 关）。与 ClojureCLR 不同，**不为此去加载 spec**：
没加载 spec 时只多一次缓存的查找。core 宏（`defn` / `let` / `ns` …）的 spec 在 `(require 'clojure.core.specs.alpha)` 后生效，`s/fdef` 过的用户宏立即生效。
错误文本与 Clojure 1.12 的 `ex-str` 相同（`Diagnostics` 新增 spec 分支）：

```
Syntax error macroexpanding clojure.core/let at (REPL:1:1).
[a] - failed: even-number-of-forms? at: [:bindings] spec: :clojure.core.specs.alpha/bindings
```

instrument 的调用失败同样按 1.12：`Execution error - invalid arguments to user/g at (REPL:1).` 加 `:k - failed: int? at: [:x]`。

**数据**：

| 场景 | 检查开 | 检查关（`CLOJURE_SPEC_SKIP_MACROS=true`） |
|---|---|---|
| 镜像构建 `--compile-boot`（不加载 spec），3 次 | 6.72 / 8.14 / 6.99 s | 6.81 / 6.86 / 6.66 s（中位差在噪声内） |
| 加载 spec + core.specs 后编 test.check 的 `generators.cljc`（约 1800 行），同进程 5 次 | 770 ~ 1183 ms，中位 1046 | 672 ~ 995 ms，中位 844（约 +24%） |

镜像重编后与 src/boot 逐字节相同（镜像构建不加载 spec）。错误黄金文件、CTS / test_clojure / conformance 基线的运行都不把 spec 放上加载路径，**不受影响**。

**裁决：缺省开（与 JVM 一致），只在用户自己加载了 spec 之后生效**；约 24% 的编译期代价由用了 spec 的程序承担，可用 `CLOJURE_SPEC_SKIP_MACROS=true` 关。
designs/README 的环境变量表 19 → 21（`CLOJURE_SPEC_CHECK_ASSERTS`、`CLOJURE_SPEC_SKIP_MACROS`）。

### 4. 顺带修的编译器缺陷：方法的源文档

查 instrument 的调用者帧时发现：变长函数 `doInvoke` 的栈帧报 `core.clj:3`（行号是用户文件的，文件名错）。原因是一个方法只挂一个 PDB 文档，
而 `SeqPointCode` 每遇到带文件的节点都改写方法的源文件——`apply` 等 core.clj 的 `:inline` 展开出的节点带 `core.clj`，最后一个内联节点就决定了整个方法的文档。
旧 Clojure 编译器（cleat-158 的 `seq-point-code`）同样如此，C# 移植照搬了。现在方法的文档取本单元的源文件（没有时取第一个带文件的节点），
别的文件来的节点不发序列点（JVM 同样不给内联展开单独的行）。修后 `doInvoke` 帧报 `/tmp/frames.clj:3`。

**已知限制（未改）**：.NET 的栈跟踪不含被 JIT 内联的帧——小函数从别的函数里调时整帧消失，从顶层调时 `invokeStatic` 被内联进 `invoke` 包装、
帧没有位置（cleat-158 的 `-g` 保留物理栈帧）。instrument 的 `:caller` 因此在缺省模式下可能指向更外层的函数。

### 5. 门禁

- Release 构建 0 error；Core 114 / 114，Compiler 49 / 49；Host 全量 **288 / 288**（14 m 6 s；286 + 162 的补全测试 + 本任务的 SpecAlphaAndTestCheck）。
- `image-set --twice --ilverify` 通过（见第 1 节）。
