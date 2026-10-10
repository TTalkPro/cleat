;;; 宿主程序（cleat-052）：Cleat.Host 启动时运行（预编译为 src/boot/cleat.scheme.clojure/program.fasl，见 build-host.sps），
;;; 登记 Cleat.Core 的运行时钩子。只导入启动所需的库；AOT 编译用的 (clojure compile) 由驱动在第一次 compile 时装入。
(import (rnrs) (clojure driver))
(install-clojure-hooks!)
