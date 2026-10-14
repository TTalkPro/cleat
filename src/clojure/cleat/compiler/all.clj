;; Clojure 编译器的全部命名空间（cleat-085）：编包程序集 src/boot/cleat.compiler.dll 的入口（Cleat.Host.csproj 的 GenerateCompilerPackage、
;; tools/build.cs compile-compiler）。新加的编译器命名空间要在这里登记。影子模式的 cleat.compiler.dump / shadow 依赖 Cleat.Scheme 的 IrDump，
;; 不进包（从源码加载，cleat-092）。
(ns cleat.compiler.all
  (:require [cleat.nanopass]
            [cleat.nanopass.records]
            [cleat.nanopass.language]
            [cleat.nanopass.pass]
            [cleat.compiler.languages]
            [cleat.compiler.loops]
            [cleat.compiler.front]
            [cleat.compiler.closures]
            [cleat.compiler.analyzer]
            [cleat.compiler.codegen]
            [cleat.compiler.pipeline]
            [cleat.compiler.driver]))
