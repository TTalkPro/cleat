# cleat-092 生成代码去 Scheme

- 里程碑：M8（阶段 D）　依赖：cleat-091　状态：完成
- 设计依据：[19 D8 第二条](../designs/19-clojure-self-hosting.md)

## 目标
编出的代码与 Clojure 编译器本身不再引用任何 Cleat.Scheme 类型。

## 工作内容
1. try / catch：处理器内联为代码，不再编成 `Procedure` 闭包；不再重抛 `ContinuationException`。
2. "无泛型参数"占位由 `'()` 改为 null（`ClojureOps.CallStatic` / `CallInstance` 同步）；顶层不用 `StaticProcedure`；去掉 `RT.Box(int)`。
3. 常量表：Cleat.Core 提供 Clojure 数据的序列化（取代 `Cleat.Scheme.Fasl.ReadPackedTable`），`Cleat.Emit/EmitConstants.cs` 改接它；`.inl` 改存 L0 的 Clojure 数据。
4. Lcil 头 `(runtime scheme)` → `(runtime clojure)`；`Cleat.Emit` 去掉 `SchemeData.cs` 的依赖。
5. 裁掉 L0 中只为 Scheme 存在、普查确认不会出现的节点与对应 codegen；前端不再产出 Scheme 痕迹。
6. 按 D7 两步法重生成种子。

## 验收标准
- `cleat.boot.lcil`、`cleat.compiler.lcil` 与包程序集中没有 `Cleat.Scheme`；G4 不动点；全量测试与基准不退化。

## 结论（2026-10-15）

完成。编出的代码（镜像、编译器包、用户的包程序集）与编译器包本身只引用 Cleat.Core 与 clojure.lang；`CLEAT_COMPILER=scheme` 的对照模式照旧（Scheme 编译器与
它的运行时 cleat-093 删）。

**生成代码**（`cleat.compiler.codegen`）
- try / catch：处理器内联进 catch 块（异常存进处理器形参的局部变量，体在 catch 里生成），不再编成 Scheme `Procedure` 闭包；处理器引用的外层变量在 try 体里
  钉住（异常可能在体清掉它们之后抛出），各处理器互为分支（局部变量清除同 if 的两支）。Scheme 的过程类、共享持有类（`$SP` / `$P<k>`）、commonize、
  已知过程的直接调用、`procedure-form` / arity 常量、`ContinuationException` 的重抛随之全部删去；到 codegen 的 lambda 只有 fn*（其余报错）。
- 无泛型参数的占位 `'()` → nil（`ClojureOps.CallStatic / CallInstance` 本来就接受 nil）；`RT.Box(int)` → `box System.Int32`；`Cleat.Scheme.Box` →
  新的 `Cleat.Core.Runtime.Box`；`Unspecified` → nil；前向变量的 `$uninitialized` / `$check-initialized` 原语 → L0 节点 `(uninitialized)` /
  `(check-initialized e)`，运行时是 `ClojureOps.Uninitialized` / `CheckInitialized`。
- 常量表：只有 Clojure 的值（Int32 编码为 `(fix n)`、double 为 `(double …)`；按值合并的是数、字符、字符串、关键字、布尔）；`(runtime clojure)`，
  `(references "Cleat.Core")`。发射器按单元的 runtime 选解码器：clojure 用新的 `Cleat.Core.Runtime.Fasl.ReadPackedTable`，不再隐含引用 Cleat.Scheme；
  `.inl` 也由 `Cleat.Core.Runtime.Fasl` 写读（`[:cleat/l0 子句]`）。
- deftype* / reify* / proxy / gen-class 的描述、`clj-case` 的表、fn 名字记录里的原始签名都改成 Clojure 向量（分析器产出、codegen 读）。
- L0 ~ L3 删去 Scheme 专有的节点（gref / gset! / mref / mset! / prim / primcall / library / and / or / break / escape-frame / try-handle / host-* /
  unsupported）与各 pass 里对应的子句；lower-loops 只剩 let / 命名 let 的降级。`cleat.compiler.dump` / `shadow`（依赖 Cleat.Scheme 的 IrDump）移出编译器包，
  影子模式从源码加载。与 Scheme 编译器输出相等的 G1 / G2 测试删去（两个编译器的输出从此有意不同）。
- 发射器：类型名在已加载程序集里找不到时按命名空间前缀在受信平台程序集里找（`System.Diagnostics.ProcessStartInfo` → System.Diagnostics.Process.dll）——
  原来靠 Cleat.Scheme 的依赖把这些框架程序集带进来，独立运行的 `cleat-emit --seed` 没有它们。

**两步法**（designs/08 §0）：先加 Core 的新入口（Box、Fasl、Uninitialized / CheckInitialized）与解码器选择，旧种子的 stage1 照旧发射、运行，`dotnet build`
用它编出新编译器；`tools/build.cs clojure-seed` 自举两级（stage2 == stage3）重生成种子（1.76 MB，原 1.98 MB，里面没有 Cleat.Scheme）；再删旧入口
`Cleat.Core.Compiler.Fixnum`。从新种子 `dotnet build` 得到的镜像与编译器包与种子的 stage1 逐字节相同。

**验收**：ILVerify 对镜像与编译器包通过；基准（`tools/build.cs bench -r 5`）与 Scheme 编译器时期持平（assoc_map 49 ms、fib 9、keyword_invoke 18、lazy_chain 83、
prim_fib 4、protocol 34、reduce_vec 33、sum_loop 12）；编编译器包 20 s（原 26 s，没有 Scheme 过程类）；linux01 上默认模式与 `CLEAT_COMPILER=scheme`
的全量测试见提交前的运行。Core 测试 `FaslTests.CoreFaslReadsClojureTablesAndRoundTripsValues`。

**遗留到 cleat-093**：`Cleat.Emit/SchemeData.cs` 与 `(runtime scheme)` 的互操作、`Cleat.Scheme.Clojure.IrDump` 与影子模式、`tools/compiler-diff.clj`、
`CLEAT_COMPILER=scheme` 都随 Scheme 一起删。
