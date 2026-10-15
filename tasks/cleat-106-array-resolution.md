# cleat-106 数组访问的静态决议（array1）

- 里程碑：M10　依赖：cleat-101、cleat-102　状态：完成（2026-10-15）
- 设计依据：[20 §2](../designs/20-skeg-optimizations.md)；来源：skeg r7rs 的 array1（iron-079 量化过）移植到 Clojure 后的实测

## 背景
array1（建 0..n-1 的数组再倒序复制，100 万元素 × 50 轮）：`long-array` 版 10.7 s（JVM 0.25 s），`object-array` 版 16.5 s（JVM 170 s，JVM 在那里反射）。
分配采样：62 GB 里 `CallKey` 33%、`Type[]` 23%——`aset` / `aget` 全走 Reflector。原因：
1. `aset` 内联成 `RT.aset`，有 14 个按数组类型的重载；codegen 只把 long / double / 字面量的类型交给决议器，`^longs` 这样的引用类型提示在分析器之后就丢了。
2. 决议器把数组类型排除在可按名字引用的类型之外。
3. `aget` 内联成不分类型的 `RT.agetOnArray(Array, int)`，结果总是装箱。

## 工作内容
1. 分析器：宿主调用实参的静态类型是引用类型时包一层 `(clj-call "Tagged" #f 'T e)`；codegen 的决议读它，生成代码、类型推断、依赖里透明。
2. 决议器：接受一维数组（元素是原始类型、object、string 或 BCL / Core 的类型）；类型未知的实参不匹配数组形参（交给运行时，与改动前相同）；
   Core 类型多候选按精确相符数挑时，要求未知位置上各候选的形参一致（否则会挑错重载）。
3. 数组类型已知、元素是 long / double / object / bool / char 时，`agetOnArray` 改用类型化的 `RT.aget(T[], int)`（结果值相同；int[] 等不改，保留 Int32 结果）。
4. 静态决议的宿主调用在丢弃上下文里不装箱结果（`(aset …)` 在循环里每轮 box 一个 long 再 pop）。
5. bench 加 array1；测试 `ClojureCompilerTests.ArrayAccessIsResolvedStatically`。

## 验收标准
- array1 的 long-array 版接近 JVM；语义用例在 `CLEAT_NO_OPT=host` 与开两种设置下逐字相同；基线、门禁、全量不变；种子不动点。

## 结果（2026-10-15）

### 测量（linux01，Release；100 万元素 × 50 轮，分开计时）
| | `CLEAT_NO_OPT=host`（相当于改前） | 改后 |
|---|---|---|
| long-array 版 | 18.6 s | 0.41 s（JVM 本机 0.25 s 的量级） |
| object-array 版 | 23.0 s | 1.67 s |
| bench array1（100 万 × 10 轮，两版合计，3 轮 min） | 8250 ms | 398 ms |

（背景里的 10.7 s / 16.5 s 是本机的数字，linux01 慢一些；两列在同一台机器上对照。）

- 五项都做了。long-array 的复制循环编成 `RT.aget(long[], int) → long` / `RT.aset(long[], int, long)`，没有 `CallStatic`（`ArrayAccessIsResolvedStatically` 检查 Lcil）；
  `tests/Cleat.Host.Tests/data/array_semantics.clj`（各元素类型的读写与结果类型、越界、错误的值类型、无提示的数组、amap）在开 / 关两种设置下输出逐字相同；
  提示与实际类型不符时 castclass 抛 InvalidCastException（JVM 的 checkcast）。
- 收尾时补的一处决议器改动（类型未知的实参不匹配数组形参；Core 类型多候选按精确相符数挑时要求未知位置上各候选形参一致）改变了编出的代码：
  原先 16:13 生成的种子已不是不动点，重新生成后 boot 镜像里多 6 处运行时决议（原来被静态挑中的数组重载可能是错的）。
- 语料门禁的一行过期（split rejects a non-string：静态决议后按 castclass 抛 InvalidCastException，与 JVM 一致），从偏离表删去，522 行。
- 验证（linux01）：种子不动点（stage2 == stage3，从种子发射与 stage3 相同）；Core 117 / Host 256 通过（1 个按环境跳过）；
  语料门禁通过（522 行）；两套基线不变（cts 6866/31/6、test_clojure 3688/18/0）。
- bench（linux01，3 轮 min）：array1 398；其余与 cleat-102 后同量级（ctor 34、property 5、nested_arith 18、reduce_vec 4、untyped_loop 22），
  keyword_invoke 在 15 ~ 37 之间波动（与开关无关，噪声）。baseline.edn 是 M10 起点，不改写。
