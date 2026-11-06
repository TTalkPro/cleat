;; cleat.kernel —— boot 的第一个文件（designs/00 D15、03 §5；cleat-017）。
;;
;; 这个命名空间本身由运行时创建：RT.Init 时 PrimitiveRegistry 把 src/Cleat.Core/Runtime/Primitives.cs
;; 原语表里的每一项包装成 IFn 绑到同名 Var（cleat.kernel/first、next、seq、cons、conj、assoc、get、nth、count、
;; list0/1/2、vector、map、set、subvec、iter、reduced?、keyword、*-cast、print-string；add、minus、…、num；
;; equiv、equals、identical?、hash、hasheq、compare、class-of）。所以这里不写 (ns cleat.kernel)，也不 def 原语。
;;
;; 03 §5 设想 kernel 还提供"编译器与 nanopass 要用的最小函数集"（str / symbol / gensym / meta / with-meta /
;; apply / concat / list* …）。cleat-017 的决定：先不在这里重复 core.clj——core.clj 开头约 700 行已经只用
;; 特殊形式 + (. clojure.lang.RT (…)) 互操作自举出 defn / defmacro / let / fn / when / cond / and / or …，
;; 而编译器（cleat-182 起是 C#，designs/30）不在镜像里，库都排在 clojure.core 之后（03 §5），直接用 clojure.core 即可。
;; 若 cleat-018 / cleat-020 发现确有必须早于 core.clj 的东西，再加到这里（并在任务文件里记录）。
;;
;; core.clj 不 refer 这个命名空间（会与 clojure.core/first 等重名而冲突）；需要原语的地方写全名，
;; 如 (cleat.kernel/maybe-type sym false)。待运行时补齐的原语见 tasks/cleat-017 的"需要的新原语"。
;;
;; 读 / 加载这个文件时只做一件事：把 *ns* 切到 cleat.kernel（命名空间已存在，in-ns 幂等）。
(clojure.core/in-ns 'cleat.kernel)
