# cleat-061 纳入 R6RS 测试套件

- 里程碑：后续　依赖：cleat-059　状态：完成
- 设计依据：skeg 的 `src/scheme/tests/r6rs/`（含提交 e02260a、6cd25cc 对它的改动）、d530d46（`bytevector-uint-set!` 修复与门禁整行匹配）、
  `tests/Skeg.Tests/ConformanceTests.cs` 的 `R6RS`；skeg `notes/R6RS差异笔记（排除call-cc）.md`。

## 背景

`(cleat-scheme-test)`（原 `ironscheme-test`）加载 `tests/r6rs/run.sps` 跑 R6RS 测试套件，但 cleat-046 纳入元层时没带上这个目录，
调用即失败。用户 2026-10-12 决定从 skeg 纳入，而不是删掉这个过程。

## 工作内容

1. 套件：skeg 的 `src/scheme/tests/r6rs/`（57 个文件）原样复制到 `src/scheme/tests/r6rs/`。来源是 Racket 的 `racket/r6rs`
   （`r6rs-test/tests/r6rs`），Apache-2.0 或 MIT 双许可（已核对 racket/r6rs 的 LICENSE.txt）；目录内 `LICENSE.txt` 与 `NOTICE` 说明来源。
2. 修复：初跑 `(cleat-scheme-test)` 为 12 of 8971 失败，其中 10 项是 `bytevector-uint-set!` 写入最高位为 1 的正数（如 2^128−3）
   时把补码的 0 符号字节算进长度——skeg d530d46 已修，cleat-058 对齐时没有带上。照搬（`boot/bytevectors.sls` +3 行），重生成种子。
3. 门禁：`tests/Cleat.Scheme.Tests/R6RSConformanceTests.cs`，镜像上运行 `run.sps`，整行匹配 `2 of 8971 tests failed.`。
   剩下 2 项是已知的文本端口缺陷（`output-port-buffer-mode`、`get-line` 对 `\r`），与 IronScheme、skeg 相同。

**不纳入**：skeg 的另外三套数值测试（trigtest、clisp-number-tests、fp-test），不在本次范围。

## 验收标准

- `(cleat-scheme-test)` 在 `src/scheme` 下可运行，结果为 2 of 8971 失败；门禁通过；种子不动点；全量测试通过。

## 结论（2026-10-12）

完成。`(cleat-scheme-test)` 在 `src/scheme` 下可运行（约 6 秒）：修复前 12 of 8971 失败，修复后 **2 of 8971**，与 skeg 的基线相同；
剩下的是两处已知的文本端口缺陷。

- **种子**：只改了 `boot/bytevectors.sls`，没有改名，一步重生成：现有镜像上生成新种子，新种子构建镜像，镜像上再生成的种子逐字节相同。
- **测试**：`dotnet test Cleat.slnx` 598 全部通过（Core 129、Scheme 234 含新的 R6RS 门禁、Host 234、Examples 1），含两段自举不动点与符合性基线。
