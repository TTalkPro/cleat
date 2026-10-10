# cleat-017 cleat.kernel 与 core.clj 分层适配

- 里程碑：M2　依赖：cleat-011、cleat-013　状态：完成
- 设计依据：[03 §5](../designs/03-language-core.md)、[00 D15](../designs/00-overview.md)

## 目标
`src/clojure/` 的源码树：`cleat/kernel.clj` + 从 ClojureCLR 改写的 `clojure/*.clj`。

## 工作内容
1. `cleat/kernel.clj`：把原语 Var `def` 为可用函数；编译器与 nanopass 需要的最小函数集（列表见 03 §5）；`defmacro`/`defn`/`let`/`loop`/`fn`/`when`/`cond`/`and`/`or` 等基础宏若 `core.clj` 开头已定义则不重复。
2. 复制 ClojureCLR `Clojure.Source/clojure/` 的文件到 `src/clojure/clojure/`，保留 EPL 头；`core.clj` 开头 refer `cleat.kernel`；去掉对 `HostExpr/MaybeType`、`Compiler/…` 的调用（改原语）；`;;;` 注释的 CLR 差异保留；移除 `parse-eval`、`await` 相关；`*compiler-options*` 等 Var 的来源改为运行时。
3. 把 `#?(:cljr …)` 保持可用，新增 `:cleat-llvm` 分支先留空。
4. 记录每处改动（文件、行、原因）到本文件。

## 验收标准
- 03 §5 加载顺序中的每个文件都能在 stage0 上展开（无未知形式）。

## 结论（2026-10-10）

完成（只读验收；"在 stage0 上展开"留给 cleat-018，stage0 尚未就绪）。
- `src/clojure/clojure/`：从 ClojureCLR `Clojure.Source/clojure/` 复制 40 个文件（目录结构、EPL 头、`;;;` CLR 注释原样保留；ClojureCLR 里只有 `repl/deps.cljc` 用 `.cljc`，其余都是 `.clj`，扩展名照旧）。未复制：`math.clj`（不在 03 §5 列表，可随时补）、`clr/async`、`clr/basis*`、`clr/process`、`tools/deps`（依赖 tools.deps / 异步）、`core/specs`（ClojureCLR 源码树里本来就没有；cleat 无 spec）。`clojure.clr.api` 是 C#，不在源码树里，无需处理。
- `src/clojure/cleat/kernel.clj`：占位（1 个形式，`(clojure.core/in-ns 'cleat.kernel)`）+ 说明。原语 Var 由 `PrimitiveRegistry` 绑定，不在这里 `def`；03 §5 设想的"最小函数集"暂不重复 core.clj（core.clj 前 700 行已自举 `defn`/`defmacro`/`let`/`fn`/`when`/`cond`…，nanopass / compiler 在加载顺序里排在 core 之后）。**偏离 03 §5**：core.clj 不 `refer cleat.kernel`（与 `clojure.core/first` 等重名会冲突），需要原语处写全名 `cleat.kernel/…`。
- `src/boot/boot-order.edn`：03 §5 的顺序（kernel → core → core.clj 末尾 load 的 core_clr / core_proxy / core_print / genclass / core_deftype / core/protocols / gvec / instant / uuid → string set walk edn data template test stacktrace repl main pprint(+7) zip clr.io clr.shell reflect(+clr) datafy），`:loaded-by` 标记由上级文件 `(load …)` 拉入的条目，`:boot false` 标记按需加载的 reducers / server / test.junit / test.tap / repl.deps。
- 测试 `tests/Cleat.Core.Tests/CoreSourcesTests.cs`（2 项）：对 boot-order.edn 的每个文件用 `LispReader` 读完全部顶层形式（`*ns*` 绑到一次性命名空间、`*read-eval*` true、`{:read-cond :allow}`，平台特性 `:cleat :cleat-clr :cljr`），无 reader 错误；合计 1788 > 1000（也是 cleat-009"读完 core.clj"的验收）。全套 129 项通过。

### 文件与顶层形式数

