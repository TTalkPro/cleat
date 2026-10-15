# r7rs-benchmarks 的 Clojure 移植（cleat-107）

ecraven/r7rs-benchmarks 各项改写成 Clojure，同一份 `.cljc` 在 cleat（`:cljr`）与 JVM Clojure（`:clj`）上运行，比较两边的程序内部计时（不含启动与编译）。
结果与解读见 [tasks/cleat-107](../../tasks/cleat-107-r7rs-vs-jvm.md)。

- `src/r7rs/common.cljc`：原版 common.scm 的 `hide` 与 `run-r7rs-benchmark`（输出 `+!CSVLINE!+impl,name,secs`）。
- `src/r7rs/util.cljc`：宿主差异（数学函数、字符、StringBuilder、读文件）、逃逸续延、可变序对、可变向量。
- `src/r7rs/<名字>.cljc`：各项，命名空间 `r7rs.<名字>`，`-main` 从标准输入读 inputs。
- `deps.edn`：JVM 侧的 Clojure 版本（1.12.4）。

```sh
git clone https://github.com/ecraven/r7rs-benchmarks.git
export R7RS_BENCH=$PWD/r7rs-benchmarks          # 工作目录与 inputs 来自这里
./run.py cleat -r 3                              # 全部；或 ./run.py cleat fib tak
./run.py clojure -r 3
./compare.py --md                                # 每项取最小值，cleat / JVM 倍数与几何平均
```

cleat 用本仓库的 Release 构建（`dotnet build -c Release`，`CLEAT=<仓库>` 可改）；JVM 侧需要 `clojure` CLI 与 `java`。结果追加到 `results/<impl>.jsonl`（不入库）。
