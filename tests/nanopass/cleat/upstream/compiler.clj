;; 上游 nanopass-framework-scheme 的 tests/compiler.ss（一个 Scheme 子集的 20 趟编译器）与 tests/helpers.ss 翻译到 cleat.nanopass（cleat-095）。
;; 语言 LP、L0 ~ L18 与各 pass 逐个对应；写法对照见 src/clojure/cleat/nanopass/README.md §3。
;; 上游的测试驱动（test-driver.ss）只跑 pass 链、不解释结果（check-eval 是空操作），这里同样（见 compiler_tests.clj）。
;; 改名：L12 起 Predicate 的 (true) / (false) 写成 (tru) / (fls)——Clojure 里 true / false 是布尔字面量，不能当关键字。
(ns cleat.upstream.compiler
  (:require [cleat.nanopass :as np :refer [define-language define-parser define-pass nanopass-case with-output-language out in-context]]))

;; —— helpers.ss ——

(def list-of-user-primitives
  '{not [1 not]
    < [2 test] <= [2 test] = [2 test] boolean? [1 test] char? [1 test] eq? [2 test] integer? [1 test] null? [1 test] pair? [1 test]
    procedure? [1 test] vector? [1 test] zero? [1 test]
    * [2 value] + [2 value] - [2 value] add1 [1 value] car [1 value] cdr [1 value] char->integer [1 value] cons [2 value]
    make-vector [1 value] quotient [2 value] remainder [2 value] sub1 [1 value] vector [-1 value] vector-length [1 value]
    vector-ref [2 value] void [0 value]
    set-car! [2 effect] set-cdr! [2 effect] vector-set! [3 effect]})

(def list-of-system-primitives
  '{closure-ref [2 value] make-closure [2 value] procedure-code [1 value]
    closure-set! [3 effect]
    fref [1 value] fset! [2 effect] fincr! [1 effect] fdecr! [1 effect] href [2 value] hset! [3 effect] logand [2 value] sll [2 value] sra [2 value]})

