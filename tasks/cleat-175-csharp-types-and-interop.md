# cleat-175 类型定义、case、try、async、宿主互操作静态决议

- 里程碑：M20　依赖：cleat-174　状态：完成（2026-11-04；AOT 那条用例随 176）
- 设计依据：[30 §2 D2 / D4](../designs/30-csharp-compiler.md)；现行 `codegen.clj:2952-3356`（deftype / proxy / gen-class）、`codegen.clj:552`（host-call-layout）、`EmitInterop.cs`

## 工作内容

1. `clj-deftype` 与 reify、definterface、proxy、gen-class 在 `ConvertClosures` 里生成类描述：接口、公开 / 可变 / volatile 字段、
   `override` 与显式接口实现（`DefineMethodOverride`）、`IType` / `IRecord`、`getBasis`；单元内重名加 `__k`；运行时 `ClojureOps.DefineType` 的注册流程不变。
2. `clj-case`：`switch` + 哈希比较，形状同 Clojure `CaseExpr`。
3. `try-catch` / `try-finally` / `throw` / `monitor-enter` / `monitor-exit` / `escape*`。
4. async：`async` 方法属性（MethodImpl 0x2000）、`AsyncSupport.*`。
5. 宿主互操作：`HostCall` / `HostNew` / `HostField` / `InstanceOf` 在 `ChooseRepresentation` 里静态决议为 `MethodInfo` / `FieldInfo` / `ConstructorInfo`，
   决议不了的保留 `ReflectiveCall`（经 `Reflector`）；返回值转换规则同 LCIL.md §5（`void` → nil，`int` → `long`……）。

## 验收

- `tests/Cleat.Host.Tests/data/*_semantics.clj` 全部、`ClassGenerationTests`、`ValueBoundaryTests` 在 `CLEAT_COMPILER=cs` 下通过（仍在 Clojure 编译器编出的镜像上）。
- 反射警告的触发点与 Clojure 编译器一致（G1 语料上比较警告列表）。

## 实施记录（2026-11-04）

**产物**：`Lower/HostCalls.cs`（宿主调用静态决议：host-call-layout / host-call-signature / static-arg-type、没有变量类型表时的 expr-type 与类型格、
cg-static-host-call! 的全部收尾规则、原始值发射的基础档 `CgRaw`：常量 / 原始形参 / 原始签名调用 / 原始返回的宿主调用）；
`Lower/Types.cs`（deftype* / reify*：字段、两种构造器、方法、桩、meta / withMeta、getBasis、defrecord 的 create 与另两个构造器；definterface；
proxy-class*；gen-class*；await* 与 `^:async` fn 的 invokeAsync）；`CgPrimInvoke` 改为类型相符的实参原始求值（同 codegen.clj）。
修了两处发射问题：构造器查找不能带 Static（无参时与 .cctor 歧义）；`LowerLoops.Kids` 补 L2 的 box 节点（codegen 的 L3Some 复用它）。

**验收**
- `tests/Cleat.Host.Tests/data/*_semantics.clj` **21 个文件输出全部与 Clojure 编译器逐字相同**（`CLEAT_COMPILER=cs`）。
- `ClassGenerationTests` + `ValueBoundaryTests`（`CLEAT_COMPILER=cs`）148 / 149；剩下的 `ProxyAndGenClassAotThenFreshProcess` 要编 `*compile-files*` 包程序集，属 176。
- 反射警告的对比（任务书原写的 G1 语料）随 172 的分析器差分一起做（分析器还是 Clojure 的，警告也来自它）。
