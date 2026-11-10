# 发布清单（cleat-188，1.2.0 起沿用）

产物由 `~/release188.sh`（13 上，cleat-188）一步做出：`artifacts/release-<版本>/`（不进仓库）。推送与 tag 由维护者执行。

## 1. 门禁

- [ ] 本地全量：Core / Compiler / Host（`dotnet test -c Release`）、`image-set --twice --ilverify`、`conformance --run --gate`、`cts cts`、`cts test-clojure`
      （两个套件都要 `dispositions: OK` 与 `baseline: OK`）。
- [ ] GitHub Actions：PR 层（`pr.yml`：core-host、packages）与 nightly 层（`nightly.yml`：Determinism / Engineering / Conformance、ilverify）在要发布的提交上都是绿的。
- [ ] r7rs：编译器或运行时有改动的版本，56 × 5 两台同轮配对无可声明回退（`bench/r7rs/pairbuild.py` + `m16sum.py`）。

## 2. 版本核对

- [ ] `src/Cleat.Core/Runtime/CoreInfo.cs` 的 `Version`、`src/Cleat.Runtime.Package/Cleat.Runtime.Package.csproj` 的缺省 `<Version>` 一致。
- [ ] `packages/*/build.clj` 的 `default-version`：为这个运行时重编过的包末位已加一（规则见 [packages.md](packages.md)）；`packages/*/cleat.edn`、
      `examples/`、`tests/Cleat.Host.Tests/NuGetColdLoadTests.cs`、README 里的引用同步。
- [ ] CHANGELOG 的版本节（日期）与"从上一版升级"一节；`notes/packages.md` 的版本表。

## 3. 打包与产物

```sh
bash ~/release188.sh      # 13 上；产物在 artifacts/release-<版本>/
```

- `nuget/`：`Cleat.Runtime`、`Cleat.Cli` 与 `packages/` 的 12 个包（按依赖顺序 `cleat -T:build install` 到这个目录）。
- `publish/`：自包含发布 `cleat-<版本>-linux-x64.tar.gz`、`-win-x64.zip`、`-osx-arm64.tar.gz`（各含镜像、随附库、`tools/`、`clojure-src/`）。
- `SHA256SUMS`：上面全部文件。

## 4. 产物校验（脚本的输出里核对）

- [ ] 每个包都打出来了（脚本没有 `PACK FAILED` / `PUBLISH FAILED`）。
- [ ] **镜像 id 一致**：发布目录、Cleat.Cli 与 Cleat.Runtime 包里，`cleat.boot.dll` 的镜像 id 与 `Cleat.Clojure.Libs.dll`、`tools/*.dll` 记的
      `CleatBootImageAttribute` 相同（三行 `OK`；cleat-186：对不上时随附库不用、tools 退回源码）。
- [ ] 包的 nuspec 对 `Cleat.Runtime` 是精确版本 `[<版本>]`：`unzip -p artifacts/release-<版本>/nuget/cleat.nrepl.*.nupkg '*.nuspec' | grep Cleat.Runtime`。
- [ ] `sha256sum -c SHA256SUMS` 通过。

## 5. 冒烟（脚本最后一节，干净的 HOME、没有 `~/.cleat/local-feed`）

- [ ] `dotnet tool install -g Cleat.Cli --add-source artifacts/release-<版本>/nuget`；`cleat -e`、脚本、REPL（含 `(source when)`）、`cleat -M -m` 的小项目。
- [ ] 自包含发布解开即可运行（`cleat -e`）。

## 6. 推送（维护者）

- NuGet 源（BaGetter 或 nuget.org，按需）：先 `Cleat.Runtime`、`Cleat.Cli`，再按依赖顺序推包——
  ToolsNamespace → ToolsBuild → Nrepl → Neorepl；ToolsReader → ToolsAnalyzer → CoreAsync；TestCheck、SpecAlpha、DataGenerators → TestGenerative；Nanopass。
  ```sh
  for f in artifacts/release-<版本>/nuget/*.nupkg; do dotnet nuget push "$f" --source <源> --api-key "$NUGET_API_KEY"; done
  ```
  （同一版本不能重推；推错了只能 unlist 并发下一个修订号。）
- git：`git tag -a v<版本> -m "cleat <版本>"`、`git push origin v<版本>`。
- GitHub Release：标题 `cleat <版本>`，正文取 CHANGELOG 的版本节（含"从上一版升级"），附件为 `publish/` 下三个自包含发布与 `SHA256SUMS`。

## 7. 回退

- 包：nuget.org 上 unlist（不能删除），BaGetter 上可删；修复后发下一个修订号（运行时末位加一，所有包随之重编、末位加一）。
- tag：`git push --delete origin v<版本>` 与 `git tag -d v<版本>`（只在没人用之前）；GitHub Release 删除或标为 pre-release。
- 推送与 tag 的结果记进当次的发布任务（如 cleat-188）。
