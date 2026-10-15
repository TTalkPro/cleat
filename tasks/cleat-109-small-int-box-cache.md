# cleat-109 小整数装箱缓存与运行时数值的快路径（codegen 的 box、Num.Box、Numbers.num；equiv / ops / category / ConvertTo* / empty? / Truth.IsTrue）

- 里程碑：M11　依赖：cleat-108　状态：完成（2026-10-15）
- 设计依据：[21](../designs/21-r7rs-gaps.md)

## 工作内容
1. 依据：nqueens(12) 一次 cleat 分配 1289 MB、JVM 163 MB——JVM 的 `Long.valueOf` 缓存 -128 ~ 127，小整数装箱不分配；cleat 每次装箱 24 字节。
2. `Cleat.Core.Runtime` 加缓存（范围待量化，至少 -128 ~ 127，可放宽到 -128 ~ 1023：Clojure 不保证数的 identical?，与 JVM 不同的只是 128 以上也相同）；
   codegen 的 `box System.Int64` 改成调用它（JIT 内联：一次范围比较加数组读取），`Num.Box`、ClojureCLR 运行时里返回装箱 long 的路径（`Numbers.num(long)`、
   LongOps 的结果、`RT.box` 一类）同样改走缓存。
3. 量化分配与时间（nqueens、takl、primes、quicksort、`bench/`）；语义（`identical?` 对小整数为真，与 JVM 相同；其余不变）。

## 验收标准
- r7rs 受影响项与 `bench/` 前后数字及分配量；种子不动点。
- 基线、门禁、全量不变；改了 src/clojure 的种子不动点。

## 结果（2026-10-15）

### 剖析方法
`perf record -g` 配 `DOTNET_PerfMapEnabled=1`、`DOTNET_EnableWriteXorExecute=0`（关掉 W^X 双重映射，否则 JIT 代码落在 memfd:doublemapper 里、符号对不上）——
JIT 代码的归因准确；`dotnet-trace` 的线程时间采样在安全点上取样，会多算运行时辅助函数，不再用。脚本在会话的 scratchpad（prof.sh / callers.py），做法记进 designs/21。

### 实现
1. `Cleat.Core.Runtime.LongBox`：-128 ~ 1023 的 long 装箱取预先分配的对象（JVM 的 Long 缓存是 -128 ~ 127；Clojure 不保证数的 identical?，cleat 的 identical? 对值类型本来按值比较）。
   生成的代码在 `box System.Int64` 处改调 `LongBox.Get`（box-raw!、box-from-sig!、宿主调用的 Int64 结果、类型化入口），`Numbers.num(long)`、`Num.Box` 同。开关 `CLEAT_NO_OPT=boxcache`（只管生成的代码）。
2. nqueens 的 perf：`Object.GetType` 10.8%、`Util.equiv` 10%、`Numbers.ops` 7.2%——ClojureCLR 运行时先 GetType / Type.GetTypeCode 再分派。
   `Util.equiv(object, object)` 在引用相等之后先判 long / long、double / double；`equiv(object, long)` 等混合重载先判对应类型、不再装箱；`IsNumeric`、`Numbers.ops`、
   `Numbers.category`、`Util.ConvertToLong / ConvertToDouble` 最前面先判 long / double。结果与原来相同（同类比较，NaN 不等）。
3. `Truth.IsTrue` / `Truth.Bool` 加 AggressiveInlining（每个 if 都调用，剖析里 8.7%）。
4. `empty?` 改调 `Colls.IsEmpty`：nil、ASeq（总是非空）、LazySeq 先用类检查，再 Counted、seq——结果与 Clojure 1.12 的 `(if (counted? coll) (zero? (count coll)) (not (seq coll)))` 相同；
   原定义对没实现 Counted 的 Cons 要扫完整张接口表（counted? 占 4%）。

### 测量
- nqueens(12) 一次（本机）：1580 ms / 1289 MB / 103 次 gen0 → 645 ms / 245 MB（JVM 365 ms / 163 MB）。装箱缓存单独只快 7%（分配降到四分之一）：主因是运行时的类型分派。
- r7rs 56 项 cleat 侧（linux01，各一次，对 cleat-107）：**几何平均 4.52 → 3.74**；ntakl 59.1 → 14.0 s（JVM 1.17 倍）、takl 64.2 → 17.8（1.49）、nqueens 60.5 → 26.4（1.85）、
  triangl 17.9 → 7.4、primes 10.3 → 5.3、mbrot 0.75 → 0.44、maze 7.7 → 4.5、simplex 16.0 → 10.2、matrix 10.0 → 6.9；其余在噪声内。
- bench：sum_loop 9 → 0 ms（关掉生成代码的缓存时 9），其余在噪声内。
- 语义：`tests/Cleat.Host.Tests/data/boxcache_semantics.clj` 的输出与 `.out` 逐字相同（与 JVM Clojure 1.12 对照：只有类型名、异常消息与 CLR 按值的 identical? 不同），
  `CLEAT_NO_OPT=boxcache` 时也相同（`ClojureCompilerTests.BoxCacheAndNumericFastPaths`）。
- 语料：一行由失败变通过（"assoc of the held value is identity (array)"：assoc 已有的值返回同一个 map，小整数同一对象后与 JVM 一致），从偏离表删去（521 行）。
- 验证（linux01）：种子不动点；Core 117 / Host 全量除这一行的门禁外通过（1 个按环境跳过）；两套基线不变。
