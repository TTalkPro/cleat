# 逃逸续延、try / catch / finally 与异常开销（2026-10-16）

> 背景：r7rs 对比里 fibc（JVM 的 18.9 倍）、ctak（72 倍）是最慢的两项（数字见 tasks/cleat-116、cleat-117 的 linux01 结果）。
> 本文记录原因分析、JVM Clojure 编译器对 try 的处理，以及"把同一方法内的逃逸编成跳转"的可行性。

## 1. fibc 的移植写法

原版是 Kent Dybvig 用 `call/cc` 写的斐波那契；`call/cc` 只作逃逸（续延只在捕获它的动态范围内被调用），
移植版（bench/r7rs/src/r7rs/fibc.cljc）用 `util/call-with-escape` 模拟：

```clojure
(defn addc [x y k]
  (if (zero? y)
    (k x)                          ; 交出结果 = 调用续延
    (recur (succ x) (pred y) k)))

(defn fibc [x c]
  (if (zero? x)
    (c 0)
    (if (zero? (pred x))
      (c 1)
      (addc (u/call-with-escape (fn [c] (fibc (pred x) c)))
            (u/call-with-escape (fn [c] (fibc (pred (pred x)) c)))
            c))))
```

`call-with-escape`（bench/r7rs/src/r7rs/util.cljc）：

```clojure
(def ^:private escape-ex (ex-info "escape" {}))   ; 预先分配，避免每次构造异常记录调用栈

(defn call-with-escape [f]
  (let [box (object-array 2)]
    (try
      (f (fn [v] (aset box 0 v) (aset box 1 box) (throw escape-ex)))
      (catch clojure.lang.ExceptionInfo e
        (if (and (identical? e escape-ex) (identical? (aget box 1) box))
          (aget box 0)
          (throw e))))))
```

每次递归开一层 try/catch，每个结果（`(c 0)`、`(c 1)`、addc 末尾的 `(k x)`）都是一次 throw + catch。
基准时间几乎全部是异常处理，加法本身可以忽略。ctak 同理，抛得更多。

## 2. 为什么 .NET 上慢这么多

- CLR 的 `throw` 总是走完整的异常机制：第一遍逐帧查找匹配的处理器（逐个检查 catch 类型），第二遍展开栈并执行沿途的 finally，
  同时更新异常对象的栈信息。即使异常对象预先分配、throw 与 catch 在同一个方法里，每次也要几微秒。
- JVM 上同样的代码快，主要靠 HotSpot：预分配的异常没有构造开销；内联之后若 `athrow` 与匹配的处理器落在同一个编译单元，
  C2 直接把抛出编成跳到处理器的代码，不走栈展开。.NET 的 JIT 不做这种变换。

## 3. JVM Clojure 编译器怎样处理 try（~/workspace/clojure，src/jvm/clojure/lang/Compiler.java）

`TryExpr`（第 2677 行起）、`ThrowExpr`（第 2959 行起）都是直接翻译，编译器本身不做任何异常相关的优化。

### 3.1 解析
- 只在返回位置直接编译 try；否则包成 `((fn* ^:once [] (try …)))` 立即调用：
  ```java
  if(context != C.RETURN)
      return analyze(context, RT.list(RT.list(FNONCE, PersistentVector.EMPTY, form)));
  ```
  原因：进入异常处理器时 JVM 操作数栈被清空，表达式中间（栈上已有别的值）不能开始 try。代价是一次闭包分配与调用。
  CIL 有同样的约束（进入 `.try` 时求值栈必须为空）。
- try 体在 `NO_RECUR` 下解析：`recur` 不能跨过 try。
- catch / finally 体在 `IN_CATCH_FINALLY` 下解析（局部变量清除在这些区域里更保守）。

