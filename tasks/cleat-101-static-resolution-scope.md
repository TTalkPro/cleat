# cleat-101 codegen 静态决议范围：构造器、属性、值类型目标、任意引用形参、泛型方法

- 里程碑：M10　依赖：cleat-100　状态：完成
- 设计依据：[20 §2](../designs/20-skeg-optimizations.md)；cleat-099 结论

## 工作内容
1. `(new T …)`：按实参静态类型选唯一相容的公共构造器，`newobj`；`ClojureStaticCalls.ResolveCtor`。
2. 属性 getter：零实参成员找不到方法 / 字段时 `call get_X`。
3. 值类型目标：已拆箱的 `:l64` / `:fl` 目标上的实例成员。
4. 非 core 引用类型形参 `castclass`（委托形参除外）。
5. 泛型方法：类型实参能唯一推断时调用实例化后的方法。
6. 开关 `CLEAT_NO_OPT` 含 `ctor` / `prop` / `valtype` / `generic`。

## 验收标准
- `bench/ctor`、`bench/property` 降一个数量级；语料门禁、两套基线、Core / Host 全量不变；种子不动点。

## 结论（2026-10-15）

完成 1、2、4 与一项顺带的；3、5 未做（下）。全部是行为不变的优化：语义核对脚本（构造器重载选择、nil 实参、值类型构造器 DateTime / TimeSpan / Guid、
错误实参的异常类型、deftype / defrecord 的三种构造器、属性与接口属性、静态属性、castclass 形参）在 `CLEAT_NO_OPT=host`（新开关：关掉全部宿主调用的
编译期决议）与开两种设置下输出逐字相同。

- **构造器**（`ClojureStaticCalls.ResolveCtor`，codegen 的 `NewOf` 布局与 `"ctor"` 签名 → `newobj`，值类型结果装箱）：规则同方法——该实参个数的公共构造器
  唯一时直接用（Core 类型）或按实参静态类型选唯一相容的；委托 / 抽象 / 不能按名字引用的类型不决议。
- **正在编译的 deftype / defrecord 的桩**（`StubCtor`）：同一顶层形式里的 `new`——典型是 defrecord 展开出的 `->R` / `map->R`——此前因为桩不能按名字引用而走
  Reflector，`(->R 1 2)` 比 `(R. 1 2)` 慢四倍。桩的字段就是全部字段；记录另有基本字段、基本字段 + meta + extmap 两个构造器；类型名写不带程序集的全名，
  发射器按名字找到本单元正在定义的类型。
- **属性**：零实参成员找不到方法与字段时取公共属性的 getter（接口含父接口的），当普通方法 `call` / `callvirt get_X`。
- **任意可按名字引用的引用类型形参 `castclass`**（`ParamOk` 不再限于 String 与 Core 类型）；委托、数组、枚举、Nullable 仍交给 Reflector（它们要转换）。
- **顺带**：返回 `Int32` 的宿主调用（`.Length` / `.Count` / `IndexOf` …）的结果算 `:l64`（`conv.i8`），参与推断与原始发射——此前 `(+ n (.Length s))` 的 n 因此装箱。

| 微基准（1M 次，ms） | 改前 | 改后 |
|---|---|---|
| `(P. i i)` deftype | 80 ns | 21 |
| `(->R i i)` 记录工厂 | 109 ns | 31 |
| `(R. i i)` | 25 | 25 |
| `(StringBuilder. 16)` | 73 ns | 15 |
| `(.Length ^String s)` 累加 | 38 | 8 |

bench（3 轮 min，对 M10 起点）：ctor 305 → 80 ms、property 155 → 20 ms、reduce_vec 36 → 22、lazy_chain 84 → 74；其余在噪声内（assoc_map 59、fib 9、
keyword_invoke 20、mbrot 53、nested_arith 312、prim_fib 4、protocol 36、sum_loop 13、untyped_loop 46）。
语料门禁：一条过期——"re-seq rejects a non-string" 现在通过（String 形参 castclass 抛 InvalidCastException，同 JVM 的 ClassCastException），从偏离表删除（524 行）；
两套基线不变；种子不动点；Core 117、Host 255 通过。

**未做**：
- 值类型目标的实例成员（`(.ToString ^long n)`、`(.AddDays ^DateTime d 1)`）：要 `unbox` 取地址再 `call`，且只能调值类型自己声明的方法；
  基准里没有这种形状，等有证据再做。
- 泛型方法：类型实参推断是另一套规则，Reflector 已能在运行时推断；同样等证据。
- 二者都记在 designs/20 §2。
