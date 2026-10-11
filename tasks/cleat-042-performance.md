# cleat-042 性能：拆箱、协议内联缓存、跨单元直接链接

- 里程碑：后续　依赖：cleat-032　状态：进行中
- 设计依据：[04 §10](../designs/04-compiler.md)、[06 §4](../designs/06-llvm-backend.md)

## 工作内容
1. 基准集（`bench/`：语料中的算术 / 集合密集用例 + ClojureCLR 常用基准），记录 M6 基线。
2. 类型推断与拆箱：`^long`/`^double` 局部与 `label` 形参用 `int64`/`double` 局部变量（IronScheme iron-034c 的工作表法）；原始类型签名 `IFn.LL` 等。
3. 协议调用内联缓存（站点类或 `MethodImplCache` 快速路径内联）；`keyword-invoke` 站点缓存。
4. 跨单元直接链接到用户 `.clj.dll`；`:inline` 之外的小函数内联。
5. 局部变量清除（`locals-clearing`）。
6. LLVM：NaN-boxing / 小整数立即数、`musttail`、`hoist-var-derefs`。

## 验收标准
- 每项有基准前后数字；不动点与符合性基线不退化。

## 进度（2026-10-11）

1. **基准集与基线** —— 完成。`bench/` 七个基准（sum_loop、fib、reduce_vec、lazy_chain、assoc_map、protocol、keyword_invoke），
   `tools/build.cs bench [-r n] [--write] [--only re]` 聚合最小/中位。基线（Release，5 次，`bench/baseline.edn`）：
   sum_loop 1006 ms（500 万次装箱算术）、fib 49 ms（fib 25）、reduce_vec 119 ms（100 万 reduce）、
   lazy_chain 349 ms（30 万惰性链）、assoc_map 219 ms（20 万 assoc）、protocol 517 ms（40 万分派）、
   keyword_invoke 631 ms（50 万 `(:k m)`）。之后的每项优化对照此表。
4. **跨单元直接链接到用户 `.clj.dll`** —— 已在 cleat-033 完成（受保护的 invokeStatic 直调，接口文件登记 + with-redefs 回退；
   本基准的数字即含此优化的状态）。
6. **LLVM** —— 整体搁置（TASK.md 2026-10-10 决定），不在本任务内。

待做：2（类型推断与拆箱，iron-034c 工作表法 + `IFn.LL` 原始签名）、3（协议与 keyword-invoke 的内联缓存）、
4 剩余（`:inline` 之外的小函数内联）、5（locals-clearing）。每项完成后在此记基准前后数字。

### 第 2 项的先期分析（2026-10-11，实验记录）

- `+` / `<` / `inc` 的 `:inline` 已生效：`(clojure.core/+ acc i)` 与手写
  `(. clojure.lang.Numbers (add acc i))` 同速（1051 vs 1049 ms / 500 万次）——内联不是瓶颈。
- 实验加了 Int64 快速路径（`Numbers.add` 等五处 `is long` 直连原语，省去 `ops(x)` 的
  `Type.GetTypeCode` 双重分派）：sum_loop 103%–105%，无改善——分派也不是瓶颈。
- 装箱实验：同样的循环去掉 `acc` 累加（每迭代 1 次分配而非 2 次）1126 → 724 ms——**成本在
  `Numbers.add(object,object)` 的结果装箱与 GC 压力**（每迭代 ~80 ns/次分配）。
- 结论：第 2 项的正确形态是完整拆箱——`^long`/`^double` 局部与 loop 形参用 int64/double
  局部变量、`+`/`<`/`inc` 在类型已知时发原始 `add.ovf`/`clt`（checked 溢出语义必须保留，
  不能用无检查的 `add`）；已移植的 `infer-var-types!` 只认 Scheme 的 `$fx…`/int32 语义，
  对 Clojure 的 Int64 返回未知，需要为 `$clj-call` 节点补 Int64 类型的推断与原始发射。
  预期 sum_loop 1006 → ~150 ms。尚无对外的 C# 改动（实验已回退）。