| 文件 | 顶层形式数 |
|---|---|
| `cleat/kernel.clj` | 1 |
| `clojure/core.clj` | 723 |
| `clojure/core_clr.clj` | 49 |
| `clojure/core_proxy.clj` | 16 |
| `clojure/core_print.clj` | 121 |
| `clojure/genclass.clj` | 16 |
| `clojure/core_deftype.clj` | 41 |
| `clojure/core/protocols.clj` | 16 |
| `clojure/gvec.clj` | 17 |
| `clojure/instant.clj` | 23 |
| `clojure/uuid.clj` | 4 |
| `clojure/string.clj` | 29 |
| `clojure/set.clj` | 15 |
| `clojure/walk.clj` | 12 |
| `clojure/edn.clj` | 3 |
| `clojure/data.clj` | 16 |
| `clojure/template.clj` | 3 |
| `clojure/test.clj` | 60 |
| `clojure/stacktrace.clj` | 8 |
| `clojure/repl.clj` | 20 |
| `clojure/main.clj` | 42 |
| `clojure/pprint.clj` | 10 |
| `clojure/pprint/utilities.clj` | 12 |
| `clojure/pprint/column_writer.clj` | 12 |
| `clojure/pprint/pretty_writer.clj` | 58 |
| `clojure/pprint/pprint_base.clj` | 38 |
| `clojure/pprint/cl_format.clj` | 116 |
| `clojure/pprint/dispatch.clj` | 59 |
| `clojure/pprint/print_table.clj` | 2 |
| `clojure/zip.clj` | 30 |
| `clojure/clr/io.clj` | 57 |
| `clojure/clr/shell.clj` | 14 |
| `clojure/reflect.clj` | 7 |
| `clojure/reflect/clr.clj` | 28 |
| `clojure/datafy.clj` | 6 |
| `clojure/core/reducers.clj` | 32 |
| `clojure/core/server.clj` | 25 |
| `clojure/test/junit.clj` | 29 |
| `clojure/test/tap.clj` | 13 |
| `clojure/repl/deps.cljc` | 5 |
| **合计** | **1788** |

### 改动表（每处上一行有 `;; CLEAT-017:` 标记；行号为改动后的）

