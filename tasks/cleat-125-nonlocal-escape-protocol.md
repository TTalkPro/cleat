# cleat-125 非本地逃逸协议调研与原型

- 里程碑：M12　依赖：cleat-122　状态：未开始
- 设计依据：[22 §3 D9](../designs/22-gc-and-performance-followups.md)

## 工作内容
1. 前置门：cleat-122 完成后 fibc / ctak 仍由异常主导（throw 在其他栈帧，`leave` 变换覆盖不到）才开工；否则关闭本项。
2. 最小语义原型：逃逸返回特殊 marker 或线程局部 escape state；可能被穿过的调用点返回后检查 marker 并继续向外返回；编译器标注"可能逃逸调用"，不给所有调用加检查。
3. 协议对应语言层的构造（例如 cleat 提供的内建 `call-with-escape` 形式）：r7rs 的 `call-with-escape` 是基准自带的工具函数，
   编译器不能为识别它的写法而特判（designs/22 §0.1 第 3 条）。
4. 显式设计异步、动态绑定与多线程下的语义；`call/cc` 的完整语义不在范围（r7rs 移植只依赖逃逸续延）。
5. 收益上界用微基准锚定：CLR 同方法预分配 throw 1.8 ~ 2.0 µs，JVM 5 ns；fibc 18.9x、ctak 72x 的差距全部来自这里。

## 验收标准
- 原型上 fibc / ctak 与 JVM 的倍数有数量级改善，普通调用无 marker 检查开销。
- 语义开关开 / 关输出逐字相同；基线、门禁、全量不变；不进入默认生成路径则明确记录并保留原型。
