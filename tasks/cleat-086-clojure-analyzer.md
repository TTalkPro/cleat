# cleat-086 Clojure 前端（分析器）

- 里程碑：M8（阶段 A）　依赖：cleat-085　状态：未开始
- 设计依据：[19 D3](../designs/19-clojure-self-hosting.md)

## 目标
用 Clojure 写的前端取代 psyntax + `src/scheme/clojure/{syntax,namespaces,syntax-kinds}.sls`，直接产出 L0，与现行"展开 + parse-core"的输出相同。

## 工作内容
1. 宏展开：Var 的 `:macro`、`(&form &env …)`、`&env` 为 `LocalBinding` 映射、`macroexpand-1` / `macroexpand` 的停止点、展开错误的 phase 与源位置。
2. 决议：直接用 `clojure.lang.Namespace`（别名、refer、import、重定义、`:const`）；特殊形式只在头部位置识别。
3. 全部特殊形式（`def`、`if`、`do`、`let*`、`loop*` / `recur`、`fn*`、`quote`、`var`、`throw`、`try`、`monitor-*`、`.`、`new`、`set!`、`letfn*`、`case*`、`deftype*`、`reify*`、
   `proxy-class*`、`gen-class*`、`await*`、`import*`）与宿主语法糖；类型提示与原始签名经 `HostSyntax`（cleat-082 后在 Cleat.Core）。
4. `:inline`、小函数内联（`.inl`：Clojure 编译器写自己的格式，两边各自从源码构建时各用各的）、直接链接（`.ifc`）。
5. 产出 L0 记录（cleat.compiler.languages），形状照 parse-core：包括 Scheme 痕迹（catch 处理器 lambda、`'()` 占位），留到 cleat-092 再清理。

## 验收标准
- G1@L0：差分工具对输入集全部顶层形式零差异（规范化后）。
- 错误黄金文件（`tests/errors`）在只换前端的模式下通过（若 085 的开关允许"Clojure 前端 + Scheme 后续 pass"组合则用它，否则在 090 一并验收）。
