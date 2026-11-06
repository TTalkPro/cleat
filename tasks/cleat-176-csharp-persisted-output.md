# cleat-176 持久化输出：镜像、包、接口文件、PDB

- 里程碑：M20　依赖：cleat-175　状态：完成（2026-11-04；"在 C# 镜像上跑 REPL"一项 172 后已验）
- 设计依据：[30 §2 D4 / D6 / D7](../designs/30-csharp-compiler.md)、[17](../designs/17-lazy-runtime-startup.md)；现行 `driver.clj` 的 compile-boot / compile-package / build-interface-file

## 工作内容

1. `compile-boot`：逐形式求值并收集 L0（后序）、每个形式 `RT.setNextID(n×100000)`、关闭新内联决策与反射警告；`PersistedAssemblyBuilder` 写 dll。
2. `compile-package` 与 `*compile-files*`：`CheckBootImage`、`Load<i>` 入口、`CleatPackage` / `CleatNamespace` 属性、`(lazy-start)`、嵌入 `cleat.ifc` / `cleat.inl`。
3. `.ifc`：Var → 类名、元数、HasStatic、镜像 MVID；`.inl`：`inline-clauses` 的规则与大小上限（160 / 400）、访问器特化（accinline 的部分可先只写出不读入）。
   格式与现在逐字节兼容（30 D6）。`register-eval-inlines!` 的 C# 版。
4. PDB 与序列点（cleat-158 的规则：行变化时打点、每个方法记源文件、`CLEAT_DEBUG_EVAL` 内存 PDB）。
5. MVID / 时间戳回填与确定性措施（08 §7）。
6. `Cleat.Host` 的 `--compile-boot` / `--compile-package` 在 `CLEAT_COMPILER=cs` 下走 C# 编译器。

## 验收

- C# 编译器编出 `cleat.boot.dll`，在它上面 REPL 与 `-e` 可用；ilverify 通过。
- 同一输入编两次逐字节相同（G5）。
- C# 编译器写的 `.ifc` / `.inl` 能被 Clojure 编译器读入，反之亦然（双向加载一个包程序集验证）。

## 实施记录（2026-11-04）

**产物**
- `Emit/ConstantEncoder.cs`：常量表（codegen.clj encode-constant 与 Cleat.Emit FaslEncoder 合一，对象 → fasl；哈希 map / set 按编码文本排序保证确定）。
- `Emit/IlEmitterPersist.cs`：`IlEmitter.Save`（PersistedAssemblyBuilder → Portable PDB、嵌入资源、契约程序集改引用、MVID / 时间戳按 SHA256 回填）与常量表的 RVA 初始化数据；
  `Emit/ContractRetargeting.cs` 从 Cleat.Emit 复制（cleat-182 删 Cleat.Emit 时这里是唯一一份）。
- `Driver/CsDriverPersist.cs`：`DriverState`（单元 / boot / package）、load-hook 的镜像与包分支、load-built（*file* / *source-path* / 源文件绑成加载名）、
  compile-boot（每形式重置 id、关新内联与反射警告）、compile-package / `*compile-files*`（CheckBootImage、Load&lt;i&gt; 入口、CleatPackage / CleatNamespace 属性、lazy-start、
  嵌入 cleat.ifc / cleat.inl）、接口文件（fn-node / fn-arities / 候选筛选）、可内联子句（inline-ok? / arith-only? / prim-sig-wrap，格式同 Clojure 编译器）、
  eval 的 register-eval-inlines!。借用的 Clojure 分析器的三个开关（inline-calls? / reflection-warnings? / restrict-loop-inlines?）与 `DriverOps.State` 的 :package 同步设置。
- 发射修正：所属类型指向本单元的引用（限定名、TypeBuilder）一律按 ClassDecl 查（TypeBuilder 在 CreateType 前不能反射）；`IsInstanceOf` / `KnownCall` 按类型名引用
  （编包时解析到本单元重新定义的同名类型，而不是 eval 时的动态程序集）；外部字段的 `volatile.` 只由 codegen 显式发（之前重复两次，ilverify 报错）。

**未做**：cleat-145 的访问器特化（accinline，默认关）只留了位置；`CLEAT_DUMP_LCIL` 没有对应物（用 `CLEAT_DUMP_IR`）。

**验收**
- `CLEAT_COMPILER=cs` 用 stage1 镜像编出完整的 `cleat.boot.dll`（19 个条目，约 10 s）；**ilverify 全部通过**；**同一输入编两次 dll / ifc / inl / pdb 逐字节相同**（G5）。
- C# 写的 `cleat.boot.inl`（859 个子句）由 Clojure 编译器的 `np/parse` 全部读入；读方向（Clojure 写、C# 读）在 171 已验证。
- `ProxyAndGenClassAotThenFreshProcess`（`*compile-files*` 编两个包，新进程加载运行）在 `CLEAT_COMPILER=cs` 下通过的手工复现：输出与期望相同。
- **已验（2026-11-04，172 / 179 / 180 合并后）**：C# 编译器 + C# 分析器编镜像约 8 s，两次逐字节相同；在其上 `-e "(+ 1 2)"` 与管道 REPL（defrecord、^long 循环、require clojure.string）正常，不加载 Clojure 编译器包。
