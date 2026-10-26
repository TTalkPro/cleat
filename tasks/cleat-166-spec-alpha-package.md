# cleat-166 clojure.spec.alpha 包

- 里程碑：M19　依赖：无　状态：未开始
- 设计依据：[29 §1 第 3 条 / §7](../designs/29-usability-and-engineering.md)、[18](../designs/18-tools-build.md)

## 问题

`(require '[clojure.spec.alpha :as s])` 报 FileNotFoundException。spec 是很多库的依赖，没有它，移植 cljc 库会卡在第一步。

## 工作内容

1. 新建 `packages/spec.alpha`（cleat 项目，与 nrepl / neorepl 同样的结构）：以上游 `clojure/spec.alpha`、`core.specs.alpha` 为蓝本，
   宿主差异参照 ClojureCLR 的 clr.spec.alpha。
2. `test.check` 生成器部分：spec 的 `gen` 依赖 test.check，先以延迟加载的形式支持（与上游一致，没有 test.check 时 `gen` 报错）。
3. 上游测试全部移植（同 cleat-062 的做法），失败项逐条归类。
4. 裁决 core 的宏 spec（`defn` / `let` / `ns` 的 :args 检查）是否默认启用：量宏展开时间和镜像构建时间，
   检查错误黄金文件的变化，再决定。
5. 打 NuGet 包，README 写依赖方式。

## 验收

- 上游测试通过率与失败分类写进任务书；`s/def`、`s/valid?`、`s/conform`、`s/explain`、`s/fdef` + `instrument` 可用。
- 宏 spec 裁决有数据支撑；启用时 CTS / test_clojure / conformance 基线的变化列清楚。