### 第 2 项的实现状态（2026-10-11，WIP）

已落地（全部测试绿、语义无损）：
- `l64` 类型接入 codegen：`type-clr`（int64 局部）、`box-raw!`（box int64）、`quote` 的 Int64 字面量识别
  （`real?`+`exact?`+`clr-is System.Int64`；注意 `exact?` 对非数会抛异常，必须放在 `real?` 之后）、
  `infer-var-types!` 收敛判据纳入 l64、label/jump/tlocal 链路通用。
- `numbers-arith-shape?` / `numbers-arith-args`（识别 `($clj-call "CallStatic" 3 "clojure.lang.Numbers" op '() 实参…)`）
  与 `numbers-raw-emit!` / `numbers-raw-emit-args!`（int64 原始 add/sub/clt + 位测试溢出检查 + ThrowLongOverflow，
  异常与 Numbers 完全一致）、`cg-raw` 外壳（l64 上下文走原始发射，否则 cg-raw/default）、
  cg 的 clj-call 情形挂钩（全 l64 时绕过 CallStatic/Reflector）。

未通（下一步调试）：sum_loop 未获益（1006 → 1042 ms，噪声内）。推断/识别/发射三段中至少一段对基准形式未生效。
已排除：`:inline` 缺失、`ops` 分派开销、`mctx-big?`（基准已改为独立小方法）、陈旧 dll（bench 目录 dll 已删）。
调试入口：`CLEAT_DEBUG_INFER=1` 环境变量 + codegen.sls 的 infer 探针（当前为 display 版，注意 fasl 需重生成才生效）；
建议先确认 infer-walk 是否真的为 loop 形式的变量登记了 l64 候选（`(GU #t (#f) 1)` 曾显示 -main 形式的 loop 旗标为 #f——
dotimes 展开后的形状可能未被 loop-forms 识别，这是最可疑的一环）。

### 触发调试的关键发现（2026-10-11，第二轮 WIP）

- **形状检查的确定性 bug（下次直接修）**：`:inline` 产物的 gen 元数据是 **#f 而非 '()**——
  `(dotimes ...)` 的 `(+ acc i)` 实际展开为 `($clj-call "CallStatic" 3 "clojure.lang.Numbers" "add" #f 实参…)`。
  形状检查的 `(null? gen)` 恒为假，**必须改为 `(not gen)`**。修正此一处即可让识别生效（推断候选与
  CallStatic 形状都已验证存在：infer 探针确认 loop 变量 `i`/`acc` 是候选，raw/boxed 计数正确）。
- 该修正已验证会让形状匹配（修正后推断在识别路径上继续走通到 emit），但引入了引导失败：
  codegen.sls 的增量补丁损坏了文件结构（func-type 定义被吞、括号失衡）——**增量补丁方式对 nanopass
  文件不可靠**，下次会话应当：checkout 到 29b677c → 单次 python 全量重放（本文所列 + gen 修正）→
  立即跑 `stage0 buildscript-nano` 引导冒烟 → 再进基准。
- 调试探针（infer-entered/cands 文件日志、CLEAT_DEBUG_INFER 门）的写法已验证可用
  （call-with-output-file 不接受 'append 位置参数，用计数器文件名）。

### 探针技术备查（2026-10-11，供下次直接使用）

- 已验证可用的文件日志探针写法（call-with-output-file 不接受 'append；用计数器文件名）：
  ```scheme
  (define infer-log-seq (let ((n 0)) (lambda () (set! n (+ n 1)) n)))
  ;; infer-var-types! 进入 / 收敛处：
  (call-with-output-file (string-append "/tmp/opencode/infer-dbg." (number->string (infer-log-seq)) ".log")
    (lambda (pt) (write ...) (newline pt)))
  ```
