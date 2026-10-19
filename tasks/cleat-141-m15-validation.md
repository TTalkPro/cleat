# cleat-141 M15 收口验证

- 里程碑：M15　依赖：cleat-140　状态：完成
- 设计依据：[27 §6](../designs/27-jit-style-runtime-specialization.md)

## 问题

M15 的代码改动完成后，需要用最终 codegen 重建种子，删除只为旧种子保留的 `RuntimeSigFn`
兼容协议，并在压力模式、语义基线、自举不动点和 r7rs 三方基准上确认没有行为或明显性能回退。

## 收口清理

1. 删除 `src/Cleat.Core/Runtime/RuntimeSigFn.cs`。
2. `RuntimeSigSpec` 删除旧 `Compiler` 委托与 `Promote` 兼容入口；保留
   `SpecCompiler` / `Install` / `Compile` / `SeedCompiled` 这条 cleat-138 之后的新协议。
3. `codegen.clj` 删除旧 runtime sig type 引用；顺带修复 `link-runtime-calls` 中
   `uninitialized` 的 malformed match 输出为 `[(uninitialized) ir]`。
4. `RuntimeSigFnTests` 删除；新增 `RuntimeSigRetirementTests`，继续覆盖
   `RuntimeSigTable` 满表 LRU 逐出后程序集只保留弱引用、完整 GC 后不可达的诊断。
5. 六个 `src/boot/seed/*.gz` 用最终 codegen 重新生成。

## 验证

### 构建与运行时

- `dotnet build Cleat.slnx -c Release -v minimal -nologo`：0 warning / 0 error。
- Runtime 相关 Core 测试：5/5 通过。
- Host 特化测试 `SignatureSpecialization`、`WholeFunctionRuntimeSpecialization`：2/2 通过。

### 种子不动点

- `dotnet run tools/build.cs -- selfhost` 通过。
- stage2 与 stage3 全部产物 identical。
- 总耗时约 143 s。

### 压力基线

统一使用：

```sh
CLEAT_RTSPEC_THRESHOLD=1 \
CLEAT_RTSPEC_ALL=1 \
CLEAT_RTSPEC_SYNC=1 \
CLEAT_RTSPEC_STATS=1
```

| 基线 | 汇总 | 基线判定 | rtspec 统计 |
|---|---|---|---|
| conformance | 4842 rows：pass 4320 / fail 77 / error 444 / timeout 1；gate 521 classified divergences | `gate: OK` | compiled 13 / failed 0 / compile-ms 38 |
| CTS | 247 namespaces；assertions pass 6868 / fail 29 / error 6 | `baseline: OK` | compiled 8 / failed 0 / compile-ms 77 |
| test-clojure | 357 tests；assertions pass 3688 / fail 18 / error 0 | `baseline: OK` | compiled 3 / failed 0 / compile-ms 34 |

fail / error 与既有分类基线一致；三类基线的运行时特化均无编译失败。

### r7rs 三方重跑

所有性能测量只在本机执行；三方为本机 cleat、OpenJDK 25 C2 和 GraalVM CE 25。以下关键基准
使用 `bench/r7rs/run130.py normal 3 fib tak ack`，每项取三次内部计时的最小值，单位秒。

#### default

| 基准 | cleat | C2 | GraalVM | cleat / C2 | cleat / GraalVM |
|---|---:|---:|---:|---:|---:|
| ack | 2.089 | 3.325 | 7.598 | 0.628 | 0.275 |
| fib | 2.235 | 2.140 | 3.804 | 1.044 | 0.588 |
| tak | 1.245 | 0.971 | 1.212 | 1.282 | 1.027 |
| **几何平均** | | | | **0.944** | **0.550** |

#### `CLEAT_NO_OPT=sigspec`

| 基准 | cleat | C2 | GraalVM | cleat / C2 | cleat / GraalVM |
|---|---:|---:|---:|---:|---:|
| ack | 2.036 | 3.344 | 6.380 | 0.609 | 0.319 |
| fib | 2.089 | 2.143 | 3.808 | 0.975 | 0.549 |
| tak | 1.200 | 0.971 | 1.196 | 1.236 | 1.003 |
| **几何平均** | | | | **0.902** | **0.560** |

#### 压力模式

| 基准 | cleat | C2 | GraalVM | cleat / C2 | cleat / GraalVM |
|---|---:|---:|---:|---:|---:|
| ack | 1.966 | 3.298 | 7.123 | 0.596 | 0.276 |
| fib | 2.126 | 2.138 | 3.807 | 0.994 | 0.559 |
| tak | 1.228 | 0.969 | 1.202 | 1.267 | 1.021 |
| **几何平均** | | | | **0.909** | **0.540** |

单独探针中，压力模式每个进程分别编译 2 个特化：

| 基准 | compiled | failed | compile-ms |
|---|---:|---:|---:|
| fib | 2 | 0 | 19 |
| tak | 2 | 0 | 19 |
| ack | 2 | 0 | 22 |

default 与 `CLEAT_NO_OPT=sigspec` 的同一探针均为 compiled 0 / failed 0；三组的
`+!CSVLINE!+` 结果标签与实参规模一致，只有计时段不同。

完整 56 项 normal 三方数据另行重跑，规模为 56 × 3 轮 × 3 实现 = 504 条，无错误。其最小值
几何平均为 cleat / C2 = 1.720、cleat / GraalVM = 1.824、GraalVM / C2 = 0.943。关键项的
cleat 最小值相对旧窗口为 ack +6.1%、fib +5.6%、tak +3.8%；随后 default 关键项 7 轮复测为
fib min 2.217 s / median 2.235 s，tak min 1.240 s / median 1.245 s，ack min 1.960 s /
median 2.019 s。ack 已回到旧值附近，fib / tak 保持约 4% 偏高；压力模式不低于 default，
且无失败或语义差异，本轮不视为 M15 收口回退。

## 结论

Release 构建、运行时测试、Host 特化语义测试、自举不动点、三套压力基线与 r7rs 三方重跑均通过。
旧 `RuntimeSigFn` 协议已可在最终种子下删除；M15 收口完成。
