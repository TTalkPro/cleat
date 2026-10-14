# cleat-089 codegen 二：生成类、AOT 常量与接口文件

- 里程碑：M8（阶段 A）　依赖：cleat-088　状态：完成
- 设计依据：[19 D5](../designs/19-clojure-self-hosting.md)、[cleat-042](cleat-042-performance.md)、[cleat-079](cleat-079-type-lattice-narrowing.md)

## 目标
codegen 的其余部分，Lcil 与 Scheme 编译器在全部单元上规范化后相同。

## 工作内容
1. ~~类型格与 `expr-type`、var-facts 收窄、l64 / 原始签名、宿主调用的类型、拆箱局部变量与 `cg-raw`、commonize~~、~~`clj-case`、`clj-static-call`、`^:async` fn~~
   （cleat-088 已做；推测循环与 071 收窄按普查不移植）。
2. `deftype*` / `reify*`、`proxy-class*`、`gen-class*`、gen-interface 的类生成。
3. AOT 单元：常量编码（`(constants …)`，Clojure 值与 Scheme 数据）；接口文件 `.ifc` 由驱动写（见结论）。

## 验收标准
- G2：boot 镜像、随附包、语料的全部单元 Lcil 规范化后相同。
- ILVerify 通过（`tools/build.cs ilverify`）。

## 结论（2026-10-14）

完成。

**移植**（`src/clojure/cleat/compiler/codegen.clj`）：
- `deftype-node!` 与 `deftype-class!`（deftype* / reify*：字段、构造函数、方法与原始类型参数的装箱、返回值转换、接口桩、reify 的 meta / withMeta、
  deftype 的 getBasis、defrecord 的另两个构造函数与 create）、`interface-class!`（gen-interface）、`proxy-class!`、`genclass-class!`，逐条照 Scheme 版。
  描述 d 仍是展开期算好的 Scheme 数据（cleat-092 换掉）。
- AOT 常量编码 `encode-constants`：Scheme 版 constants.sls 的共享 / 环（def / ref）、list / dlist，加上 (clojure compile) 的 `encode-clojure-constant`
  （var、long、kw、csym、cvec、map / set 按编码文本排序、sorted-*、clist、bigint、bigdec、ratio、regex、type、带元数据的 meta）。元素经新的
  `Cleat.Core.Compiler.RawData`（IFn）原样取出：Clojure 代码里宿主调用与字段读取会把 Int32 换成 long，而 fixnum 编码为 `(fix n)`、long 为 `(long n)`。
  Scheme 版在 canonical-gensyms（boot 镜像）时改写编码里的 gensym；Clojure 编译器的临时名本来就确定，不移植。
- 接口文件 `.ifc` 不在 codegen：Clojure 单元的接口由驱动（`Driver.WriteInterfaceFile` / `WritePackageInterface`，读 Lcore 与 Lcil）写，随驱动在 cleat-090 处理；
  `unit-interface` 只登记 Scheme 库的已知过程，Clojure 输入用不到。
- `pipeline/eval-form`：顶层的 `do` 照 Clojure 的 eval 逐个形式编译运行（definterface 之后的 import 能看到新类）。

**比较方法**
- 影子转储新增 `aot` 阶段：两边对每个 eval 形式再以 AOT 模式（常量编码）生成一次 Lcil；Scheme 一侧在转储路径里按需装入 (clojure compile) 并把它的
  外来常量编码器登记到 eval 用的那份 constants 库上；编不了的常量（DateTime、Guid、函数对象）两边都记为 `aot-error`（消息与类型名）。
- compiler-diff 的常量规范化：两个编译器合并相同常量与否取决于对象身份（Scheme 版经语法对象重建 Clojure 的符号与集合），下标可以不同而语义相同。
  aot 阶段把 `(ldconst k)` 换成第 k 个常量的编码再比较，内存模式的 lcil 阶段抹掉下标（内容由 aot 阶段比较）。影子单元的程序集名改为 `eval$sN`，
  不再与被引用的 Scheme 单元 `eval$N`（协议接口所在的程序集）混淆。

**验收**
- G2：`CLEAT_NO_INLINE=1 tools/build.cs ir-dump <目录> --shadow --corpus`，`--stage lcil --stage aot --stage aot-error --stage error`：
  26935 条记录 26931 条相同，4 条是 cleat-088 记下的 Int32 字面量（`gen-delegate`、Libs 的 `thread` 两个形式的 lcil 与 aot）。
  boot 镜像、随附包与 tools 包的整单元 Lcil 由各自的形式逐个比较覆盖（整单元的打包、镜像核对、预链接属于驱动，cleat-090 / 091 比较整单元）。
- ILVerify：Clojure 编译器以 AOT 模式编出的程序集（原始签名函数、多 arity、loop、try、case、deftype、reify、proxy、各种常量）全部通过。
- Host 测试：`CompilerDiffTests.ShadowCodegenMatchesSchemeLcil` 加入 defrecord / deftype / defprotocol / extend-type / reify / proxy / definterface 与各种常量，
  比较 lcil 与 aot；`ClojureCompiledFormsRun` 加入 deftype / defrecord / definterface / reify / proxy 生成的类的运行。

**顺带修正**
- `--compile-package` 编 `cleat.compiler.dll`（或 `Cleat.Clojure.Libs.dll`）时宿主先登记了旁边那个同名的旧包，require 从旧包加载，编出的新包没有形式
  （3.5 KB）；源码没变时连续编两次就会出现。正在写的包不再登记。

**已知**
- `CLEAT_SCHEME_NO_034=loops`（测量开关）下从源码加载 Clojure 编译器时，Scheme 编译器编 `cleat/nanopass/records.clj` 报
  "cannot assign captured variable"（关掉 lower-loops 的路径没人走）；正常有编译器包时不经过它。
