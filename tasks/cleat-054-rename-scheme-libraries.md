# cleat-054 Scheme 库名更名

- 里程碑：后续　依赖：cleat-053　状态：完成
- 设计依据：[15 §1](../designs/15-scheme-core-vendoring.md)

## 工作内容
1. `(ironscheme …)` → `(cleat scheme …)`、`(boot …)` 保持；源码、`targets.sls`、构建脚本、测试同步。
2. 按 IronScheme 05 §5 重生成种子，不动点通过。

## 验收标准
- 仓库中不再出现 `ironscheme` 库名；两段不动点通过。

## 结果（2026-10-11）

1. `(ironscheme …)` → `(cleat scheme …)`：91 个文件、约 340 处库名引用（库定义、boot 源码的 import、构建脚本的 legend 与 import、C#/Clojure 侧注释、tests/scheme 的脚本）；`src/scheme/lib/ironscheme/` → `src/scheme/lib/cleat/scheme/`（psyntax 按库名定位文件）；构建脚本改名 `ironscheme-buildscript-nano.sps` → `buildscript-nano.sps`（build.cs、Cleat.Host.csproj、BootstrapTests、README 同步）。`(boot …)` 与 `(nanopass …)` 保持。
2. 种子按 IronScheme 05 §5 重生成：种子文本先做同形替换作桥接（install-library 的库名数据），Phase A 用新种子 + 新构建脚本重建 Scheme 镜像，再在镜像上重生成种子提交；两段不动点（Scheme 的 B == C 逐字节一致、Clojure 的 B == C）通过。

保留（非库名）：原语名 `ironscheme-version` / `ironscheme-runtime` / `ironscheme-test` / `ironscheme-build`，fasl 与 .ifc 的格式头 `(ironscheme-fasl 1 …)` / `(ironscheme-interface 1 …)`，nanopass 的 helpers 文件名约定 `implementation-helpers.ironscheme.sls`（快照内机制，"尽量不改 nanopass"），LICENSE/NOTICE 与 designs/、tasks/ 的历史文档。

## 验收标准
- 仓库中不再出现 `ironscheme` 库名；两段不动点通过。——通过（`dotnet test` 全绿 583 项；符合性门禁不变，Clojure 层对改名无感）。
