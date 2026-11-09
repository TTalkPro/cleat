# cleat-184 core.async：go 块里的 proxy

- 里程碑：M21　依赖：cleat-169（收口后开始）、cleat-167　状态：完成（2026-11-09）
- 设计依据：cleat-167 的落地记录；cleat-043（proxy 的编译期生成）

## 问题

`go` 宏先用 tools.analyzer(.clr) 把块分析成语法树再改写成状态机。tools.analyzer 只认 Clojure 的标准特殊形式，而 cleat 的 `proxy`
展开成 cleat 自己的特殊形式 `proxy-class*`（编译期生成真正的子类，AOT 时进包程序集，cleat-043）。tools.analyzer 把它当普通符号去找 Var：
`Could not resolve var: proxy-class*`。所以**直接写在 go 块里的 proxy 编不过**；go 块外的 proxy 不受影响（现在的绕法：proxy 挪进普通函数，go 里调用）。
ClojureCLR 的 proxy 在运行时生成类，展开结果是普通函数调用，所以那边没有这个问题。tools.analyzer 的上游测试 `analyze-proxy` 因此注释掉了。

## 工作内容

1. 在 tools.analyzer.clr（`packages/tools.analyzer`）里给 `proxy-class*` 加解析：产生一个不透明节点，emit-form 时原样输出；
   状态机变换不需要看它内部。
2. **闭包捕获**：proxy 的方法体可能引用 go 块里的局部。要把方法体当 fn 体分析出它用到的局部（`:closed-overs` / 子节点），
   否则这些局部跨过 `<!` 等停顿点时状态机不会保存它们。
3. 同样检查 cleat 其余的非标准特殊形式在 go 块里是否有同类问题（列出 `Cleat.Core.Expand.SpecialForms` 之外 cleat 编译器认的形式，逐个确认），一并处理或记账。
4. 测试：go 块里的 proxy（不捕获 / 捕获 go 局部 / 在 `<!` 之后用到被捕获的局部）；恢复 tools.analyzer 的 `analyze-proxy` 测试。

## 验收

- 上述用例在源码与包程序集两条路径都正确；tools.analyzer、core.async 的上游测试全过（含恢复的 `analyze-proxy`）。
- README"已知限制"与 packages/core.async、packages/tools.analyzer 的 README 里这一条删除。

## 结果（2026-11-09）

### 做法

- cleat 的 `proxy` 展开成 `(let [pc (proxy-class* 基类 接口 …) p (construct-proxy pc 实参 …)] (init-proxy p {"方法" (fn [this …] …) …}) p)`：
  `proxy-class*` 只含类型名、求值为编译期生成的子类；**方法体是 init-proxy 映射里的普通 `fn`**，tools.analyzer 照常分析，闭包捕获的 go 局部
  （`:closed-overs`）跨 `<!` 停顿点照常由状态机保存——工作内容第 2 条因此不需要专门的处理，用例覆盖。
- tools.analyzer.clr（`packages/tools.analyzer`，`;;; CLEAT-184`）：`specials` 加 cleat 的 `proxy-class*` / `gen-class*` / `await*`；
  `proxy-class*` → 不透明节点 `:proxy-class`（`:tag` System.Type），`gen-class*` → `:gen-class`（选项是数据），emit-form 原样输出；
  core.async 的状态机对未知 op 走缺省的 `RawCode`，不用改。`await*` 解析时报清楚的错误（只在 `^:async` fn 里直接有效，go 块的体会变成状态机的 fn）。
- 第 3 条（cleat 其余的特殊形式）：编译器认的特殊形式里 tools.analyzer 不认的是 `proxy-class*`、`gen-class*`、`await*`、`escape*`；前三个如上，
  `escape*` 只由编译器内部生成（源码里不出现），tools.analyzer 看不到。
- 顺带修 tools.analyzer.clr 的 `convertible?`：形参类型解析不出（泛型方法的 `T[]`，如 `Array/Sort<T>`）时对 nil 调 `.IsAssignableFrom`，
  go 块里写 `(System.Array/Sort arr)` 宏展开即 NullReferenceException；改为按不可转换（该重载不参与匹配）。

### 测试

- core.async：新增 `test/clojure/core/async/cleat_go_proxy_test.clj`（cleat 专有，6 个测试：go 里的 proxy、捕获 go 局部、跨停顿点、实现接口、
  泛型重载、go 里的 await 报错）；上游 51 个 + 6 个 = 57 个测试、348 个断言。
- tools.analyzer：恢复上游 `analyze-proxy`，44 个测试、371 个断言。
- `PackageTests.CoreAsyncWithAnalyzerAndReader`（源码与包程序集各一遍）通过。
- README"已知限制"、packages/core.async 与 packages/tools.analyzer 的 README 更新；顺带删掉 core.async、tools.reader README 里 183 之后已失效的 by-ref 差异条目。

### 门禁

Core 116 / Compiler 50 / 全量 Host 292；`image-set --twice --ilverify`（含重编的 tools.analyzer、core.async 包程序集）逐字节相同、全部通过 ilverify；
conformance `--gate`、CTS / test_clojure 基线 OK。本节只改 packages/，镜像不变。
