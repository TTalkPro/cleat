# Boot 镜像的生成

本文记录 `src/boot/cleat.boot.dll` 怎样生成、怎样验证。cleat-182 起编译器是 C#（`src/Cleat.Compiler`，[designs/30](../designs/30-csharp-compiler.md)），
仓库里没有种子、没有自举：镜像由编译器从 `src/clojure` 的源码直接编出。cleat-091 ~ 181 的旧做法（Clojure 编译器、文本种子
`src/boot/seed/`、`selfhost` / `clojure-seed` 两级自举）见 [designs/08](../designs/08-bootstrap.md) 与 [designs/19](../designs/19-clojure-self-hosting.md)（历史）和 git 历史。

## 日常构建：源码 → stage1 → boot

入口在 `src/Cleat.Host/Cleat.Host.csproj` 的 `GenerateBootImage`，默认 `dotnet build` 执行：

1. **编译 C#**：`Cleat.Core.dll`、`Cleat.Compiler.dll`、宿主 `cleat.dll` 等。
2. **从零编 stage1**：宿主不加载任何镜像（`--no-boot`），编译器按 `src/boot/boot-order.edn` 从 `src/clojure/` 逐个加载
   `cleat.kernel`、`clojure.core` 及其余 `:boot true` 的命名空间，每个顶层形式经分析器、前中端各遍、L5、ILGenerator 发射并执行，
   最后合并成一个程序集 `obj/cs-stage1/cleat.boot.dll`。这一遍 `core.clj` 早期的几处 `:inline` 用不了（inliner 要的 `concat` 还没定义），
   所以 stage1 与最终镜像不同。
3. **在 stage1 上再编一遍**：产出

   ```text
   src/boot/cleat.boot.dll
   src/boot/cleat.boot.pdb
   src/boot/cleat.boot.ifc     接口文件：已知函数、类名、参数个数（直接链接与内联用）
   src/boot/cleat.boot.inl     可内联子句（L0）
   ```

编译器的输出与起步镜像无关（只要起步镜像是完整的），所以第二遍就是最终结果。按输入增量，约 12 秒。

```sh
dotnet build Cleat.slnx                            # 缺省 Release，含镜像
dotnet build Cleat.slnx -p:BuildBootImage=false    # 只编译 C#
```

随附库 `Cleat.Clojure.Libs.dll`（`:boot false` 的命名空间）与 `src/boot/tools/` 不在 `dotnet build` 里：
`dotnet run tools/build.cs -- compile-libs` / `compile-tools`（`pack` / `publish` 会先做）。没有它们时这些命名空间从源码加载。

## 确定性（G5）

- **构建两次相同**：从零两步编出的镜像（含 `.ifc` / `.inl`）与 `dotnet build` 的逐字节相同；
- **不动点**：在 `src/boot` 的镜像上再编一遍，结果与它逐字节相同；
- 两台机器、两个 .NET 补丁版本编出的镜像也相同（MVID 按内容哈希）。

`dotnet test` 的 `DeterminismTests`（`Category=Determinism`，PR CI 的确定性守卫）验证前两条，并用重编的镜像跑冒烟集与 ILVerify。
整套产物（镜像、随附库、tools、packages 下的包）的确定性与 ILVerify（G3）：

```sh
dotnet run tools/build.cs -- image-set --twice --ilverify    # 编到 src/boot-cs/ 与 src/boot-cs.2/ 并逐文件比较
```
