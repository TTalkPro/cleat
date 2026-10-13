# cleat-075 文本端口的行尾与缓冲模式（移植 skeg iron-074）

- 里程碑：后续　依赖：cleat-061　状态：完成
- 来源：skeg 提交 e34373d、12dfdaf（iron-074，`~/workspace/skeg/Skeg/tasks/iron-074-textual-port-eol-buffer-mode.md`）

## 背景

用户（2026-10-13）：skeg 的 iron-07x 完成了性能优化，另外解决了 get-line 的 eol-style 问题，移植到 cleat 的 Scheme 层。
范围经确认为 skeg 的 M10、M11 全部（iron-061 ~ 074，iron-075 只有任务书不移植）按依赖顺序分几个任务移植，Chez mats（iron-072）一并纳入。
本任务是第一个：iron-074 与编译器无关，先做。

cleat-061 纳入 R6RS 套件时留下 2 of 8971 的基线，正是 iron-074 修的两处：

- `get-line` 用 .NET 的 `TextReader.ReadLine`，把 `\r` 也当行尾；转码端口的输入行尾转换做在 ReadLine / ReadToEnd 之后，
  而且用的是输出方向的换行串，逐字符读取不转换。R6RS 8.2.4 / 8.2.9：只有带转码器、eol-style 不是 none 的端口在输入时把
  CR、CRLF、NEL、CRNEL、LS 转成换行符，`get-line` 只认换行符。
- `output-port-buffer-mode` 按底层流猜（文本端口总是 `line`），不是打开时给的模式。

## 做法

skeg 与 cleat 同源（都是 IronScheme 改名），这几个文件只差几十行。按三方合并移植：基为 skeg 的 d40adef（iron-074 之前），
theirs 为 12dfdaf，名字按 `Skeg.Core` → `Cleat.Scheme` 等改写后 `git merge-file` 进 cleat 的文件。

- `R6RS/IO.cs`：`TranscodedReader` 改为继承 `TextReader` 的块缓冲实现（字节缓冲 + Decoder 直接解码进字符缓冲，字节序标记照
  StreamReader），每取一块就地把行尾改写成 LF，跨块的 CR 由两态状态机接上；`NonBufferedTranscodedReader`（双向端口）在逐字符
  读取上转换；`open-input-file` 用原生转码器的 `TranscodedReader`（原来是 `File.OpenText`）。
- `Runtime/Strings.cs`：`get-line` 自己按 LF 分行。
- `R6RS/PortPrimitives.cs`：端口 → 缓冲模式的 `ConditionalWeakTable`，新原语 `$port-buffer-mode`；`open-file-*-port` 记下给的模式
  （省略时 `block`），`transcoded-port` 继承，标准输出 / 错误为 `line`，其余 `block`；`none` 真的不缓冲；非法缓冲模式与转码器报错；
  关闭后的自定义端口读写报错，重复关闭只调一次 close 过程。
- `Reader/ReaderPrimitives.cs`：读源码时的文件名也认 `TranscodedReader`。
- `boot/io/ports.sls`：`output-port-buffer-mode` 用 `$port-buffer-mode`；`boot/generic-writer.sls`：`write` 把 NEL、LS 与其余控制字符、
  DEL 写成 `\x…;`（字符写成 `#\x…`），原样写出会被转码端口读成换行符。两个构建脚本登记新原语。
- 冲突三处：`IO.cs` 的类注释、`generic-writer.sls` 的 `write-string-char`（skeg 早先把字符串转义抽成了函数，一并采用）、
  `primitives.ss`（skeg 的表已是 iron-069 的格式，按 cleat 的格式补 `$port-buffer-mode`，PrimGen `--check` 一致）。

## 测试

- R6RS 套件 **8971 项全部通过**（原来 2 of 8971 失败）：门禁 `R6RSConformanceTests` 改为整行匹配 `8971 tests passed`，`src/scheme/README.md` 同步。
- 新目录 `tests/scheme/base/`（boot 库的语义回归，照 skeg 的 `src/scheme/tests/*.sps`；`SchemeScriptTests` 登记，测试名 `base/…`）：
  `r6rs-edges.sps` 先纳入 iron-074 的 40 项（字符串端口与各 eol-style 的 `get-line` / `get-char` / `get-string-all` / `lookahead-char`、
  跨 4096 字符块的 CR LF、关闭后读取、`open-input-file`、crlf 输出、NEL / LS 的写出、各打开方式的缓冲模式、关闭后的自定义端口）。
  其余各组随 iron-069 / 072 的移植加入。脚本失败时退出码为 1。
