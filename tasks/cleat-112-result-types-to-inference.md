# cleat-112 结果类型交给推断：宿主调用返回的数组类型给无提示局部、deftype 原始字段读取的类型进算术（cleat-107 原因 6、2）

- 里程碑：M11　依赖：cleat-111　状态：完成（2026-10-16）
- 设计依据：[21](../designs/21-r7rs-gaps.md)

## 工作内容
1. 复现：`(let [v (object-array 3)] (aset v 0 a))` 的 aset 走运行时决议；`(+ (.-re a) (.-re b))`（^double 字段）走 Object 算术。
2. let 变量的静态类型取初值的宿主结果类型（分析器给引用类型的局部带标注 / codegen 的 static-arg-type 认局部）；
   GetInstanceField 的字段类型是 long / double 时 expr-type 给 :l64 / :fl，原始发射直接读字段。
3. 语义用例开 / 关对照。

## 验收标准
- array1、scheme、matrix、mbrotZ 前后数字。
- 基线、门禁、全量不变；改了 src/clojure 的种子不动点。

## 结果（2026-10-16）
1. 无提示的 let 局部取初值的宿主类型：`init-tag` 在 `(new T …)` / `(T. …)` 之外用 `host-form-type`（宿主调用的返回类型、Var 的 :tag、:inline 展开后的形式，
   JVM 的 LocalBinding 同样取初值的类；只取引用类型，loop 不取）。`(let [a (object-array n)] (aset a i v))` 不再运行时决议（100 万次约 400 → 7 ms）；
   r7rs 的 `util/make-vector` 因此在 fft 里不再反射。
2. deftype / defrecord 的 `^long` / `^double` 字段编成 Int64 / Double 字段（数据结构贴近 CIL）：
   - 分析器的字段描述带原始类型，`HostSyntax.DefineStub` 的桩按同样的类型声明字段，外部 `(.-re a)` 静态决议到原始字段（expr-type :fl / :l64，原始读取）；
   - 构造器形参仍是 object（工厂、map->R、反射照旧），按原始签名的转换（PrimArgLong / PrimArgDouble）存；另有按字段类型的构造器，
     `(Complex. x y)` 的实参已知是 double 时静态决议选它、不装箱（桩上也有）；
   - 方法体里字段位置带原始类型：原始用途直接读字段，按值用时装箱，可变字段的 set! 先转换再存；推断把这些字段登记为固定类型（只对 deftype*，
     reify / proxy / 接口 / gen-class 的描述布局不同）。
   - mbrotZ 17.7 → 2.8 s（JVM 0.32；原来 77 倍）；`c+` 500 万次约 400 → 35 ms。
   - 与 JVM 的差别：map->P 给 ^long 字段 1.5 时 JVM 报错，cleat 按 longCast 截断（改动前 cleat 原样存 1.5，也不报错）。
- 语义：`data/primfield_semantics.clj` 与 `.out` 逐字相同，与 JVM Clojure 对照过（`ClojureCompilerTests.PrimitiveTypeFields`）。
- 验证（linux01，与 110 ~ 113 一起，一次完整流程）：种子不动点；Core 117 / Host 263 通过（1 个按环境跳过）；语料门禁 521 行；两套基线不变；
  bench 在噪声内（lazy_chain 48 → 16 ms）。r7rs 56 项 cleat 侧（各一次，对 JVM）：几何平均 3.74（109 后）→ 3.51（110 ~ 112 第一部分）→ **3.26**（含 112 第二部分与 113）。
- 这几个任务的改动在 codegen.clj、Util.cs 等文件里交织，按任务拆分要多跑几轮验证，所以一起验证、一个提交（与"每个任务一个提交"不同，有意为之）。
