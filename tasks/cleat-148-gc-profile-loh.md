# cleat-148 GC profile 可声明化与 LOH 初始化评估

- 里程碑：M16　依赖：无　状态：未开始
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