| 文件:行 | 原来 | 改为 / 原因 |
|---|---|---|
| core.clj:252 | `HostExpr/maybeSpecialTag`（defn 的 resolve-tag） | `(cleat.kernel/maybe-special-tag tag)`，原语待提供 |
| core.clj:254 | `HostExpr/MaybeType tag false` | `(cleat.kernel/maybe-type tag false)`，原语待提供 |
| core.clj:342 | defn 按 `:async` 元数据给 fn 加 `:async true` | 去掉（`await*` 不支持） |
| core.clj:3212 | `(. clojure.lang.Compiler (eval form))` | `RuntimeHooks/Eval` 委托 `.Invoke`；未登记抛 `InvalidOperationException`（同 `RuntimeHooks.Require` 的信息） |
| core.clj:3977 | `clojure.lang.Reflector/prepRet`（aget） | `Cleat.Core.Runtime.Reflector/ConvertFrom`（cleat 没有 clojure.lang.Reflector；同义） |
| core.clj:4083 | `(. clojure.lang.Compiler (macroexpand1 form))` | `RuntimeHooks/Macroexpand1` 委托 `(.Invoke h form nil)`；该槽是可选的，stage0 需登记 |
| core.clj:4150 | `(. clojure.lang.Compiler (load rdr))`（load-reader） | `(Cleat.Core.Runtime.Loader/LoadSource rdr nil nil)` |
| core.clj:4423 | — | 新增 `maybe-resolve-in`（`Compiler.maybeResolveIn` 的 Clojure 版：别名/命名空间 → `FindInternedVar`；含 `.`/以 `]` 结尾 → `RT/classForName`；`ns`/`in-ns`；否则 `GetMapping`） |
| core.clj:4448 | `Compiler/maybeResolveIn`（ns-resolve） | 上面的 `maybe-resolve-in` |
| core.clj:5251 | `(. clojure.lang.Compiler specials)`（special-symbol?） | `(Cleat.Core.Expand.Expander/IsSpecial s)` |
| core.clj:6134 | `Compiler+CompilerException` + `LineVar`/`ColumnVar`（throw-if） | `Cleat.Core.Expand.ExpanderException.`（`*file*` 0 0 `:clojure.error/compile-syntax-check` message cause）；行列未知记 0 |
| core_clr.clj:27 | `gen-delegate`：`HostExpr/MaybeType` + `GenDelegate/Create` | `(Cleat.Core.Runtime.Reflector/MakeDelegate (fn argVec body) type-sym)`；参数不再按委托签名加类型提示（装箱，cleat-042 再拆箱）。string.clj / core.clj tap-loop / core/server / reducers 的 `gen-delegate` / `sys-func` 不用改 |
| core_clr.clj:184 | `HostExpr/MaybeType tsym false`（generate-generic-delegate） | `(cleat.kernel/maybe-type tsym false)` |
| core_deftype.clj:145 | `Compiler/munge` | `clojure.lang.Munge/munge` |
| core_deftype.clj:353 | `Compiler+CompilerException` + `LineVar`/`ColumnVar`（validate-fields） | `ExpanderException.`（`:clojure.error/macro-syntax-check`，原 Exception 作 cause） |
| core_proxy.clj:25 | `(import '(clojure.lang IProxy RT Reflector))` | `clojure.lang` 组去掉 Reflector，加 `'(Cleat.Core.Runtime Reflector)` |
| core_proxy.clj:57 | `(Compiler/IsCompilingSuffix)`（proxy-name） | `""` |
| core_proxy.clj:60 | `GenProxy/GenerateProxyClass`（generate-proxy） | `(throw (ex-info "proxy with a superclass or constructor args is not supported yet (cleat-043)" …))` |
| core_proxy.clj:310 | `(. Reflector (InvokeConstructor c args))` | `(Reflector/New c args)` |
| core_proxy.clj:371 | `proxy` 宏在展开期 `get-proxy-class` + `(new pname …)` | 子集（03 §4）：基类为 Object 且无构造参数 → `reify*`（方法首参仍叫 `this`）；否则展开为运行时 `get-proxy-class`/`construct-proxy`/`init-proxy`（执行时抛 cleat-043）。`proxy-super`/`update-proxy`/`proxy-mappings` 在 reify* 子集上不可用 |
| genclass.clj:56 | `the-array-class`：`HostExpr/BuildArrayTypeDescriptor` + `Compiler/PrimType` | 纯 Clojure：元素类型 = `prim->class` 或 `the-class`，按维数 `.MakeArrayType`（加 `(declare the-class)`） |
| genclass.clj:69 | `HostExpr/LooksLikeArrayType` | `(and (namespace x) (clojure.lang.Util/IsPosDigit (name x)))` |
| genclass.clj:87 | `Compiler/munge` | `clojure.lang.Munge/munge` |
| genclass.clj:95 | `generate-class` 调 `GenClass/GenerateClass` | 校验选项后 `(throw (ex-info "gen-class is not supported yet (cleat-043)" …))`；`gen-class` 宏保留（只在 `*compile-files*` 时走到） |
| genclass.clj:256 | `HostExpr/MaybeArrayType`（the-class-for-definterface） | ns/数字判断 + `the-array-class` |
| genclass.clj:270 | `GenInterface/GenerateInterface` | `(cleat.kernel/gen-interface name attrs extend-types method-sigs)`，原语待提供 |
| genclass.clj:305 | `` `(parse-eval* (#'generate-interface '~m)) `` | `` `(#'clojure.core/generate-interface '~m) ``：接口在求值该顶层形式时生成，后续顶层形式展开时类型已存在（stage0 逐形式求值） |
| main.clj:15 / 120 / 315 / 351 | `clojure.spec.alpha` require、`*explain-out*` 绑定、`spec/explain-out` | 去掉；错误信息里的 spec 数据改 `pr-str` |
| main.clj:19 | import `Compiler Compiler+CompilerException` | `(Cleat.Core.Expand ExpanderException)` |
| main.clj:34 / repl.clj:208 | `Compiler/demunge` | `clojure.lang.Munge/demunge` |
| main.clj:43 / repl.clj:216 | `(instance? Compiler+CompilerException …)` + `.Source` | `ExpanderException` + `.SourcePath` |
| main.clj:493 | `Compiler/loadFile` | `Cleat.Core.Runtime.Loader/LoadFile` |
| repl.clj:15 / 108 / 136 | spec require、print-doc 的 spec 段、`doc` 宏的关键字分支 | 去掉 |

保留不动：`clojure.lang.RT/load`（core.clj `load`，已经经 Loader）、`with-loading-context`（只推空绑定）、`*compiler-options*` / `*compile-files*` / `*compile-path*` / `*warn-on-reflection*` / `*unchecked-math*` / `*file*` / `*source-path*`（core.clj 只 `add-doc-and-meta`，不重新 `def`；RT 的 Var 与 dynamic 性保持）、agent 的 `await` / `await-for`（与 `await*` 无关）、`CountDownLatch.Await`、`by-ref` / `type-args`（运行时抛错的占位函数）、`#?(:clj … :cljr …)`（只有 deps.cljc 有）。没有加 `:cleat-llvm` 分支——目前没有需要按后端分叉的代码。

### 需要的新原语（`cleat.kernel/…`，Primitives.cs 待加）

| 名字 | 签名 | 语义 | 用处 |
|---|---|---|---|
| `maybe-type` | `[form string-ok?] → Type \| nil` | `HostExpr.MaybeType`：Type 原样；无命名空间的符号：名字含 `.` / 以 `]` 结尾 / 泛型·`\|…\|` → `TypeResolver.Resolve`，否则当前 `*ns*` 的 import 映射或 `RT.classForName`，失败返回 nil；`string-ok?` 时字符串经 `classForNameE`。`Expander.MaybeType` 已有同样逻辑（实例方法，依赖词法环境），可抽成静态版 | core.clj:254（`defn` 给 argvec 的 `:tag` 补全名）、core_clr.clj:184 |
| `maybe-special-tag` | `[sym] → Type \| nil` | `int long float double … objects ints longs …` → 类型；`TypeResolver.SpecialTag(name)` 已有，包一层即可 | core.clj:252 |
| `gen-interface` | `[name attributes extend-types method-sigs] → Type` | 运行时生成接口类型（stage0 走 Cleat.Emit，与 D12 的 `deftype*` 同路）；`method-sigs` 每项 `[mname (param-types) return-type pmetas]`，param-type 可为 `(by-ref T)`；`attributes` 为 `extract-attributes` 的 map | genclass.clj:270。**cleat-018 必需**：gvec.clj（`IVecImpl`、`ArrayManager`）、pprint/utilities.clj（`PrettyFlush`）的 `definterface` 都走这里 |

### cleat-018 会碰到的其它运行时缺口（不是原语，需移植 C#）

- `clojure.lang.Printf/Format`（core.clj `format`）：cleat 没有 `Printf`。
- `clojure.lang.Runtime.ImmutableDictionaryEnumerator`（core_deftype.clj:283，`defrecord` 的 `IDictionary.GetEnumerator`）。
- `RuntimeHooks.Macroexpand1` 是可选槽，stage0 要登记才有 `macroexpand-1`。
- 其余被引用的 `clojure.lang.*`（`RT/classForNameE`、`TypeNotFoundException`、`RecordEnumerable`、`SeqEnumerator`、`LineNumberingTextReader`、`LispReader+ReaderException`、`CountDownLatch`、`Murmur3`、`*ns-load-mappings*`、`RT/SystemRuntimeDirectory`、`RT/FindFile`、`RT/LoadCljScript`）已存在。

### 移除 / 守护汇总

- 移除：`parse-eval*`（gen-interface）、`:async` defn、`clojure.spec.alpha`（main / repl）、`GenDelegate`（改 `Reflector.MakeDelegate`，功能保留）。
- 守护（宏保留，实现抛 cleat-043）：`gen-class`；`proxy` 含基类或构造参数的用法——影响 core_print.clj `PrintWriter-on`（StringWriter）、pprint 的 column-writer / pretty-writer / cl_format 的 TextWriter proxy、repl.clj `source-fn` 的 PushbackTextReader proxy：都在函数体里，加载不受影响，调用时才抛。
- 不受影响：全接口的 `proxy`（→ `reify*`）、`definterface`（→ `cleat.kernel/gen-interface`）、`gen-delegate` / `sys-func` / `sys-action`。