### 3.2 代码生成：finally 复制到每个出口
```
startTry:  <try 体> → retLocal
endTry:    <finally 副本 1>; goto ret
catch_i:   astore e; <handler> → retLocal; <finally 副本>; goto ret     ; 每个 catch 一段
finally:   astore finallyLocal; <finally 副本 N>; aload finallyLocal; athrow   ; 捕获任何异常
ret:       aload retLocal
```
异常表：每个 catch 覆盖 `[startTry, endTry)`；finally 的"任意异常"处理器覆盖 try 体和每个 catch 体。
JVM 字节码没有真正的 finally（`jsr` / `ret` 已淘汰），所以 finally 体被复制到正常出口、每个 catch 出口和异常出口。
CIL 有真正的 `.finally` 区域，`leave` 离开 try 时由运行时执行，只生成一份。

### 3.3 throw
```java
excExpr.emit(C.EXPRESSION, objx, gen);
gen.checkCast(THROWABLE_TYPE);
gen.throwException();   // athrow
```

cleat 的对应：codegen 用 CIL 原生的 `.try` 区域，try-catch 的处理器体内联进 catch 块（cleat-092）。

## 4. "只在本函数内使用的逃逸"编成跳转

### 4.1 思路
throw 与接住它的 catch 在同一个方法里、且编译期能确定一定被这层接住时，不必真的抛 CLR 异常：
用 `leave` 直接跳出 try（沿途 finally 照常执行，不经异常机制）。

```
原来                                  改写后
.try {                                .try {
  ...                                   ...
  box[0] = v                            result = v
  throw escape-ex                       leave ESCAPED        ; 普通跳转
}                                     }
catch ExceptionInfo {                 catch ExceptionInfo { ... }   ; 保留，处理别处抛来的异常
  if (e == escape-ex && box[1] == box)
    result = box[0] else rethrow     ESCAPED:
}                                       ...使用 result
```

成立条件：
- throw 点在 try 体的词法范围内、同一个方法里（例如 try 体里直接调用的局部逃逸函数，经内联后它的 throw 落在本方法）；
- catch 的判断在编译期为真（抛的是已知对象，`box[1] == box` 对本层恒成立）；
- 同一方法内嵌套的其他 try/catch 能静态判断不会截获它。

这正是 HotSpot 在内联后做的事；.NET JIT 不做，只能由 cleat 在生成 Lcil 时自己完成。

### 4.2 对 fibc / ctak 帮不上忙
逃逸函数 `c` 被当作参数传给另一个函数（递归调用的 fibc、addc），throw 发生在别的栈帧里。
要变成同一方法内的跳转就得把那个函数内联进来，而 fibc 是递归的，无法完全内联。

### 4.3 其他办法
- 返回值协议：逃逸时返回特殊标记（结果另存），可能被逃逸穿过的调用点返回后检查标记、继续往外返回。fibc 会快很多，
  但编译器要知道哪些调用可能被穿过；判断不了时只能给所有调用加检查，拖慢普通代码。
- 结论：同一方法内的"逃逸变跳转"代价不大，可覆盖 try 体内直接抛已知异常、被本层 catch 接住的情况
  （更正：Clojure 核心库的提前退出都不用异常——`reduced` 是包装值、由 reduce 检查，`some` 等是循环——所以核心库里基本没有这种模式，
  受益面很可能接近零）；fibc / ctak 仍按宿主固有差距对待。
  若要实现，先统计编译器里这种模式出现的次数再决定。
- 更新（2026-10-17，cleat-125）：已采用保守标注的返回值协议。语言层 `(escape* (fn [k] …))` 分配 owner；只对 `^:escape`
  形参的已证明直接调用点返回 marker，其余读法回退到异常协议，普通调用不加 marker 检查。保护区里从受保护块返回受限，
  统一用 marker 异常走旧协议。fibc / ctak 分别约 31x / 50x；跨线程与超出动态范围的续延不在本协议范围内。

## 5. `escape*` 的用法与多层返回协议

### 5.1 用法

基本形式：

```clojure
(escape* (fn [k] body))
```

进入表达式时创建一个单向逃逸续延 `k`；在动态范围内调用 `(k v)` 会让整个 `escape*` 表达式的值变成 `v`，之后的 body 不执行。
不调用 `k` 时，`escape*` 的值就是 body 的正常返回值。

跨函数传播要写成 `fn*` 并标注形参（当前 `defn` 会剥掉形参元数据）：

