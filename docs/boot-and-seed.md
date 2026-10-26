# Boot 与 seed

本文记录 `src/boot/cleat.boot.dll` 的两条生成路径，以及 `src/boot/seed/` 里的信任根是什么。

## 日常构建：seed → stage1 → boot

入口在 `src/Cleat.Host/Cleat.Host.csproj`，默认 `dotnet build` 会执行三步。

1. **编译 C#**

   产出 `Cleat.Core.dll`、`cleat-emit.dll`、`Cleat.Host.dll`。

2. **发射 seed**

   `EmitSeedStage1` 调：

   ```sh
   cleat-emit --seed src/boot/seed -o src/Cleat.Host/obj/seed-stage1
   ```

   `cleat-emit` 解压 `src/boot/seed/*.gz`，把 `.lcil` 发射成同名 DLL：

   ```text
   cleat.boot.lcil.gz     -> cleat.boot.dll
   cleat.compiler.lcil.gz -> cleat.compiler.dll
   ```

   `.ifc` / `.inl` 是旁路文件，用于后续编译、接口核对和内联。

3. **编译 boot 与编译器包**

   `GenerateBootImage` 让宿主加载 stage1 的 boot 和 compiler，按 `src/boot/boot-order.edn`
   从 `src/clojure/` 加载 `:boot true` 的命名空间。每个顶层形式经过 analyzer、nanopass
   pipeline、codegen，合并成一个程序集：

   ```text
   src/boot/cleat.boot.dll
   src/boot/cleat.boot.lcil
   src/boot/cleat.boot.ifc
   src/boot/cleat.boot.inl
   ```

   `GenerateCompilerPackage` 先编译改名版 `cleat.compiler0`，再用它编当前编译器源码，
   产出 `src/boot/cleat.compiler.dll`。

日常命令：

```sh
dotnet build Cleat.slnx -c Release
```

只编译 C#、不生成 boot：

```sh
dotnet build Cleat.slnx -c Release -p:BuildBootImage=false
```

## 从 0 自举：stage2 → stage3 → seed

`tools/build.cs` 的 `selfhost` / `clojure-seed` 走完整自举链。

1. 用当前 `src/boot/cleat.boot.dll` 作为第 1 级。
2. 编 stage2：

   ```text
   stage2/cleat.boot.dll
   stage2/cleat.compiler.dll
   ```

3. 编译改名版工具编译器 `cleat.compiler0`；避免编译器在同一进程里重编自己。
4. 用 stage2 与改名版编译器再编一遍，得到 stage3。
5. 验证不动点：

   ```text
   stage2 == stage3
   ```

   要求 boot / compiler 的 DLL、Lcil、接口与内联文件逐字节一致。
6. `clojure-seed` 把 stage3 的 Lcil、接口文件、内联表 gzip 写回 `src/boot/seed/`，
   再把 seed 发射一遍，与 stage3 逐字节比较。

命令：

```sh
dotnet run tools/build.cs -- selfhost      # 只验证不动点
dotnet run tools/build.cs -- clojure-seed  # 自举并重写 seed
```

## seed 是什么

`src/boot/seed/` 是**自举不动点产物的 Lcil 快照、接口文件和内联表**，也是日常构建的信任根。

| 文件 | 内容 |
|---|---|
| `cleat.boot.lcil.gz` | boot 程序集的 Lcil 文本 |
| `cleat.boot.ifc.gz` | boot 的接口文件 |
| `cleat.boot.inl.gz` | boot 的内联 / 跨单元元信息 |
| `cleat.compiler.lcil.gz` | 编译器包程序集的 Lcil 文本 |
| `cleat.compiler.ifc.gz` | 编译器包的接口文件 |
| `cleat.compiler.inl.gz` | 编译器包的内联表 |

seed 的作用是把一次性的多级自举冻结下来，让日常构建不再重复自举：

```text
seed -> stage1 -> src/clojure -> src/boot/cleat.boot.dll
```

改了 `src/clojure` 或编译器后，必须用 `clojure-seed` 重生成 seed；否则 `src/boot`
与 seed 发射出的 stage1 不一致，`BuildFromTheSeedReproducesTheSeed` 会失败。
