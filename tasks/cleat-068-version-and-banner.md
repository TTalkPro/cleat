# cleat-068 版本号与 banner

- 里程碑：后续　依赖：cleat-062、cleat-063　状态：完成

## 背景

用户发现（2026-10-13）：nREPL 报的、neorepl 显示的版本是 `Clojure 1.13.0-SNAPSHOT`——`RT.cs` 里从 ClojureCLR 照抄的
`version=1.13.0-SNAPSHOT`，看不出是 cleat；宿主 REPL 没有 banner，`--version` 是 `cleat 0.1.0-dev`。用户定：cleat 的版本是 **1.0.0**，
`*clojure-version*` 报 **1.13.0**（core.clj 跟的是 ClojureCLR master，已含 `:added "1.13"` 的函数），banner 是
**`Cleat 1.0.0 (Clojure 1.13.0)`**。

## 工作内容

1. `CoreInfo.Version`（cleat）= `1.0.0`，新增 `CoreInfo.ClojureVersion` = `1.13.0` 与 `CoreInfo.Banner`；`RT` 的版本属性取 `ClojureVersion`（唯一来源）。
2. `clojure.core/*cleat-version*`（与 `*clojure-version*` 同形的 map）与 `(cleat-version)`，相当于 ClojureScript 的 `*clojurescript-version*`。
3. 宿主：交互式 REPL 启动先打 banner（输入被重定向时不打，脚本化输出不变）；`--version` 打同一行。
4. nREPL 的 `describe` 在 `:versions` 里多报 `:cleat`；neorepl 的 banner 对 cleat 服务显示 `(Cleat 1.0.0, Clojure 1.13.0)`。
5. `Cleat.Runtime` 包的缺省版本随之为 `1.0.0`；tools.build 的 `pack` 缺省依赖 `Cleat.Runtime >= 1.0.0`。

## 结论（2026-10-13）

完成。nREPL `describe` 现在报 `{:cleat "1.0.0", :clojure "1.13.0", :clr "10.0.x", :nrepl "1.8.0"}`，neorepl 连上显示
`neorepl connected to … (Cleat 1.0.0, Clojure 1.13.0)`，`cleat --version` 与交互式 REPL 打 `Cleat 1.0.0 (Clojure 1.13.0)`。
`NuGetColdLoadTests` 打的运行时包版本改为 `1.0.0`（SemVer 里 `1.0.0-任何标签` 低于 `1.0.0`，满足不了 `>= 1.0.0` 的依赖）。

顺带处理的两个测试问题（全量测试里冒出来的，都不是版本改动本身引起的）：

- **符合性门禁**：语料行 `"seq / lazy over infinite" "eduction reduces"` 的结果取决于 `find-protocol-impl` 在 `(supers c)`（按对象标识哈希的
  `Type` 集合）里先遇到 Eduction 的哪个接口（`IEnumerable` 返回 9，`IReduceInit` 强转 `IReduce` 抛异常，JVM 上抛异常），镜像一重建就可能翻转
  （jolt 的表早标了 `:flaky`）。偏离表的条目可以标 `:flaky true`，门禁不再把它算作过期；这一行这样登记（`:host/hash`）。
- **neorepl 的 repl 测试**：测试终端经 `TextWriter/Synchronized` 写，读屏幕却直接 `(str out)`，与写入竞争（`StringBuilder.ToString` 越界）；
  改为在同一把锁（包装对象本身）里读。banner 的测试补一条：显示 `(Cleat <版本>, Clojure <版本>)`。
- `sessions_test` 的 `ensure!-at-the-same-time-test` 在一次全量运行里偶发失败一次（10 个并发会话里有一个不在缓存里），单独重跑 6 次都通过，原因未查明，留作观察。
