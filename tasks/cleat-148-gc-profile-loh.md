# cleat-148 GC profile 可声明化与 LOH 初始化评估

- 里程碑：M16　依赖：无　状态：完成（2026-10-20）
- 设计依据：[28 §4](../designs/28-hot-path-optimizations.md)

## 问题

server GC + gen0 256MB 让 array1 -63%、gcbench / nboyer / fft / dynamic -18 ~ -27%，
但 deriv / cpstak 有回归（22 §0.2 两机实测）→ 维持"不改默认值"的结论，
把配置做成可声明的批处理 profile。另外 array1 的 `Object[]` 占其分配 40%，
LOH 大数组的初始化写入走常规 `stelem.ref`（带写屏障）值得评估。
测量环境：192.168.3.111 只有 .NET runtime 10.0.0（本地 10.0.12），跨轮比较前先对齐。

## 工作内容

1. GC profile 声明入口：`cleat.edn` 的 `{:gc {:mode :server :gen0-budget "256MB"}}`、
   CLI `--gc-profile batch` 或 env 旋钮三选一（与 17 / 18 的宿主配置路线对齐），
   在进程启动最早期通过 `runtimeconfig` / AppContext 生效；默认值不变。
2. bench/r7rs 脚本加统一开关，跑 GC 敏感项时用同一 profile，结果注明配置。
3. LOH 初始化：数组填充路径评估无屏障写入（`spans` / 直接指针）；
   先做 micro 对照（分配 + 填充 1M 元素 Object[]），有收益再进 codegen / RT。
4. 远端 runtime 对齐：192.168.3.111 装 10.0.12 或本地降测，记录到 notes。

## 验收

- 默认配置下所有基线数字不变（profile 未声明时零影响）；声明后 array1 复现 -60% 量级、
  deriv / cpstak 的回归在文档中注明。
- LOH 路径：micro 对照有可测收益才落地；落地后 array1 同窗口 A/B 不回退，语义用例逐字相同。
- 门禁与两套基线不变；改 codegen 后种子不动点。

## 结论

1. **入口选择环境变量，不在 `cleat.edn` / CLI 里改 GC。** CLR 在托管 `Main` 前完成 GC 初始化；
   `cleat.edn`、`Options.Parse` 和进程内 `Environment.SetEnvironmentVariable` 都太晚。2026-10-20 本机
   .NET 10.0.12 最小探针显示：`DOTNET_gcServer=1`、`DOTNET_GCgen0size=0x10000000` 在启动前设置时，
   `GCSettings.IsServerGC=true`、`GCGen0MaxBudget=268435456`；托管代码启动后写同样环境变量，两者仍分别是
   `false` / `12582912`。要按 `cleat.edn` 动态选择只能再 spawn 子进程并给它不同的启动环境 / runtimeconfig，
   增加一层进程与启动成本；因此本轮不做。
2. **实现**：`CLEAT_GC_PROFILE=batch` 只是显式批处理别名；宿主 `Main` 最早期校验声明，且要求父进程已经设置
   `DOTNET_gcServer=1` 与 `DOTNET_GCgen0size=0x10000000`。随后读 `GCSettings.IsServerGC` 和
   `GC.GetConfigurationVariables()["GCGen0MaxBudget"]` 防止“声明了但没生效”。不声明、`default`，
   或 `CLEAT_NO_OPT=gcprofile` 退回时不设置 / 不校验 GC。非法值和未生效都 exit 2；usage 文本保持不变。
3. 远端批测脚本可直接用：

   ```sh
   CLEAT_GC_PROFILE=batch \
   DOTNET_gcServer=1 DOTNET_GCgen0size=0x10000000 \
   dotnet src/Cleat.Host/bin/Release/net10.0/cleat.dll -I bench/r7rs/src -m r7rs.array1
   ```

   本机进程内读数：off = `server=false gen0=12582912`，batch = `server=true gen0=268435456`
   （.NET 10.0.12，本机 Debian / i9-13900H，非 192.168.3.111）。

## LOH 判定