```clojure
(def target
  (fn* [x ^:escape k]
    (if (= x :escape)
      (k :escaped)
      [:normal x])))

(def pass
  (fn* [x ^:escape k]
    (target x k)))

(escape* (fn [k] (pass :escape k)))
;; => :escaped
```

`k` 只应在 `escape*` 的动态范围内调用；保存到外部、跨线程、跨异步边界或把它当作可重入 `call/cc` 使用都不在本协议内。

### 5.2 多层退栈

快速路径不是真正的跨栈跳转，也不是保存/恢复调用栈，而是协作式 marker 返回链：

```text
(k v)
  → EscapeInvoke(owner, v) 返回 EscapeMarker(owner, v)
  → 调用点 EscapePropagate 看到 marker
  → 当前方法立刻 ret / throw marker
  → 逐层穿过已证明调用点
  → escape* 边界 EscapeUnwrap 拆本层 marker
```

对上面的 `pass` / `target`：

```text
escape* owner O
  └─ pass
      └─ target
          └─ (k :escaped)
              returns Marker(O, :escaped)
      ← pass 看到 marker，立刻返回
  ← EscapeUnwrap(O, marker) 返回 :escaped
```

物理栈仍然逐帧返回；区别是中间帧用普通返回值把 marker 送出去，不走 CLR 异常展开。因此只有编译器能证明的路径启用这套协议：

- 对 `:escape` 局部的单参直接调用；
- 直接把 `:escape` 局部传给 Var 声明为 `:escape-params` 的指定参数位置。

未证明路径经 `EscapeFallbackOf` 回到旧异常协议：owner 被普通调用时 `EscapeOwner.invoke` 抛 marker，由 `escape*` 边界的
`EscapeCatch` 拆自己的 marker。这样普通调用不加 marker 检查。

### 5.3 owner / marker 与嵌套边界

`EscapeOwner` 是本次 `escape*` 执行的身份；`EscapeMarker` 携带 owner 和逃逸值。`EscapeUnwrap` 只拆 owner 引用相同的 marker：

```csharp
if (ReferenceEquals(m.Owner, owner))
    return m.Value;

throw m;   // 别人的 marker 继续向外传播
```

所以嵌套情况下：

```clojure
(escape*
  (fn [outer-k]
    (escape*
      (fn [inner-k]
        (outer-k :from-inner)))))
;; => :from-inner
```

`(outer-k :from-inner)` 产生外层 owner 的 marker；内层边界发现不是自己的 owner，rethrow；外层边界匹配后返回值。

非尾位置不能把 marker 当普通值继续计算，因此 `EscapePropagate` 在尾位置 `ret` marker，在非尾位置 `throw` marker。
`try / catch / finally` 保护区里 CIL 不能从受保护块 `ret`，改走 `EscapePropagateProtected`，用 marker 异常穿过保护区；
`escape*` 自己的 catch 再拆本层 marker。

## 6. 为什么 JVM 的 try / catch / finally 通常代价更低

CLR 的异常模型面向多语言统一语义、确定性 finally / fault 清理、异常过滤、managed/native interop 和运行时服务，
异常路径采用通用两遍式查找与栈展开。正常路径很便宜，但 throw 通常要经历处理器查找、栈展开和保护区语义处理；
JIT 一般不把同方法 throw/catch 优化成普通跳转。

JVM/Clojure 编译器本身不高级很多：JVM 字节码也没有真正的 finally，通常把 finally 复制到正常、catch 和异常出口。
关键差别在于 HotSpot 对特定形态有低成本变换：预分配异常避免构造成本；内联后如果 `athrow` 和匹配 handler 落在同一个
编译单元，C2 可把 throw 编成跳到 handler 的普通跳转。旧协议里预分配 `escape-ex` 的 fibc 正处在这种形态，所以同方法
throw/catch 在 JVM 上约 5 ns，而 CLR 上约 1.8–2.0 µs。跨方法异常仍会走 JVM 通用异常机制；这个数字不代表所有 JVM
异常都便宜。

这也是 cleat-125 不继续压异常、而引入 `escape*` marker 返回协议的原因：在热逃逸路径上避免 CLR 通用异常展开，
只在证明失败或保护区回退时才依赖旧异常协议。
