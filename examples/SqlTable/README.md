# SqlTable：用 Clojure 写的控制台应用

读一个文件、执行一行 SQL，把结果画成表格。代码全是 Clojure，数据库是 NuGet 上的 `Microsoft.Data.Sqlite`
（连同它的原生库 `e_sqlite3`），没有 C# 项目。

```sh
$ dotnet cleat -M:run data/people.csv "select city, count(*) as n, avg(age) as avg_age from people group by city"
+----------+---+---------+
| city     | n | avg_age |
+----------+---+---------+
| Beijing  | 2 |    32.5 |
| Shanghai | 2 |    37.5 |
| 深圳     | 1 |      25 |
+----------+---+---------+
3 rows
```

- `.csv` 文件：第一行是列名，导入内存数据库里以文件名命名的表（`people.csv` → `people`）；全是整数的列存 INTEGER，
  全是数的列存 REAL，其余 TEXT，空字段是 NULL。
- `.db` / `.sqlite` 等其他文件：当 SQLite 数据库只读打开。
- 不返回结果集的语句打印影响的行数。用法错误退出码 2，SQL 出错退出码 1。

```
SqlTable/
  .config/dotnet-tools.json  本地 dotnet tool：Cleat.Cli（命令 cleat）
  nuget.config            包从哪来：nuget.org（SQLite），BaGetter（Cleat.*）
  cleat.edn               加载路径、NuGet 依赖、别名
  build.clj               构建脚本：test / compile / uber / clean
  src/sqltable/csv.clj    CSV 解析（RFC 4180：引号、引号里的逗号与换行）
  src/sqltable/db.clj     SQLite：CSV 导入、打开、查询
  src/sqltable/table.clj  画表格（数字右对齐，中日韩字符占两格）
  src/sqltable/main.clj   -main
  test/sqltable/          clojure.test
  data/people.csv         示例数据
```

## 准备

.NET 10 SDK；`Cleat.Cli` 在 BaGetter 上（见 `../WebServer/README.md` 的“准备”）。在本目录：

```sh
dotnet tool restore
```

## 运行

```sh
dotnet cleat -M:run data/people.csv "select * from people order by age desc"
dotnet cleat -M -m sqltable.main data/people.csv "select name from people"   # 不用别名
```

第一次运行时 `create-basis` 在 `.cleat/basis/` 里 `dotnet restore` 出 `cleat.edn` 的依赖。依赖就是一行：

```clojure
:deps {Microsoft.Data.Sqlite {:nuget/version "10.0.12"}}
```

`Microsoft.Data.Sqlite` 经 `SQLitePCLRaw.bundle_e_sqlite3` 带来原生库，放在包的 `runtimes/<rid>/native/` 下。basis 按当前平台
（如 `linux-x64`，再退到 `linux`、`unix`）把这个目录也放进加载路径，cleat 加载原生库时在加载路径里找（`libe_sqlite3.so`、
`e_sqlite3.dll`、`libe_sqlite3.dylib`）。Clojure 代码里直接 `(:import (Microsoft.Data.Sqlite SqliteConnection))`，和用 C# 写一样。

`dotnet cleat -Spath -M:run` 打印加载路径。

## 测试

```sh
dotnet cleat -M:test        # 或 dotnet cleat -T:build test
```

## 交互开发

```sh
dotnet cleat -M:neorepl
```

```clojure
user=> (require '[sqltable.db :as db] '[sqltable.table :as table])
user=> (with-open [c (db/open "data/people.csv")]
         (println (table/render (db/query c "select * from people where age < 30"))))
```

## 发布

```sh
dotnet cleat -T:build uber
target/sqltable/sqltable data/people.csv "select count(*) from people"
```

`uber` 把 Clojure 代码编成程序集 `SqlTable.dll`，和 cleat 运行时、NuGet 依赖（含原生库）一起放进目录 `target/sqltable/`，
写一个启动脚本，名字取目录名：`sqltable`（Windows 是 `sqltable.cmd`；要别的名字，`uber` 加 `:launcher`）。目标机器只需要 .NET 10 运行时，不需要 cleat 与 SDK。
