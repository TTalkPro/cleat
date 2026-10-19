# cleat-138 单一签名 key 与 RuntimeSigTable

- 里程碑：M15　依赖：cleat-137　状态：完成
- 设计依据：[27 §2 ~ §3](../designs/27-jit-style-runtime-specialization.md)

## 问题

cleat-133 ~ 136 的 `RuntimeSigFn` 按精确 `Type[]` 逐项比较，所有引用形参都参与守卫；未命中用 `null` 表示，
特化函数返回 `nil` 时通用体会再跑一遍；每个候选的 `invokeStatic` 都包着 `TypedBailout` 的 try / catch。
运行时生成类的通用 `invoke` 跑的是"Object 形参的类型化体"，`invokeStaticTyped` 只给自调用用，
而自调用的守卫是 `isinst rtSpec 类`——Var 的值是原函数类，守卫永远失败，递归每层都回到查表入口。

## 工作内容

1. `RuntimeSigKey`：14 个类型码（`long` / `double` / `int` / 原生数组 / `String` / `Object[]`，0 为其他），
   每形参 4 位拼成一个 `long`，最多 16 参；`GetType()` 精确比较，不受 CLR 数组协变影响。
2. `RuntimeSigTable`：条目 `(key, IFn, guarded)` 线性比较；没有相同 key 时选最具体的**兼容**条目
   （条目 0 槽位通配）并缓存为别名；未命中返回哨兵 `RuntimeSigTable.Miss`；R 模式条目的 `TypedBailout`
   在表里转成 `Miss`；失败 key 记负表；每函数 16 条（含别名），LRU 逐出（先别名）并登记程序集弱引用。
3. `RuntimeSigSpec`：`Threshold` 改为可配（`CLEAT_RTSPEC_THRESHOLD`，缺省 128）；`Install` 建表并按当前实参
   生成首个条目（key 为 0 返回 null，入口永久回通用体）；`Compile` 与 `generate-unit` 共用一把锁，运行时补生成
   只 TryEnter，同线程编译单元内不补生成；`SpecCompiler` 是新回调，旧 `Compiler` / `RuntimeSigFn` 只为旧种子保留。
4. codegen：
   - `spec-param-mask`：形参（含 let / jump 副本）作为宿主 / 原语调用或 case 的操作数出现才进 key；
     掩码为 0 的函数不成为候选。
   - `RuntimeSigEntry '[n 掩码]`：入口无 try / catch，按 `Miss` 回通用体。
   - 运行时类 `rtSpec$N` 的通用 `invoke` 改为 `RtSpecEntry`：直接拆箱调用 `invokeStaticTyped`；
     类型化体在 `generate-unit` 里按签名固定形参类型后推断。
   - `TypedSelf` 增加身份守卫：Var 的值 `==` 编译时传入的通用函数对象（live 常量）就直调特化类；
     返回 `O` 时不经 `TypedBailout` 转换。
   - 编译回调按掩码内类型码固定形参：`Int64` / `Double` 原始，数组 / `String` `Tagged`，其余 object；
     返回的有效 key 只含真正固定的槽位。

## 验收

- `sigspec_semantics.clj` 在默认（阈值 16）、`CLEAT_NO_OPT=sigspec`、`CLEAT_NO_OPT=typed`、压力模式（阈值 1）
  四种设置下输出逐字相同；压力模式生成 9 个特化，`aget` 决议到 `Int32[]` / `Int64[]` / `Double[]` 重载，
  自调用直调 `rtSpec` 的 `invokeStaticTyped`。
- `RuntimeSigTableTests`（4 项）：类型码与打包、nil 结果不是未命中、兼容条目与别名、R 模式放弃与负表。
- `SignatureSpecialization` 更新：入口调用 `Install` 与 `Miss`、无 `TypedBailout`；rt6 只有 `a` / `n` 是 `Int64`。
