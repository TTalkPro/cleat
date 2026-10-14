# cleat-081 Clojure 自寄宿：设计与任务拆分

- 里程碑：M8　依赖：cleat-080　状态：完成
- 设计依据：[19](../designs/19-clojure-self-hosting.md)

## 背景
用户（2026-10-14）：已经完成从 Scheme 的 nanopass 启动自己的步骤；现在要移除 Scheme，用 Clojure 实现一个 nanopass，然后通过 Clojure 实现的 nanopass 自举。
三个前提问题的答复：移除范围 = 全部；信任根 = Lcil 文本种子；门控 = 分级门控。

## 工作内容
1. 调研现行工具链（nanopass 实际用到的特性、编译器各文件、Clojure 前端、生成代码对 Cleat.Scheme 的运行时依赖、构建流程、测试分层）。
2. 写 designs/19；拆出 cleat-082 ~ 094；旧路线的 cleat-023 ~ 029、cleat-056 标为由本里程碑取代；designs/07、15 加状态说明。

## 结论（2026-10-14）
完成。设计见 designs/19，任务见 TASK.md 的 M8。
