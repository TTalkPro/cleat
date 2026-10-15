# cleat-100 性能测量基建：分配采样、装箱探针、基准补充

- 里程碑：M10　依赖：cleat-099　状态：完成
- 设计依据：[20 §0 / §7](../designs/20-skeg-optimizations.md)；skeg `tools/alloc.cs`（直接搬）

## 工作内容
1. `tools/alloc.cs`：从 skeg 搬来，改成在本进程里跑 cleat 宿主（`Cleat.Core.Runtime.Startup` + 脚本），按类型汇总 `GCAllocationTick`，给出各代 GC 次数与总分配量。
2. `bench/` 补：`untyped_loop`（初值来自无提示参数的 long / double 循环）、`nested_arith`（无提示参数的嵌套算术）、`ctor`（deftype / record / BCL 构造）、
   `property`（属性读）、`mbrot`（skeg r7rs 的 mbrot 形状，Clojure 写）。README 表与 `baseline.edn` 更新（`bench --write`）。
3. 现状数字记入 designs/20 §0。

## 验收标准
- `dotnet run tools/alloc.cs -- script.clj` 能给出分配表；新基准进 baseline。

## 结论（2026-10-15）

完成。
- `tools/alloc.cs`：从 skeg 搬来并改成在本进程里跑 cleat 宿主。两处与 skeg 不同：宿主与 Cleat.Core / cleat-emit / Cleat.Hosting 都要在缺省加载上下文里
  `LoadFromAssemblyPath`（`Assembly.LoadFrom` 另开上下文会让 Cleat.Core 被加载两份，生成代码的 `$Constants` 初始化失败）；编译器包的常量表按名字找
  `Cleat.Emit.LcilEmitter`，而本进程的 `AppContext.BaseDirectory` 不是宿主目录，所以先把宿主目录的程序集都加载进来。`--first-chance` 打印每个异常（排错用）。
- 基准新增 untyped_loop、nested_arith、ctor、property、mbrot；`bench --write` 写成 M10 起点的基线（数字见 designs/20 §0）。
- 采样结论：nested_arith 94% 是 Double 装箱；ctor 的分配几乎全是 Reflector 的 CallKey / Type[] / 实参数组——cleat-101、102 的目标明确。
- 装箱探针（按站点计数）没有单独做：分配采样按类型已经够定位，需要按站点时再加。
