;; cleat-189：clojure.lang.Compiler 门面。ClojureCompilerTests.CompilerFacade 核对输出。
(import '(clojure.lang Compiler Compiler+CompilerException))

;; 编译错误就是 Compiler+CompilerException（cleat 的 ExpanderException 是它的子类），ex-data 照 JVM
(prn :catch (try (eval 'no-such-symbol-here) (catch Compiler+CompilerException e
                                                [(instance? Compiler+CompilerException e)
                                                 (:clojure.error/phase (ex-data e))
                                                 (boolean (re-find #"Unable to resolve symbol: no-such-symbol-here" (.Message e)))])))

;; quote 多参：原因是带 :form 的 ExceptionInfo
(prn :quote (try (eval '(quote 1 2 3)) (catch Compiler+CompilerException e (:form (ex-data (.InnerException e))))))

;; 名字改写
(prn :munge (Compiler/munge "a-b?") (Compiler/demunge (Compiler/munge "a-b?")) (pos? (count Compiler/CHAR_MAP)))

;; 求值 / 加载 / 展开
(prn :eval (Compiler/eval '(+ 1 2 3))
     (Compiler/load (System.IO.StringReader. "(def facade-x 40) (+ facade-x 2)") "facade.clj" "facade.clj" "facade.clj")
     (Compiler/macroexpand1 '(when a b))
     (contains? Compiler/specials 'if))

;; 动态 Var
(prn :vars (identical? Compiler/SOURCE_PATH #'clojure.core/*file*) (identical? Compiler/SOURCE #'clojure.core/*source-path*))

;; macroexpand 时宏的元数错误原样是 ArityException（去掉 &form / &env），编译时仍是编译错误
(defmacro m0 [] 0)
(prn :arity (try (macroexpand '(m0 1)) (catch clojure.lang.ArityException e (.-Actual e)))
     (try (eval '(m0 1)) (catch Compiler+CompilerException e (.Name (class (.InnerException e))))))

;; 反射调用找不到成员：消息开头 "No matching member m taking n args"
(prn :nomatch (try (eval '(Int32/Parse)) (catch Exception e (subs (.Message e) 0 38))))

;; case 的结果类型、int 测试的性能警告、局部沿用初值局部的类型
(defn warnings [form]
  (let [w (System.IO.StringWriter.)]
    (binding [*err* w *warn-on-reflection* true] (eval form))
    (str w)))
(prn :case-type (warnings '(clojure.lang.BigDecimal/Create (case 1 1 1))) (warnings '(clojure.lang.BigDecimal/Create (case 1 1 "1"))))
(prn :case-perf (boolean (re-find #"^Performance warning, .*case has int tests, but tested expression is not primitive"
                                  (warnings '(let [x (Object.)] (case x 1 1 2)))))
     (warnings '(let [x 5] (case x 1 1 2))))
(prn :local-tag (warnings '(let [x (Object.)] (let [y x] (.GetHashCode y)))))