- 陷阱：探针必须放进 fasl 才对宿主 AOT 编译生效（rm -rf src/boot/cleat.scheme.clojure && dotnet build）；
  display 输出会进 Scheme 引擎自己的端口，宿主进程里不可见——必须写文件。
- AOT 产物的 IL 检查工具：/tmp/opencode/inspect（System.Reflection.Metadata 逐字节解码，打印方法/局部/IL）。
- 已验证的事实链：`(loop …)` 宏转发到 `loop*` 特殊形式（core.clj:40）→ 降级为 label/jump；
  `:inline` 形状中 gen 元数据是 #f（需 `(not gen)` 判定）——此修正 + 全部设施已在本会话验证可构建、
  语义无损（583 测试绿），只是基准触发尚未打通（sum_loop 恒 ~1010-1070ms，与装箱基线一致）。

### 第二轮调试的增量（2026-10-11 晚，已提交）

三处修复已进树（583 测试全绿、基准未获益——发射仍未被触发）：
1. 形状检查 gen 修正：`(null? gen)` → `(not gen)`（:inline 的 gen 元数据是 #f）。
2. `deps` 下钻 clj 子树（clj-call/clj-invoke/clj-keyword-invoke/clj-static-call/clj-var-set!/clj-def）——
   否则变量变化不会重排依赖它的候选（不动点缺依赖边）。
3. `expr-type` 的 clj-call 情形改用**暂定 top** 语义：实参未定时返回 top 供不动点继续收敛，
   而非直接塌缩 #f（#f 不可恢复）。

下一步排查（按可疑度）：
- loop-forms 旗标：`(GU #t (#f) 1)` 曾显示 -main 形式的旗标为 #f——dotimes/(loop…) 展开后的形状可能
  未被 loop-forms 识别 → infer-var-types! 只对旗标为真的形式运行。在 compile-forms->lcil* 的
  loop-forms 计算处加探针确认。
- CallStatic 形状中 t 是字符串 "clojure.lang.Numbers"（已验证 parse-dot 走 type-spec→字符串），
  但 add 的实参里 `(quote "add")` 的 quoted-datum 提取应无问题——若识别仍不中，dump lcil 文本
  （clojure-compile-unit 加 lcil->string 一行）直接看 clj-call 节点的实际形状。

### 本轮调试补充（2026-10-11 深夜——避免重复踩坑）

1. **每次 bash 工具调用是独立 shell**：export CLEAT_DEBUG_INFER=1 必须与被测命令同一条命令链，否则探针静默不触发——多轮"无日志"观测因此无效。
2. **call-with-output-file 不支持追加模式**（第三参位置被读成布尔，报 "not boolean: append"）；多探针用计数器文件名或接受覆盖。
3. 对 codegen.sls 的**增量补丁已两次损坏文件结构**（括号失衡/顶层 form 被吞，报 func-type unbound 或 "mismatched brackets"）。任何新修改必须：git checkout 到干净态 → 单次 python 全量重放 → 括号平衡校验 → stage0 引导冒烟 → 再构建。
4. 当前 9c03e1f 状态 = 三处修复（形状 gen=#f、deps 下钻、暂定 top）已进树、全测试绿、基准无变化。下一会话第一步：**单次全量重放**完整变更集（l64 推断/发射设施 + gen 修正 + 计数器版探针），然后用探针数据定位识别链路断点。

### 探针迭代二结论（2026-10-11 深夜）

- 带探针的构建在**引导编译路径**上崩溃（探针写文件抛异常 → 汇总为构建失败），bench 表单的推断数据未能取得。
- 下次探针迭代必须：探针写文件用 `guard` 包裹（任何异常写 'ERR 后继续），且**不放在引导编译会经过的路径上**
  （generate-unit/infer-var-types! 会被引导期的编译调用——需要按 assembly-name 过滤，只对 bench/clojure 单元记录）。
- loop-forms 旗标疑点仍未验证（此前 GU 只出现过 -main 表单的旗标 #t/#f 各一次，无法归因到 run 表单）。