**不做无屏障写入路径。** array1 的 `create-x` / `create-y` 是逐元素不同的新装箱 long 写入 1M 元素 `Object[]`；
这是 old LOH 数组 → 年轻代引用，写屏障正是维护卡表 / generational 正确性的机制。跳过它需要 GC 停顿、
批量预标卡表或 unsafe raw memory 写，前者不暴露给托管代码，后两者会漏对象 / 破坏 GC 不变量。
`Span<object>` / managed pointer 不是无屏障：本机 .NET 10.0.12 JIT disasm 中，常规 `a[i]=v` 循环调用
`CORINFO_HELP_ASSIGN_REF`，`Span<object>` 与 unsafe pointer 循环调用 `CORINFO_HELP_CHECKED_ASSIGN_REF`。
1,048,576 refs × 20 rounds、7 次取最小的隔离填充对照（值数组预分配，只隔离写入；本机、非 192）：
default normal 48.60 ms / span 51.74 ms / pointer 48.31 ms；server+gen0 256MB normal 52.84 ms /
span 77.36 ms / pointer 68.55 ms。另测 pinned POH `GC.AllocateArray<object>(..., pinned:true)`：
default 58.83 vs normal 106.72 ms，server 66.10 vs 135.38 ms，也无收益。故保持常规 `stelem.ref`；
若未来做，只能是证明“同值 bulk fill”时的专用 runtime 批量 barrier 路径，不适用于 array1。

## 验证

- Release 构建：0 error，13 warning（基线同样是 13；新增代码 0 warning）。
- Core 全量：123 passed / 0 failed。
- Host 全量第一次在并行 cleat-144 / 147 / 149 同机运行时** Failed 5 / Passed 277（共 282 = 基线 278 + 新增 4）**，
  五个失败项与归类（详证见 `notes/m16-regression-triage.md` §6 第 1 条）：
  - `SelfhostReachesAFixedPoint`：`module not found: /tmp/cleat-clojure-bootstrap/stage2/cleat.boot.dll`
    —— `ClojureBootstrapTests.s_root` 是**固定共享目录**，另一个 worktree 同时在自举会互相覆盖 / 删除 dll。
  - `DirectLinkingAcrossUnitsAndWithRedefs`：`runtime hook 'load-source' is not registered (no boot image loaded?)`
    —— 同一类并发压力（四个构建 + 两个基准进程在 4 ~ 8 负载的机器上抢内存 / 页缓存）。
  - `Nrepl` 与 `BothFromPackageAssemblies`：报的是 `nrepl.util.out_test` 里
    `InvalidCastException: String -> Number`（栈在 `Numbers.ops` / `Num.SlowDivide`）—— nREPL transport
    端口与**捕获的 stderr 跨进程串台**，字符串是从别的进程漏进来的；cleat 自身没有这条路径。
  - `ClojureTestSuiteMatchesItsBaseline`：`Could not find a part of the path '.../tests/clojure-test-suite/test'`
    —— `tests/clojure-test-suite` 是 submodule，worktree 不自动 checkout（`git submodule update --init` 后可跑）。
  本轮按 149 的纪律不再在 worktree 里重跑 Host 全套：**收口证据由合并后的 main 串行跑一次全套提供**
  （Release 构建 + Core + Host 282 + gate + CTS + test-clojure + 种子不动点）。
  新增 `GcProfileTests` 4 passed；error golden 与新增测试同跑 41 passed。
- 本机 main ee9bf41 对照窗口：conformance `pass:4320 fail:77 error:444 timeout:1`，
  `gate: 521 classified divergences, 521 failing rows`，`gate: OK`；本任务同窗口输出相同。
- CTS：main `6868/29/6`、本任务 `6868/29/6`，均 `baseline: OK`。
- test-clojure：main `3688/18`、本任务 `3688/18`，均 `baseline: OK`。
- `git status src/boot/seed` 干净；未改 `src/clojure`、`bench/r7rs`、`notes/r7rs-performance.md` 或 Core 运行时库。

## 基准脚本接入点

- `bench/r7rs/gc_bench.py`：在 `CLEAT_CONFIGS` 加一条
  `('cleat-batch', {'CLEAT_GC_PROFILE': 'batch', 'DOTNET_gcServer': '1', 'DOTNET_GCgen0size': '0x10000000'}, [])`；
  与现有 `cleat-ws` 同窗口对照。不要只设 `CLEAT_GC_PROFILE`，两个 `DOTNET_*` 必须由父进程设置。
- `bench/r7rs/run.py`：`command()` 返回的是 argv；在 `run()` / `__main__` 里按新 `GC_PROFILE=batch` 环境组装
  `env = dict(os.environ, CLEAT_GC_PROFILE='batch', DOTNET_gcServer='1', DOTNET_GCgen0size='0x10000000')`
  并传给 `subprocess.run`，结果 label 记录该 profile。手工对照直接用上面的三变量前缀即可。
- 完整回退：unset `CLEAT_GC_PROFILE`、`DOTNET_gcServer`、`DOTNET_GCgen0size`；`CLEAT_NO_OPT=gcprofile`
  只关闭本任务的声明校验，不等于清除父进程已经设置的原生 `DOTNET_*` GC 配置。