(defn user-primitive? [x] (contains? list-of-user-primitives x))
(defn system-primitive? [x] (contains? list-of-system-primitives x))
(defn primitive? [x] (or (user-primitive? x) (system-primitive? x)))
(defn- prim-kind [x] (second (or (list-of-user-primitives x) (list-of-system-primitives x))))
(defn predicate-primitive? [x] (= 'test (prim-kind x)))
(defn value-primitive? [x] (= 'value (prim-kind x)))
(defn effect-primitive? [x] (= 'effect (prim-kind x)))

(defn constant? [x] (or (nil? x) (= x ()) (number? x) (char? x) (boolean? x) (string? x)))
(defn scheme-keyword? [x] (contains? '#{quote set! if begin let letrec lambda} x))
(defn datum? [x] (or (constant? x) (and (sequential? x) (every? datum? x))))
(def variable? symbol?)

(def ^:private gen-symbol-seed (atom 0))
(defn reset-seed [] (reset! gen-symbol-seed 0))
(defn gen-symbol [sym] (symbol (str sym "_" (swap! gen-symbol-seed inc))))
(defn gen-label [sym] (symbol (str sym "%")))

(defn distinct-list? [ls] (or (empty? ls) (apply distinct? ls)))
(defn set-cons [a s] (if (some #{a} s) s (cons a s)))
(defn union [& sets] (reduce (fn [acc s] (reduce (fn [acc x] (if (some #{x} acc) acc (conj acc x))) acc s)) [] sets))
(defn intersection [s & sets] (filter (fn [x] (every? #(some #{x} %) sets)) s))
(defn difference [s & sets] (remove (fn [x] (some #(some #{x} %) sets)) s))
(defn list-index [a ls] (let [i (count (take-while #(not= % a) ls))] (if (= i (count ls)) -1 i)))

;; —— LP、L0 与 rename-vars/verify-legal ——

(define-language LP
  (terminals (variable (x)) (datum (d)) (user-primitive (pr)))
  (Expr (e body)
    d
    x
    pr
    (set! x e)
    (if e1 e2)
    (if e1 e2 e3)
    (begin e1 ... e2)
    (lambda (x ...) body1 ... body2)
    (let ((x e) ...) body1 ... body2)
    (letrec ((x e) ...) body1 ... body2)
    (e0 e1 ...)))

(define-parser parse-LP LP)

(define-language L0 (extends LP)
  (Expr (e body)
    (- d x pr (e0 e1 ...))
    (+ (datum d) (var x) (primapp pr e ...) (app e0 e1 ...))))

(define-parser parse-L0 L0)

(defn- listify
  "输入表达式里的向量（[x 0]）改成列表。"
  [x]
  (if (sequential? x) (apply list (map listify x)) x))

(defn rename-vars-verify-legal
  "上游 rename-vars/verify-legal（syncase 的匹配改为手写）：S 表达式 → L0，变量改名 x_N，引用未绑定的变量报错。"
  [expr]
  (let [extend-env* (fn [x* env] (let [rx* (mapv gen-symbol x*)] [rx* (into env (zipmap x* rx*))]))
        bad (fn [& msg] (throw (ex-info (apply str "rename-vars/verify-legal: " msg) {})))
        bindings? (fn [bs] (and (sequential? bs) (every? #(and (sequential? %) (= 2 (count %)) (symbol? (first %))) bs)
                                (distinct-list? (map first bs))))]
    (letfn [(f [expr env]
              (let [f* (fn [e* env] (mapv #(f % env) e*))
                    head (when (and (seq? expr) (seq expr) (symbol? (first expr)) (not (contains? env (first expr)))) (first expr))
                    n (when (seq? expr) (count expr))]
                (with-output-language [L0 Expr]
                  (cond
                    (constant? expr) (out (datum ~expr))
                    (and (= head 'quote) (= n 2)) (out (datum ~(second expr)))
                    (symbol? expr) (cond (contains? env expr) (out (var ~(env expr)))
                                         (scheme-keyword? expr) (bad "invalid reference to keyword " expr)
                                         :else (bad "reference to unbound var " expr))
                    (and (= head 'set!) (= n 3) (symbol? (second expr)))
                    (let [v (second expr)]
                      (cond (contains? env v) (out (set! ~(env v) ~(f (nth expr 2) env)))
                            (scheme-keyword? v) (bad "set! of keyword " (pr-str expr))
                            :else (bad "set! of unbound var " (pr-str expr))))
                    (and (= head 'if) (= n 3)) (out (if ~(f (nth expr 1) env) ~(f (nth expr 2) env)))
                    (and (= head 'if) (= n 4)) (out (if ~(f (nth expr 1) env) ~(f (nth expr 2) env) ~(f (nth expr 3) env)))
                    (and (= head 'begin) (>= n 2)) (let [es (rest expr)] (out (begin ~(f* (butlast es) env) ... ~(f (last es) env))))
                    (and (= head 'let) (>= n 3) (bindings? (second expr)))
                    (let [bs (second expr) body (nnext expr)
                          [x* new-env] (extend-env* (map first bs) env)]
                      (out (let ([~x* ~(f* (map second bs) env)] ...) ~(f* (butlast body) new-env) ... ~(f (last body) new-env))))
                    (and (= head 'letrec) (>= n 3) (bindings? (second expr)))
                    (let [bs (second expr) body (nnext expr)
                          [x* env] (extend-env* (map first bs) env)]
                      (out (letrec ([~x* ~(f* (map second bs) env)] ...) ~(f* (butlast body) env) ... ~(f (last body) env))))
                    (and (= head 'lambda) (>= n 3) (sequential? (second expr)) (every? symbol? (second expr)) (distinct-list? (second expr)))
                    (let [body (nnext expr)
                          [x* env] (extend-env* (second expr) env)]
                      (out (lambda (~x* ...) ~(f* (butlast body) env) ... ~(f (last body) env))))
                    (and head (user-primitive? head) (= (first (list-of-user-primitives head)) (count (rest expr))))
                    (out (primapp ~head ~(f* (rest expr) env) ...))
                    (and (seq? expr) (seq expr)) (out (app ~(f (first expr) env) ~(f* (rest expr) env) ...))
                    :else (bad "invalid expression " (pr-str expr))))))]
      (f (listify expr) {}))))

(define-pass verify-scheme LP [ir] -> L0 []
  (definitions
    (defn invalid-var? [x env]
      (cond (some #{x} env) false
            (scheme-keyword? x) "keyword"
            (user-primitive? x) "user-primitive"
            :else "unbound variable"))
    (defn valid-bindings? [ls] (every? variable? ls))
    (defn duplicate-names? [var*] (seq (for [[x n] (frequencies var*) :when (> n 1)] x)))
    (defn check-bindings! [x what]
      (cond (not (valid-bindings? x)) (throw (ex-info (str "verify-scheme: invalid binding list " x " in " what " form") {}))
            (duplicate-names? x) (throw (ex-info (str "verify-scheme: duplicate bindings " (vec (duplicate-names? x)) " in " what " form") {})))))
  (Expr [ir [env ()]] -> Expr []
    [~d (out (datum ~d))]
    [~x (if-let [invalid (invalid-var? x env)]
          (throw (ex-info (str "verify-scheme: reference to " invalid " " x) {}))
          (out (var ~x)))]
    [(set! ~x ~e)
     (if-let [invalid (invalid-var? x env)]
       (throw (ex-info (str "verify-scheme: assignment to " invalid " " x) {}))
       (let [e (Expr e env)] (out (set! ~x ~e))))]
    [(lambda (~x ...) ~body1 ... ~body2)
     (check-bindings! x "lambda")
     (let [env (concat env x)
           body1 (mapv #(Expr % env) body1)
           body2 (Expr body2 env)]
       (out (lambda (~x ...) ~body1 ... ~body2)))]
    [(let ((~x ~e) ...) ~body1 ... ~body2)
     (check-bindings! x "let")
     (let [e (mapv #(Expr % env) e)
           env (concat env x)
           body1 (mapv #(Expr % env) body1)
           body2 (Expr body2 env)]
       (out (let ((~x ~e) ...) ~body1 ... ~body2)))]
    [(letrec ((~x ~e) ...) ~body1 ... ~body2)
     (check-bindings! x "letrec")
     (let [env (concat env x)
           e (mapv #(Expr % env) e)
           body1 (mapv #(Expr % env) body1)
           body2 (Expr body2 env)]
       (out (letrec ((~x ~e) ...) ~body1 ... ~body2)))]
    [(~e0 ~e1 ...)
     (let [e1 (mapv #(Expr % env) e1)]
       (if (and (symbol? e0) (user-primitive? e0))
         (out (primapp ~e0 ~e1 ...))
         (out (app ~(Expr e0 env) ~e1 ...))))]))

;; —— L1 ~ L3 ——

(define-language L1 (extends L0)
  (Expr (e body)
    (- (lambda (x ...) body1 ... body2)
       (let ((x e) ...) body1 ... body2)
       (letrec ((x e) ...) body1 ... body2))
    (+ (lambda (x ...) body)
       (let ((x e) ...) body)
       (letrec ((x e) ...) body))))

(define-parser parse-L1 L1)

(define-pass remove-implicit-begin L0 [ir] -> L1 []
  (process-expr-expr Expr [ir] -> Expr []
    [(lambda (~x ...) ~[body1] ... ~[body2]) (out (lambda (~x ...) (begin ~body1 ... ~body2)))]
    [(let ((~x ~[e]) ...) ~[body1] ... ~[body2]) (out (let ((~x ~e) ...) (begin ~body1 ... ~body2)))]
    [(letrec ((~x ~[e]) ...) ~[body1] ... ~[body2]) (out (letrec ((~x ~e) ...) (begin ~body1 ... ~body2)))]))

(define-language L2 (extends L1)
  (Expr (e body)
    (- (datum d))
    (+ (quoted-const d))))

(define-parser parse-L2 L2)

(define-pass remove-unquoted-constant L1 [ir] -> L2 []
  (process-expr-expr Expr [ir] -> Expr []
    [(datum ~d) (out (quoted-const ~d))]))

(define-language L3 (extends L2) (Expr (e body) (- (if e1 e2))))

(define-parser parse-L3 L3)

(define-pass remove-one-armed-if L2 [ir] -> L3 []
  (process-expr-expr Expr [ir] -> Expr []
    [(if ~[e1] ~[e2]) (out (if ~e1 ~e2 (primapp void)))]))

;; —— L4：uncover-settable（额外返回值、线程化的变量表）——

(define-language L4 (extends L3)
  (Expr (e body)
    (- (lambda (x ...) body)
       (let ((x e) ...) body)
       (letrec ((x e) ...) body))
    (+ (lambda (x ...) sbody)
       (let ((x e) ...) sbody)
       (letrec ((x e) ...) sbody)))
  (SetBody (sbody) (+ (settable (x ...) body) => body)))

(define-parser parse-L4 L4)

(define-pass uncover-settable L3 [ir] -> L4 []
  (definitions
    (defn Expr* [e* asgn-var*]
      (reduce (fn [[es asgn] e] (let [[e asgn] (Expr e asgn)] [(conj es e) asgn])) [[] asgn-var*] e*)))
  (Expr [ir asgn-var*] -> Expr [asgn-var*]
    [(set! ~x ~[e asgn-var*]) [(out (set! ~x ~e)) (set-cons x asgn-var*)]]
    [(lambda (~x* ...) ~[body asgn-var*])
     (let [set-x* (intersection asgn-var* x*)]
       [(out (lambda (~x* ...) (settable (~set-x* ...) ~body))) (difference asgn-var* set-x*)])]
    [(let ([~x* ~e*] ...) ~[body asgn-var*])
     (let [set-x* (intersection asgn-var* x*)
           [e* asgn-var*] (Expr* e* (difference asgn-var* set-x*))]
       [(out (let ([~x* ~e*] ...) (settable (~set-x* ...) ~body))) asgn-var*])]
    [(letrec ([~x* ~e*] ...) ~[body asgn-var*])
     (let [[e* asgn-var*] (Expr* e* asgn-var*)
           set-x* (intersection asgn-var* x*)]
       [(out (letrec ((~x* ~e*) ...) (settable (~set-x* ...) ~body))) (difference asgn-var* set-x*)])]
    [(app ~[e asgn-var*] ~e* ...)
     (let [[e* asgn-var*] (Expr* e* asgn-var*)] [(out (app ~e ~e* ...)) asgn-var*])]
    [(primapp ~pr ~e* ...)
     (let [[e* asgn-var*] (Expr* e* asgn-var*)] [(out (primapp ~pr ~e* ...)) asgn-var*])]
    [(if ~[e0 asgn-var*] ~e1 ~e2)
     (let [[e1 asgn-var*] (Expr e1 asgn-var*)
           [e2 asgn-var*] (Expr e2 asgn-var*)]
       [(out (if ~e0 ~e1 ~e2)) asgn-var*])]
    [(begin ~e* ... ~[e asgn-var*])
     (let [[e* asgn-var*] (Expr* e* asgn-var*)] [(out (begin ~e* ... ~e)) asgn-var*])])
  (let [[e asgn-var*] (Expr ir ())] e))

;; —— L5：remove-impure-letrec ——

(define-language L5 (extends L4)
  (Expr (e body)
    (+ lexpr
       (letrec ((x lexpr) ...) body))
    (- (lambda (x ...) sbody)
       (letrec ((x e) ...) sbody)))
  (LambdaExpr (lexpr) (+ (lambda (x ...) sbody))))

(define-parser parse-L5 L5)

(define-pass remove-impure-letrec L4 [ir] -> L5 []
  (process-expr-expr Expr [ir] -> Expr []
    [(lambda (~x ...) ~[sbody]) (in-context LambdaExpr (out (lambda (~x ...) ~sbody)))]
    [(letrec ((~x1 (lambda (~x2 ...) ~[sbody1])) ...) (settable () ~[body2]))
     (let [lambdabody (mapv (fn [x sbody] (in-context LambdaExpr (out (lambda (~x ...) ~sbody)))) x2 sbody1)]
       (out (letrec ((~x1 ~lambdabody) ...) ~body2)))]
    [(letrec ((~x1 ~[e]) ...) (settable (~x2 ...) ~[body]))
     (let [voids (mapv (fn [_] (in-context Expr (out (primapp void)))) x1)
           new-ids (mapv gen-symbol x1)
           bodies (mapv (fn [lhs id] (in-context Expr (out (set! ~lhs (var ~id))))) x1 new-ids)
           new-body (butlast bodies)
           rest-bodies (last bodies)]
       (out (let ([~x1 ~voids] ...)
              (settable (~x1 ...)
                (begin
                  (primapp void)
                  (let ([~new-ids ~e] ...)
                    (settable ()
                      (begin ~new-body ... ~rest-bodies)))
                  ~body)))))])
  (process-setbody-setbody SetBody [ir] -> SetBody []
    [(settable (~x ...) ~[body]) (out (settable (~x ...) ~body))])
  (process-expr-lexpr Expr [ir] -> LambdaExpr []
    [(lambda (~x ...) ~[sbody]) (out (lambda (~x ...) ~sbody))])
  (process-setbody-expr SetBody [ir] -> Expr []
    [(settable (~x ...) ~[body]) body]))

;; —— L6：remove-set!、sanitize-binding ——

(define-language L6 (extends L5)
  (Expr (e body)
    (- (let ((x e) ...) sbody)
       (set! x e))
    (+ (let ((x e) ...) body)))
  (LambdaExpr (lexpr)
    (- (lambda (x ...) sbody))
    (+ (lambda (x ...) body)))
  (SetBody (sbody) (- (settable (x ...) body))))

(define-parser parse-L6 L6)

(define-pass remove-set! L5 [ir] -> L6 []
  (Expr [ir [set* ()]] -> Expr []
    [(var ~x) (if (some #{x} set*) (out (primapp car (var ~x))) (out (var ~x)))]
    [(set! ~x ~[e set* -> e]) (out (primapp set-car! (var ~x) ~e))]
    [(let ((~x ~[e set* -> e]) ...) ~sbody) (SetBody sbody x e set*)])
  (LambdaExpr [ir set*] -> LambdaExpr []
    [(lambda (~x ...) ~[sbody x () set* -> body]) body])
  (SetBody [ir x* e* set*] -> Expr []
    [(settable () ~[body set* -> body])
     (if (empty? e*)
       (out (lambda (~x* ...) ~body))
       (out (let ([~x* ~e*] ...) ~body)))]
    [(settable (~x ...) ~[body (concat x set*) -> body])
     (let [settable-bindings (fn settable-bindings [var* set*]
                               (if (empty? var*)
                                 [() () ()]
                                 (let [v (first var*)
                                       [var* lhs* rhs*] (settable-bindings (rest var*) set*)]
                                   (if (some #{v} set*)
                                     (let [tmp (gen-symbol v)]
                                       [(cons tmp var*) (cons v lhs*)
                                        (cons (in-context Expr (out (primapp cons (var ~tmp) (primapp void)))) rhs*)])
                                     [(cons v var*) lhs* rhs*]))))
           [x* lhs* rhs*] (settable-bindings x* x)]
       (if (empty? e*)
         (out (lambda (~x* ...) (let ([~lhs* ~rhs*] ...) ~body)))
         (out (let ([~x* ~e*] ...) (let ([~lhs* ~rhs*] ...) ~body)))))]))

(define-pass sanitize-binding L6 [ir] -> L6 []
  (Expr [ir [rhs? false]] -> Expr [false]
    [(var ~x) [(out (var ~x)) false]]
    [(if ~[e1 false -> e1 ig1] ~[e2 false -> e2 ig2] ~[e3 false -> e3 ig3]) [(out (if ~e1 ~e2 ~e3)) false]]
    [(begin ~[e1 false -> e1 ig1] ... ~[e2 false -> e2 ig2]) [(out (begin ~e1 ... ~e2)) false]]
    [(primapp ~pr ~[e false -> e ig] ...) [(out (primapp ~pr ~e ...)) false]]
    [(app ~[e0 false -> e0 ig0] ~[e1 false -> e1 ig1] ...) [(out (app ~e0 ~e1 ...)) false]]
    [(quoted-const ~d) [(out (quoted-const ~d)) false]]
    [(let ([~x ~[e true -> e lambda?]] ...) ~[body false -> body ig])
     (let [groups (map vector x e lambda?)
           lets (remove #(nth % 2) groups) letrecs (filter #(nth % 2) groups)
           let-x* (mapv first lets) let-e* (mapv second lets)
           letrec-x* (mapv first letrecs) letrec-e* (mapv second letrecs)]
       (cond
         (empty? letrec-x*) [(out (let ([~let-x* ~let-e*] ...) ~body)) false]
         (empty? let-x*) [(out (letrec ([~letrec-x* ~letrec-e*] ...) ~body)) false]
         :else [(out (letrec ([~letrec-x* ~letrec-e*] ...) (let ([~let-x* ~let-e*] ...) ~body))) false]))]
    [(letrec ([~x1 (lambda (~x2 ...) ~[body1 false -> body1 ig1])] ...) ~[body2 false -> body2 ig2])
     [(out (letrec ([~x1 (lambda (~x2 ...) ~body1)] ...) ~body2)) false]])
  (LambdaExpr [ir [rhs? false]] -> LambdaExpr [dummy]
    [(lambda (~x ...) ~[body false -> body ig]) [(out (lambda (~x ...) ~body)) true]]))

;; —— L7、L8：remove-anonymous-lambda、uncover-free ——

(define-language L7 (extends L6) (Expr (e body) (- lexpr)))

(define-parser parse-L7 L7)

(define-pass remove-anonymous-lambda L6 [ir] -> L7 []
  (Expr [ir] -> Expr []
    [(lambda (~x ...) ~[body])
     (let [anon (gen-symbol 'anon)]
       (out (letrec ([~anon (lambda (~x ...) ~body)]) (var ~anon))))]))

(define-language L8 (extends L7)
  (entry Expr)
  (LambdaExpr (lexpr) (- (lambda (x ...) body)))
  (FreeExp (free-body) (+ (free (x ...) body) => body))
  (LambdaExpr (lexpr) (+ (lambda (x ...) free-body))))

(define-parser parse-L8 L8)

(define-pass uncover-free L7 [ir] -> L8 []
  (definitions
    (defn LambdaExpr* [lexpr* free*]
      (reduce (fn [[ls free*] l] (let [[l free*] (LambdaExpr l free*)] [(conj ls l) free*])) [[] free*] lexpr*))
    (defn Expr* [e* free*]
      (reduce (fn [[es free*] e] (let [[e free*] (Expr e free*)] [(conj es e) free*])) [[] free*] e*)))
  (Expr [ir free*] -> Expr [free*]
    [(letrec ([~x* ~lexpr*] ...) ~[body free*])
     (let [[e* free*] (LambdaExpr* lexpr* free*)]
       [(out (letrec ([~x* ~e*] ...) ~body)) (difference free* x*)])]
    [(let ([~x* ~e*] ...) ~[body free*])
     (let [[e* free*] (Expr* e* (difference free* x*))]
       [(out (let ([~x* ~e*] ...) ~body)) free*])]
    [(var ~x) [(out (var ~x)) (cons x free*)]]
    [(app ~[e free*] ~e* ...)
     (let [[e* free*] (Expr* e* free*)] [(out (app ~e ~e* ...)) free*])]
    [(primapp ~pr ~e* ...)
     (let [[e* free*] (Expr* e* free*)] [(out (primapp ~pr ~e* ...)) free*])]
    [(if ~[e1 free*] ~e2 ~e3)
     (let [[e2 free*] (Expr e2 free*)
           [e3 free*] (Expr e3 free*)]
       [(out (if ~e1 ~e2 ~e3)) free*])]
    [(begin ~e* ... ~[e free*])
     (let [[e* free*] (Expr* e* free*)] [(out (begin ~e* ... ~e)) free*])])
  (LambdaExpr [ir free*] -> LambdaExpr [free*]
    [(lambda (~x* ...) ~[body free*])
     (let [free* (difference free* x*)]
       [(out (lambda (~x* ...) (free (~free* ...) ~body))) free*])])
  (let [[e free*] (Expr ir ())] e))

;; —— L9 ~ L11：闭包转换 ——

(define-language L9
  (terminals (variable (x)) (datum (d)) (user-primitive (pr)))
  (Expr (e body)
    (var x)
    (quoted-const d)
    (if e1 e2 e3)
    (begin e1 ... e2)
    (let ((x e) ...) body)
    (letrec ((x lexpr) ...) c-letrec)
    (primapp pr e ...)
    (app e0 e1 ...)
    (anonymous-call e0 e1 ...))
  (LambdaExpr (lexpr) (lambda (x ...) bf-body))
  (BindFree (bf-body) (bind-free (x1 x2 ...) body))
  (Closure (c-exp) (closure x1 x2 ...))
  (ClosureLetrec (c-letrec) (closure-letrec ((x c-exp) ...) body)))

(define-parser parse-L9 L9)

(define-pass convert-closure L8 [ir] -> L9 []
  (Expr [ir [direct {}]] -> Expr []
    [(app (var ~x) ~[e1 direct -> e1] ...)
     (guard (contains? direct x))
     (out (app (var ~(direct x)) (var ~x) ~e1 ...))]
    [(app ~[e0 direct -> e0] ~[e1 direct -> e1] ...) (out (anonymous-call ~e0 ~e1 ...))]
    [(letrec ([~x1 (lambda (~x2 ...) (free (~x3 ...) ~body1))] ...) ~body2)
     (let [code-name* (mapv gen-label x1)
           cp* (mapv (fn [_] (gen-symbol 'cp)) x1)
           direct (into direct (zipmap x1 code-name*))
           body1 (mapv #(Expr % direct) body1)
           bind-free* (mapv (fn [cp formal* free* lbody]
                              (in-context LambdaExpr (out (lambda (~cp ~formal* ...) (bind-free (~cp ~free* ...) ~lbody)))))
                            cp* x2 x3 body1)
           closure* (mapv (fn [code-name free*] (in-context Closure (out (closure ~code-name ~free* ...)))) code-name* x3)]
       (out (letrec ([~code-name* ~bind-free*] ...)
              (closure-letrec ([~x1 ~closure*] ...)
                ~(Expr body2 direct)))))]))

(define-language L10 (extends L9)
  (entry LetrecExpr)
  (LetrecExpr (lrexpr) (+ (letrec ((x lexpr) ...) e)))
  (Expr (e body)
    (- (letrec ((x lexpr) ...) c-letrec))
    (+ (closure-letrec ((x c-exp) ...) body)))
  (ClosureLetrec (c-letrec) (- (closure-letrec ((x c-exp) ...) body))))

(define-parser parse-L10 L10)

(define-pass lift-letrec L9 [ir] -> L10 []
  (definitions
    (defn Expr* [e* binding*]
      (reduce (fn [[es b] e] (let [[e b] (Expr e b)] [(conj es e) b])) [[] binding*] e*))
    (defn LambdaExpr* [lexpr* binding*]
      (reduce (fn [[ls b] l] (let [[l b] (LambdaExpr l b)] [(conj ls l) b])) [[] binding*] lexpr*)))
  (Expr [ir binding*] -> Expr [binding*]
    [(var ~x) [(out (var ~x)) binding*]]
    [(quoted-const ~d) [(out (quoted-const ~d)) binding*]]
    [(if ~e1 ~e2 ~[e3 binding*])
     (let [[e1 binding*] (Expr e1 binding*)
           [e2 binding*] (Expr e2 binding*)]
       [(out (if ~e1 ~e2 ~e3)) binding*])]
    [(begin ~e1 ... ~[e2 binding*])
     (let [[e1 binding*] (Expr* e1 binding*)] [(out (begin ~e1 ... ~e2)) binding*])]
    [(let ([~x* ~e*] ...) ~[body binding*])
     (let [[e* binding*] (Expr* e* binding*)] [(out (let ([~x* ~e*] ...) ~body)) binding*])]
    [(primapp ~pr ~e* ...)
     (let [[e* binding*] (Expr* e* binding*)] [(out (primapp ~pr ~e* ...)) binding*])]
    [(app ~[e binding*] ~e* ...)
     (let [[e* binding*] (Expr* e* binding*)] [(out (app ~e ~e* ...)) binding*])]
    [(anonymous-call ~[e binding*] ~e* ...)
     (let [[e* binding*] (Expr* e* binding*)] [(out (anonymous-call ~e ~e* ...)) binding*])]
    [(letrec ((~x* ~lexpr*) ...) ~[e binding*])
     (let [[lexpr* binding*] (LambdaExpr* lexpr* binding*)]
       [e (concat (map vector x* lexpr*) binding*)])])
  (LambdaExpr [ir binding*] -> LambdaExpr [binding*]
    [(lambda (~x* ...) ~[bf-body binding*]) [(out (lambda (~x* ...) ~bf-body)) binding*]])
  (BindFree [ir binding*] -> BindFree [binding*]
    [(bind-free (~x ~x* ...) ~[body binding*]) [(out (bind-free (~x ~x* ...) ~body)) binding*]])
  (ClosureLetrec [ir binding*] -> Expr [binding*]
    [(closure-letrec ([~x* ~[c-exp*]] ...) ~[body binding*]) [(out (closure-letrec ([~x* ~c-exp*] ...) ~body)) binding*]])
  (let [[e binding*] (Expr ir ())
        x* (mapv first binding*) e* (mapv second binding*)]
    (out (letrec ([~x* ~e*] ...) ~e))))

(define-language L11 (extends L10)
  (entry LetrecExpr)
  (terminals (+ (system-primitive (spr))))
  (Expr (e body)
    (- (closure-letrec ((x c-exp) ...) body))
    (+ (sys-primapp spr e ...)))
  (BindFree (bf-body) (- (bind-free (x1 x2 ...) body)))
  (Closure (c-exp) (- (closure x1 x2 ...)))
  (LambdaExpr (lexpr)
    (- (lambda (x ...) bf-body))
    (+ (lambda (x ...) body))))

(define-parser parse-L11 L11)

(define-pass explicit-closure L10 [ir] -> L11 []
  (LetrecExpr [ir] -> LetrecExpr []
    [(letrec ((~x ~[lexpr]) ...) ~e)
     (let [e (Expr e () ())] (out (letrec ((~x ~lexpr) ...) ~e)))])
  (Expr [ir [cp ()] [env ()]] -> Expr []
    [(var ~x)
     (let [i (list-index x env)]
       (if (>= i 0)
         (out (sys-primapp closure-ref (var ~cp) (quoted-const ~i)))
         (out (var ~x))))]
    [(closure-letrec ((~x ~[c-exp -> e free**]) ...) ~[body cp env -> body])
     (let [e* (concat (apply concat
                             (map (fn [lhs free*]
                                    (map (fn [i free]
                                           (in-context Expr
                                             (out (sys-primapp closure-set! (var ~lhs) (quoted-const ~i)
                                                               ~(let [ind (list-index free env)]
                                                                  (if (>= ind 0)
                                                                    (out (sys-primapp closure-ref (var ~cp) (quoted-const ~ind)))
                                                                    (out (var ~free))))))))
                                         (range (count free*)) free*))
                                  x free**))
                      [body])
           e1 (butlast e*) e2 (last e*)]
       (out (let ([~x ~e] ...) (begin ~e1 ... ~e2))))])
  (BindFree [ir] -> Expr []
    [(bind-free (~x1 ~x2 ...) ~[body x1 x2 -> body]) body])
  (Closure [ir] -> Expr [dummy]
    [(closure ~x1 ~x2 ...) [(out (sys-primapp make-closure (var ~x1) (quoted-const ~(count x2)))) x2]])
  (LambdaExpr [ir] -> LambdaExpr []
    [(lambda (~x ...) ~[bf-body -> body]) (out (lambda (~x ...) ~body))]))

;; —— L12、L13：normalize-context、remove-complex-opera* ——

(define-language L12
  (terminals (variable (x)) (datum (d)) (value-primitive (vp)) (predicate-primitive (pp)) (effect-primitive (ep)) (system-primitive (spr)))
  (LetrecExpr (lrexpr) (letrec ((x lexpr) ...) v))
  (LambdaExpr (lexpr) (lambda (x ...) v))
  (Value (v)
    (var x)
    (quoted-const d)
    (if p1 v2 v3)
    (begin f0 ... v1)
    (let ((x v1) ...) v2)
    (primapp vp v ...)
    (sys-primapp spr v ...)
    (anonymous-call v0 v1 ...)
    (app v0 v1 ...))
  (Predicate (p)
    (tru)
    (fls)
    (if p1 p2 p3)
    (begin f0 ... p1)
    (let ((x v) ...) p)
    (primapp pp v ...)
    (sys-primapp spr v ...)
    (anonymous-call v0 v1 ...)
    (app v0 v1 ...))
  (Effect (f)
    (nop)
    (if p1 f2 f3)
    (begin f0 ... f1)
    (let ((x v) ...) f)
    (primapp ep v ...)
    (sys-primapp spr v ...)
    (anonymous-call v0 v1 ...)
    (app v0 v1 ...)))

(define-parser parse-L12 L12)

(define-pass normalize-context L11 [ir] -> L12 []
  (LetrecExpr [ir] -> LetrecExpr []
    [(letrec ((~x ~[lexpr]) ...) ~[v]) (out (letrec ((~x ~lexpr) ...) ~v))])
  (LambdaExpr [ir] -> LambdaExpr []
    [(lambda (~x ...) ~[v]) (out (lambda (~x ...) ~v))])
  (Value Expr [ir] -> Value []
    [(var ~x) (out (var ~x))]
    [(quoted-const ~d) (out (quoted-const ~d))]
    [(if ~[p0] ~[v1] ~[v2]) (out (if ~p0 ~v1 ~v2))]
    [(begin ~[f0] ... ~[v1]) (out (begin ~f0 ... ~v1))]
    [(let ((~x ~[v1]) ...) ~[v2]) (out (let ((~x ~v1) ...) ~v2))]
    [(primapp ~pr ~[p]) (guard (= pr 'not)) (out (if ~p (quoted-const false) (quoted-const true)))]
    [(primapp ~pr ~[v0] ...) (guard (predicate-primitive? pr)) (out (if (primapp ~pr ~v0 ...) (quoted-const true) (quoted-const false)))]
    [(primapp ~pr ~[v0] ...) (guard (value-primitive? pr)) (out (primapp ~pr ~v0 ...))]
    [(primapp ~pr ~[v0] ...) (guard (effect-primitive? pr)) (out (begin (primapp ~pr ~v0 ...) (primapp void)))]
    [(sys-primapp ~spr ~[v0] ...) (guard (predicate-primitive? spr)) (out (if (sys-primapp ~spr ~v0 ...) (quoted-const true) (quoted-const false)))]
    [(sys-primapp ~spr ~[v0] ...) (guard (value-primitive? spr)) (out (sys-primapp ~spr ~v0 ...))]
    [(sys-primapp ~spr ~[v0] ...) (guard (effect-primitive? spr)) (out (begin (sys-primapp ~spr ~v0 ...) (primapp void)))]
    [(anonymous-call ~[v0] ~[v1] ...) (out (anonymous-call ~v0 ~v1 ...))]
    [(app ~[v0] ~[v1] ...) (out (app ~v0 ~v1 ...))])
  (Predicate Expr [ir] -> Predicate []
    [(var ~x) (out (if (primapp eq? (var ~x) (quoted-const false)) (fls) (tru)))]
    [(quoted-const ~d) (if d (out (tru)) (out (fls)))]
    [(if ~[p0] ~[p1] ~[p2]) (out (if ~p0 ~p1 ~p2))]
    [(begin ~[f0] ... ~[p1]) (out (begin ~f0 ... ~p1))]
    [(let ((~x ~[v]) ...) ~[p]) (out (let ((~x ~v) ...) ~p))]
    [(primapp ~pr ~[p]) (guard (= pr 'not)) (out (if ~p (fls) (tru)))]
    [(primapp ~pr ~[v0] ...) (guard (predicate-primitive? pr)) (out (primapp ~pr ~v0 ...))]
    [(primapp ~pr ~[v0] ...) (guard (value-primitive? pr)) (out (if (primapp eq? (primapp ~pr ~v0 ...) (quoted-const false)) (fls) (tru)))]
    [(primapp ~pr ~[v0] ...) (guard (effect-primitive? pr)) (out (begin (primapp ~pr ~v0 ...) (tru)))]
    [(sys-primapp ~spr ~[v0] ...) (guard (predicate-primitive? spr)) (out (sys-primapp ~spr ~v0 ...))]
    [(sys-primapp ~spr ~[v0] ...) (guard (value-primitive? spr)) (out (if (primapp eq? (sys-primapp ~spr ~v0 ...) (quoted-const false)) (fls) (tru)))]
    [(sys-primapp ~spr ~[v0] ...) (guard (effect-primitive? spr)) (out (begin (sys-primapp ~spr ~v0 ...) (tru)))]
    [(anonymous-call ~[v0] ~[v1] ...) (out (if (primapp eq? (anonymous-call ~v0 ~v1 ...) (quoted-const false)) (fls) (tru)))]
    [(app ~[v0] ~[v1] ...) (out (if (primapp eq? (app ~v0 ~v1 ...) (quoted-const false)) (fls) (tru)))])
  (Effect Expr [ir] -> Effect []
    [(var ~x) (out (nop))]
    [(quoted-const ~d) (out (nop))]
    [(if ~[p0] ~[f1] ~[f2]) (out (if ~p0 ~f1 ~f2))]
    [(begin ~[f0] ... ~[f1]) (out (begin ~f0 ... ~f1))]
    [(let ((~x ~[v]) ...) ~[f]) (out (let ((~x ~v) ...) ~f))]
    [(primapp ~pr ~[f]) (guard (= pr 'not)) f]
    [(primapp ~pr ~[f0] ...) (guard (or (predicate-primitive? pr) (value-primitive? pr))) (if (empty? f0) (out (nop)) (out (begin ~f0 ... (nop))))]
    [(primapp ~pr ~[v0] ...) (guard (effect-primitive? pr)) (out (primapp ~pr ~v0 ...))]
    [(sys-primapp ~spr ~[f0] ...) (guard (or (predicate-primitive? spr) (value-primitive? spr))) (if (empty? f0) (out (nop)) (out (begin ~f0 ... (nop))))]
    [(sys-primapp ~spr ~[v0] ...) (guard (effect-primitive? spr)) (out (sys-primapp ~spr ~v0 ...))]
    [(anonymous-call ~[v0] ~[v1] ...) (out (anonymous-call ~v0 ~v1 ...))]
    [(app ~[v0] ~[v1] ...) (out (app ~v0 ~v1 ...))]))

(define-language L13
  (terminals (variable (x)) (datum (d)) (value-primitive (vp)) (predicate-primitive (pp)) (effect-primitive (ep)) (system-primitive (spr)))
  (LetrecExpr (lrexpr) (letrec ((x lexpr) ...) v))
  (LambdaExpr (lexpr) (lambda (x ...) v))
  (Triv (t) (var x) (quoted-const d))
  (Value (v)
    t
    (if p1 v2 v3)
    (begin f0 ... v1)
    (let ((x v1) ...) v2)
    (primapp vp t ...)
    (sys-primapp spr t ...)
    (anonymous-call t0 t1 ...)
    (app t0 t1 ...))
  (Predicate (p)
    (tru)
    (fls)
    (if p1 p2 p3)
    (begin f0 ... p1)
    (let ((x v) ...) p)
    (primapp pp t ...)
    (sys-primapp spr t ...)
    (anonymous-call t0 t1 ...)
    (app t0 t1 ...))
  (Effect (f)
    (nop)
    (if p1 f2 f3)
    (begin f0 ... f1)
    (let ((x v) ...) f)
    (primapp ep t ...)
    (sys-primapp spr t ...)
    (anonymous-call t0 t1 ...)
    (app t0 t1 ...)))

(define-parser parse-L13 L13)

(define-pass remove-complex-opera* L12 [ir] -> L13 []
  (definitions
    (defn remove-nulls [ls] (remove empty? ls))
    (defn with-bindings [binding* v] ; binding*：[(tmp value) …]；有则包一层 let
      (if (empty? binding*) v (in-context Value (out (let ((~(mapv first binding*) ~(mapv second binding*)) ...) ~v))))))
  (LetrecExpr [ir] -> LetrecExpr []
    [(letrec ((~x ~[lexpr]) ...) ~[v]) (out (letrec ((~x ~lexpr) ...) ~v))])
  (LambdaExpr [ir] -> LambdaExpr []
    [(lambda (~x ...) ~[v]) (out (lambda (~x ...) ~v))])
  (Opera Value [ir] -> Triv [dummy]
    [(var ~x) [(out (var ~x)) ()]]
    [(quoted-const ~d) [(out (quoted-const ~d)) ()]]
    [(if ~[p1] ~[v2] ~[v3])
     (let [tmp (gen-symbol 'tmp)] [(out (var ~tmp)) (list tmp (in-context Value (out (if ~p1 ~v2 ~v3))))])]
    [(begin ~[f0] ... ~[v1])
     (let [tmp (gen-symbol 'tmp)] [(out (var ~tmp)) (list tmp (in-context Value (out (begin ~f0 ... ~v1))))])]
    [(let ((~x ~[v1]) ...) ~[v2])
     (let [tmp (gen-symbol 'tmp)] [(out (var ~tmp)) (list tmp (in-context Value (out (let ((~x ~v1) ...) ~v2))))])]
    [(primapp ~vp ~[t* binding*] ...)
     (let [tmp (gen-symbol 'tmp)]
       [(out (var ~tmp)) (list tmp (with-bindings (remove-nulls binding*) (in-context Value (out (primapp ~vp ~t* ...)))))])]
    [(sys-primapp ~spr ~[t* binding*] ...)
     (let [tmp (gen-symbol 'tmp)]
       [(out (var ~tmp)) (list tmp (with-bindings (remove-nulls binding*) (in-context Value (out (sys-primapp ~spr ~t* ...)))))])]
    [(anonymous-call ~[v0 binding] ~[v1 binding*] ...)
     (let [tmp (gen-symbol 'tmp)]
       [(out (var ~tmp)) (list tmp (with-bindings (remove-nulls (cons binding binding*)) (in-context Value (out (anonymous-call ~v0 ~v1 ...)))))])]
    [(app ~[v0] ~[t* binding*] ...)
     (let [tmp (gen-symbol 'tmp)]
       [(out (var ~tmp)) (list tmp (with-bindings (remove-nulls binding*) (in-context Value (out (app ~v0 ~t* ...)))))])])
  (Value [ir] -> Value []
    [(var ~x) (in-context Triv (out (var ~x)))]
    [(quoted-const ~d) (in-context Triv (out (quoted-const ~d)))]
    [(if ~[p1] ~[v2] ~[v3]) (out (if ~p1 ~v2 ~v3))]
    [(begin ~[f0] ... ~[v1]) (out (begin ~f0 ... ~v1))]
    [(let ((~x ~[v1]) ...) ~[v2]) (out (let ((~x ~v1) ...) ~v2))]
    [(primapp ~vp ~[t* binding*] ...) (with-bindings (remove-nulls binding*) (out (primapp ~vp ~t* ...)))]
    [(sys-primapp ~spr ~[t* binding*] ...) (with-bindings (remove-nulls binding*) (out (sys-primapp ~spr ~t* ...)))]
    [(anonymous-call ~[t0 binding] ~[t1 binding*] ...) (with-bindings (remove-nulls (cons binding binding*)) (out (anonymous-call ~t0 ~t1 ...)))]
    [(app ~[v0] ~[t* binding*] ...) (with-bindings (remove-nulls binding*) (out (app ~v0 ~t* ...)))])
  (Predicate [ir] -> Predicate []
    [(let ((~x ~[v]) ...) ~[p]) (out (let ((~x ~v) ...) ~p))]
    [(primapp ~pp ~[t* binding*] ...)
     (let [binding* (remove-nulls binding*)]
       (if (empty? binding*) (out (primapp ~pp ~t* ...)) (out (let ((~(mapv first binding*) ~(mapv second binding*)) ...) (primapp ~pp ~t* ...)))))]
    [(sys-primapp ~spr ~[t* binding*] ...)
     (let [binding* (remove-nulls binding*)]
       (if (empty? binding*) (out (sys-primapp ~spr ~t* ...)) (out (let ((~(mapv first binding*) ~(mapv second binding*)) ...) (sys-primapp ~spr ~t* ...)))))]
    [(anonymous-call ~[t0 binding] ~[t1 binding*] ...)
     (let [binding* (remove-nulls (cons binding binding*))]
       (if (empty? binding*) (out (anonymous-call ~t0 ~t1 ...)) (out (let ((~(mapv first binding*) ~(mapv second binding*)) ...) (anonymous-call ~t0 ~t1 ...)))))]
    [(app ~[v0] ~[t* binding*] ...)
     (let [binding* (remove-nulls binding*)]
       (if (empty? binding*) (out (app ~v0 ~t* ...)) (out (let ((~(mapv first binding*) ~(mapv second binding*)) ...) (app ~v0 ~t* ...)))))])
  (Effect [ir] -> Effect []
    [(let ((~x ~[v]) ...) ~[f]) (out (let ((~x ~v) ...) ~f))]
    [(primapp ~ep ~[t* binding*] ...)
     (let [binding* (remove-nulls binding*)]
       (if (empty? binding*) (out (primapp ~ep ~t* ...)) (out (let ((~(mapv first binding*) ~(mapv second binding*)) ...) (primapp ~ep ~t* ...)))))]
    [(sys-primapp ~spr ~[t* binding*] ...)
     (let [binding* (remove-nulls binding*)]
       (if (empty? binding*) (out (sys-primapp ~spr ~t* ...)) (out (let ((~(mapv first binding*) ~(mapv second binding*)) ...) (sys-primapp ~spr ~t* ...)))))]
    [(anonymous-call ~[t0 binding] ~[t1 binding*] ...)
     (let [binding* (remove-nulls (cons binding binding*))]
       (if (empty? binding*) (out (anonymous-call ~t0 ~t1 ...)) (out (let ((~(mapv first binding*) ~(mapv second binding*)) ...) (anonymous-call ~t0 ~t1 ...)))))]
    [(app ~[v0] ~[t* binding*] ...)
     (let [binding* (remove-nulls binding*)]
       (if (empty? binding*) (out (app ~v0 ~t* ...)) (out (let ((~(mapv first binding*) ~(mapv second binding*)) ...) (app ~v0 ~t* ...)))))]))

;; —— L14 ~ L18 ——

(define-language L14 (extends L13)
  (entry LetrecExpr)
  (Value (v) (- (anonymous-call t0 t1 ...)))
  (Predicate (p) (- (anonymous-call t0 t1 ...)))
  (Effect (f) (- (anonymous-call t0 t1 ...))))

(define-pass remove-anonymous-call L13 [ir] -> L14 []
  (Value [ir] -> Value []
    [(anonymous-call ~[t0] ~[t1] ...)
     (let [tmp (gen-symbol 'tmp)] (out (let ([~tmp (sys-primapp procedure-code ~t0)]) (app (var ~tmp) ~t0 ~t1 ...))))])
  (Predicate [ir] -> Predicate []
    [(anonymous-call ~[t0] ~[t1] ...)
     (let [tmp (gen-symbol 'tmp)] (out (let ([~tmp (sys-primapp procedure-code ~t0)]) (app (var ~tmp) ~t0 ~t1 ...))))])
  (Effect [ir] -> Effect []
    [(anonymous-call ~[t0] ~[t1] ...)
     (let [tmp (gen-symbol 'tmp)] (out (let ([~tmp (sys-primapp procedure-code ~t0)]) (app (var ~tmp) ~t0 ~t1 ...))))]))

(define-parser parse-L14 L14)

(define-language L15
  (terminals (variable (x)) (datum (d)) (value-primitive (vp)) (predicate-primitive (pp)) (effect-primitive (ep)) (system-primitive (spr)))
  (LetrecExpr (lrexpr) (letrec ((x1 lexpr) ...) rnexpr))
  (RunExpr (rnexpr) (run (x) tl))
  (LambdaExpr (lexpr) (lambda (x ...) tl))
  (Triv (t) (var x) (quoted-const d))
  (Application (a) (app t0 t1 ...))
  (Tail (tl)
    (return t1 t2)
    (if p1 tl2 tl3)
    (begin f0 ... tl1)
    (let ((x ntl1) ...) tl2)
    (app t0 t1 ...))
  (Nontail (ntl)
    t
    (if p1 ntl2 ntl3)
    (begin f0 ... ntl1)
    (let ((x ntl1) ...) ntl2)
    (primapp vp t ...)
    (sys-primapp spr t ...)
    (return-point x a))
  (Predicate (p)
    (tru)
    (fls)
    (if p1 p2 p3)
    (begin f0 ... p1)
    (let ((x ntl) ...) p)
    (primapp pp t ...)
    (sys-primapp spr t ...))
  (Effect (f)
    (nop)
    (if p1 f2 f3)
    (begin f0 ... f1)
    (let ((x ntl) ...) f)
    (primapp ep t ...)
    (sys-primapp spr t ...)
    (return-point x a)))

(define-parser parse-L15 L15)

(define-pass introduce-dummy-rp L14 [ir] -> L15 []
  (LetrecExpr [ir] -> LetrecExpr []
    [(letrec ((~x ~[lexpr]) ...) ~[rnexpr]) (out (letrec ((~x ~lexpr) ...) ~rnexpr))])
  (LambdaExpr [ir] -> LambdaExpr []
    [(lambda (~x ...) ~v)
     (let [rp (gen-symbol 'rp)
           tl (ValueTail v rp)]
       (out (lambda (~rp ~x ...) ~tl)))])
  (ValueRun Value [ir] -> RunExpr []
    [(var ~x) (let [rp (gen-symbol 'rp)] (out (run (~rp) (return (var ~rp) (var ~x)))))]
    [(quoted-const ~d) (let [rp (gen-symbol 'rp)] (out (run (~rp) (return (var ~rp) (quoted-const ~d)))))]
    [(if ~[p1] ~v2 ~v3)
     (let [rp (gen-symbol 'rp)
           tl2 (ValueTail v2 rp) tl3 (ValueTail v3 rp)]
       (out (run (~rp) (if ~p1 ~tl2 ~tl3))))]
    [(begin ~[f0] ... ~v1)
     (let [rp (gen-symbol 'rp)
           tl1 (ValueTail v1 rp)]
       (out (run (~rp) (begin ~f0 ... ~tl1))))]
    [(let ((~x ~[ntl1]) ...) ~v2)
     (let [rp (gen-symbol 'rp)
           tl2 (ValueTail v2 rp)]
       (out (run (~rp) (let ((~x ~ntl1) ...) ~tl2))))]
    [(primapp ~vp ~[t] ...)
     (let [rp (gen-symbol 'rp) tmp (gen-symbol 'tmp)]
       (out (run (~rp) (let ([~tmp (primapp ~vp ~t ...)]) (return (var ~rp) (var ~tmp))))))]
    [(sys-primapp ~spr ~[t] ...)
     (let [rp (gen-symbol 'rp) tmp (gen-symbol 'tmp)]
       (out (run (~rp) (let ([~tmp (sys-primapp ~spr ~t ...)]) (return (var ~rp) (var ~tmp))))))]
    [(app ~[t0] ~[t1] ...)
     (let [rp (gen-symbol 'rp)] (out (run (~rp) (app ~t0 (var ~rp) ~t1 ...))))])
  (ValueTail Value [ir rp] -> Tail []
    [(var ~x) (out (return (var ~rp) (var ~x)))]
    [(quoted-const ~d) (out (return (var ~rp) (quoted-const ~d)))]
    [(if ~[p1] ~[ValueTail :> v2 rp -> tl2] ~[ValueTail :> v3 rp -> tl3]) (out (if ~p1 ~tl2 ~tl3))]
    [(begin ~[f0] ... ~[ValueTail :> v1 rp -> tl1]) (out (begin ~f0 ... ~tl1))]
    [(let ((~x ~[ntl1]) ...) ~[ValueTail :> v2 rp -> tl2]) (out (let ((~x ~ntl1) ...) ~tl2))]
    [(primapp ~vp ~[t] ...)
     (let [tmp (gen-symbol 'tmp)] (out (let ([~tmp (primapp ~vp ~t ...)]) (return (var ~rp) (var ~tmp)))))]
    [(sys-primapp ~spr ~[t] ...)
     (let [tmp (gen-symbol 'tmp)] (out (let ([~tmp (sys-primapp ~spr ~t ...)]) (return (var ~rp) (var ~tmp)))))]
    [(app ~[t0] ~[t1] ...) (out (app ~t0 (var ~rp) ~t1 ...))])
  (ValueNTail Value [ir] -> Nontail []
    [(if ~[p1] ~[ntl2] ~[ntl3]) (out (if ~p1 ~ntl2 ~ntl3))]
    [(begin ~[f0] ... ~[ntl1]) (out (begin ~f0 ... ~ntl1))]
    [(let ((~x ~[ntl1]) ...) ~[ntl2]) (out (let ((~x ~ntl1) ...) ~ntl2))]
    [(app ~[t0] ~[t1] ...)
     (let [label (gen-label (gen-symbol 'lab))] (out (return-point ~label (app ~t0 (var ~label) ~t1 ...))))])
  (Predicate [ir] -> Predicate []
    [(let ((~x ~[ntl1]) ...) ~[p]) (out (let ((~x ~ntl1) ...) ~p))])
  (Effect [ir] -> Effect []
    [(let ((~x ~[ntl1]) ...) ~[f]) (out (let ((~x ~ntl1) ...) ~f))]
    [(app ~[t0] ~[t1] ...)
     (let [label (gen-label (gen-symbol 'lab))] (out (return-point ~label (app ~t0 (var ~label) ~t1 ...))))]))

(define-language L16 (extends L15)
  (entry LetrecExpr)
  (Tail (tl)
    (- (let ((x ntl1) ...) tl2))
    (+ (let ((x ntl1)) tl2)))
  (Nontail (ntl)
    (- (let ((x ntl1) ...) ntl2))
    (+ (let ((x ntl1)) ntl2)))
  (Predicate (p)
    (- (let ((x ntl) ...) p))
    (+ (let ((x ntl)) p)))
  (Effect (f)
    (- (let ((x ntl) ...) f))
    (+ (let ((x ntl)) f))))

(define-parser parse-L16 L16)

(define-pass remove-nonunary-let L15 [ir] -> L16 []
  (Tail [ir] -> Tail []
    [(let ((~x ~[ntl]) ...) ~[tl])
     (reduce (fn [tl [x ntl]] (out (let ((~x ~ntl)) ~tl))) tl (reverse (map vector x ntl)))])
  (Nontail [ir] -> Nontail []
    [(let ((~x ~[ntl1]) ...) ~[ntl2])
     (reduce (fn [ntl2 [x ntl1]] (out (let ((~x ~ntl1)) ~ntl2))) ntl2 (reverse (map vector x ntl1)))])
  (Predicate [ir] -> Predicate []
    [(let ((~x ~[ntl]) ...) ~[p])
     (reduce (fn [p [x ntl]] (out (let ((~x ~ntl)) ~p))) p (reverse (map vector x ntl)))])
  (Effect [ir] -> Effect []
    [(let ((~x ~[ntl]) ...) ~[f])
     (reduce (fn [f [x ntl]] (out (let ((~x ~ntl)) ~f))) f (reverse (map vector x ntl)))]))

(define-language L17 (extends L16)
  (entry LetrecExpr)
  (RunExpr (rnexpr)
    (- (run (x) tl))
    (+ (run (x) dec)))
  (LambdaExpr (lexpr)
    (- (lambda (x ...) tl))
    (+ (lambda (x ...) dec)))
  (DeclareExpr (dec) (+ (declare (x ...) tl)))
  (Tail (tl) (- (let ((x ntl1)) tl2)))
  (Nontail (ntl)
    (- t
       (if p1 ntl2 ntl3)
       (begin f0 ... ntl1)
       (let ((x ntl1)) ntl2)
       (primapp vp t ...)
       (sys-primapp spr t ...)
       (return-point x a)))
  (RhsExpr (rhs)
    (+ t
       (if p1 rhs2 rhs3)
       (begin f0 ... rhs1)
       (primapp vp t ...)
       (sys-primapp spr t ...)
       (return-point x a)))
  (Predicate (p) (- (let ((x ntl)) p)))
  (Effect (f)
    (- (let ((x ntl)) f))
    (+ (set! x rhs))))

(define-parser parse-L17 L17)

(define-pass return-of-set! L16 [ir] -> L17 []
  (definitions
    (defn Effect* [f* var*]
      (reduce (fn [[fs var*] f] (let [[f var*] (Effect f var*)] [(conj fs f) var*])) [[] var*] f*)))
  (RunExpr [ir] -> RunExpr []
    [(run (~x) ~[tl () -> tl var*]) (out (run (~x) (declare (~var* ...) ~tl)))])
  (LambdaExpr [ir] -> LambdaExpr []
    [(lambda (~x* ...) ~[tl () -> tl var*]) (out (lambda (~x* ...) (declare (~var* ...) ~tl)))])
  (Tail [ir var*] -> Tail [var*]
    [(let ([~x ~ntl]) ~[tl var*])
     (let [[rhs var*] (Nontail ntl var*)] [(out (begin (set! ~x ~rhs) ~tl)) (cons x var*)])]
    [(if ~[p1 var*] ~tl2 ~tl3)
     (let [[tl2 var*] (Tail tl2 var*)
           [tl3 var*] (Tail tl3 var*)]
       [(out (if ~p1 ~tl2 ~tl3)) var*])]
    [(begin ~f* ... ~[tl var*])
     (let [[f* var*] (Effect* f* var*)] [(out (begin ~f* ... ~tl)) var*])])
  (Nontail [ir var*] -> RhsExpr [var*]
    [(let ((~x ~ntl1)) ~[rhs2 var*])
     (let [[rhs1 var*] (Nontail ntl1 var*)] [(out (begin (set! ~x ~rhs1) ~rhs2)) (cons x var*)])]
    [(if ~[p1 var*] ~ntl2 ~ntl3)
     (let [[rhs2 var*] (Nontail ntl2 var*)
           [rhs3 var*] (Nontail ntl3 var*)]
       [(out (if ~p1 ~rhs2 ~rhs3)) var*])]
    [(begin ~f* ... ~[rhs var*])
     (let [[f* var*] (Effect* f* var*)] [(out (begin ~f* ... ~rhs)) var*])]
    [(quoted-const ~d) [(out (quoted-const ~d)) var*]]
    [(var ~x) [(out (var ~x)) var*]])
  (Effect [ir var*] -> Effect [var*]
    [(let ([~x ~ntl]) ~[f var*])
     (let [[rhs var*] (Nontail ntl var*)] [(out (begin (set! ~x ~rhs) ~f)) var*])]
    [(if ~[p1 var*] ~f2 ~f3)
     (let [[f2 var*] (Effect f2 var*)
           [f3 var*] (Effect f3 var*)]
       [(out (if ~p1 ~f2 ~f3)) var*])]
    [(begin ~f* ... ~[f var*])
     (let [[f* var*] (Effect* f* var*)] [(out (begin ~f* ... ~f)) var*])])
  (Predicate [ir var*] -> Predicate [var*]
    [(let ([~x ~ntl]) ~[p var*])
     (let [[rhs var*] (Nontail ntl var*)] [(out (begin (set! ~x ~rhs) ~p)) (cons x var*)])]
    [(if ~[p1 var*] ~p2 ~p3)
     (let [[p2 var*] (Predicate p2 var*)
           [p3 var*] (Predicate p3 var*)]
       [(out (if ~p1 ~p2 ~p3)) var*])]
    [(begin ~f* ... ~[p var*])
     (let [[f* var*] (Effect* f* var*)] [(out (begin ~f* ... ~p)) var*])]))

(define-language L18 (extends L17)
  (entry LetrecExpr)
  (Triv (t) (+ (label x))))

(define-parser parse-L18 L18)

(define-pass explicit-labels L17 [ir] -> L18 []
  (LetrecExpr [ir [labs ()]] -> LetrecExpr []
    [(letrec ((~x ~[lexpr x -> lexpr]) ...) ~[rnexpr x -> rnexpr]) (out (letrec ((~x ~lexpr) ...) ~rnexpr))])
  (LambdaExpr [ir labs] -> LambdaExpr [])
  (Triv [ir labs] -> Triv []
    [(var ~x) (if (some #{x} labs) (out (label ~x)) (out (var ~x)))])
  (Application [ir labs] -> Application [])
  (DeclareExpr [ir labs] -> DeclareExpr [])
  (RunExpr [ir labs] -> RunExpr [])
  (Tail [ir labs] -> Tail [])
  (RhsExpr [ir labs] -> RhsExpr []
    [(return-point ~x ~a) (let [a (Application a (cons x labs))] (out (return-point ~x ~a)))])
  (Predicate [ir labs] -> Predicate [])
  (Effect [ir labs] -> Effect []
    [(return-point ~x ~a) (let [a (Application a (cons x labs))] (out (return-point ~x ~a)))]))

;; —— 上游 compiler-test.ss 的 pass 链（与 test-driver.ss 的 test-one：跑完所有 pass、不报错）——

(def passes
  [rename-vars-verify-legal remove-implicit-begin remove-unquoted-constant remove-one-armed-if uncover-settable remove-impure-letrec
   remove-set! sanitize-binding remove-anonymous-lambda uncover-free convert-closure lift-letrec explicit-closure normalize-context
   remove-complex-opera* remove-anonymous-call introduce-dummy-rp remove-nonunary-let return-of-set! explicit-labels])

(def pass-names
  '[rename-vars/verify-legal remove-implicit-begin remove-unquoted-constant remove-one-armed-if uncover-settable remove-impure-letrec
    remove-set! sanitize-binding remove-anonymous-lambda uncover-free convert-closure lift-letrec explicit-closure normalize-context
    remove-complex-opera* remove-anonymous-call introduce-dummy-rp remove-nonunary-let return-of-set! explicit-labels])

(defn run-passes
  "表达式经全部 pass；返回 L18 的节点。出错时 ex-info 带 :pass 与 :input（unparse 过）。"
  [expr]
  (reset-seed)
  (reduce (fn [ir [nm p]]
            (try (p ir)
                 (catch Exception e
                   (throw (ex-info (str nm ": " (.Message e)) {:pass nm :input (np/unparse ir)} e)))))
          expr (map vector pass-names passes)))

(defn test-one [expr] (run-passes expr) true)
