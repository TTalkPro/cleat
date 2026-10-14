;; lower-loops：L0 → L0，接在前端之后（cleat-084 试点，移植原版 loops.sls（"原版"指 cleat-093 前仓库里的 Scheme 实现 src/scheme，已删除，见 git 历史），IronScheme iron-034b）。
;;
;;   D9  单绑定 letrec 的 lambda，全部引用都是参数个数相符的尾调用 ⇒ (label f (x …) body fbody)，调用 ⇒ (jump f e …)
;;   let 绑定、只在尾位置调用的 lambda 也变成 label
;; 原版专有的部分（call-with-values 内联、escape-frame、try-handle）cleat-092 删去。
(ns cleat.compiler.loops
  (:require [cleat.nanopass :refer [define-pass nanopass-case with-output-language out]]
            [cleat.compiler.languages :refer [L0]]))

;; —— 通用工具 ——

(defn clause-body [cl]
  (nanopass-case [L0 Clause] cl [(clause (~x* ...) ~x ~body) body]))

(defn kids
  "子表达式（不含绑定信息）。"
  [e]
  (nanopass-case [L0 Expr] e
    [~x []]
    [(quote ~d) []]
    [(if ~e0 ~e1 ~e2) [e0 e1 e2]]
    [(begin ~e* ... ~e) (conj (vec e*) e)]
    [(set! ~x ~e) [e]]
    [(lambda ~nm ~cl* ...) (mapv clause-body cl*)]
    [(letrec ([~x* ~e*] ...) ~body) (conj (vec e*) body)]
    [(let ([~x* ~e*] ...) ~body) (conj (vec e*) body)]
    [(call ~e ~e* ...) (vec (cons e e*))]
    [(throw ~e) [e]]
    [(try-finally ~e0 ~e1) [e0 e1]]
    [(try-catch ~e (~d* ~e*) ...) (vec (cons e e*))]
    [(label ~x (~x* ...) ~body0 ~body1) [body0 body1]]
    [(jump ~x ~e* ...) e*]
    [(clj-var-ref ~d ~st) []]
    [(clj-var-set! ~d ~e) [e]]
    [(clj-def ~d ~st0 ~st1 ~e0 ~e1) [e0 e1]]
    [(clj-invoke ~e ~e* ...) (vec (cons e e*))]
    [(clj-keyword-invoke ~d ~e ~e* ...) (vec (cons e e*))]
    [(clj-call ~d0 ~d1 ~e* ...) e*]
    [(clj-static-call ~d0 ~d1 ~e* ...) e*]
    [(clj-case ~d ~e0 ~e1 ~e* ...) (vec (list* e0 e1 e*))]
    [(clj-deftype ~d (~x* ...) ~le* ...) le*]))

(defn var-ref? [e v]
  (nanopass-case [L0 Expr] e [~x (= x v)] [else false]))

(defn simple-lambda
  "单子句、无剩余参数的 lambda：[形参表 体]，否则 [nil nil]。"
  [e]
  (nanopass-case [L0 Expr] e
    [(lambda ~nm ~cl)
     (nanopass-case [L0 Clause] cl
       [(clause (~x* ...) ~x ~body) (if x [nil nil] [x* body])])]
    [else [nil nil]]))

;; 临时变量名按编译单元从头编号（驱动在每个单元开始时调用），输出确定
(def ^:private counter (atom 0))
(defn reset-fresh-names! [] (reset! counter 0))
(defn fresh [nm] (symbol (str nm "%" (swap! counter inc))))
(defn fresh-name [nm] (fresh nm))

(defn check-refs
  "v 在 e 中的引用是否都是参数个数为 n 的尾调用。返回 nil（合格）或原因：
  :value、:arity、:non-tail、:closure（在嵌套 lambda 里引用）、:assigned。"
  [v n e tail? in-lambda?]
  (letfn [(r [e tail?] (check-refs v n e tail? in-lambda?))
          (r* [es] (some #(r % false) es))]
    (nanopass-case [L0 Expr] e
      [~x (when (= x v) (if in-lambda? :closure :value))]
      [(set! ~x ~e) (if (= x v) :assigned (r e false))]
      [(call ~e ~e* ...)
       (if (var-ref? e v)
         (cond
           in-lambda? :closure
           (not= (count e*) n) :arity
           (not tail?) :non-tail
           :else (r* e*))
         ;; 立即调用的 lambda：体在尾位置
         (let [[x* body] (simple-lambda e)]
           (if (and x* (= (count x*) (count e*)))
             (or (r* e*) (r body tail?))
             (or (r e false) (r* e*)))))]
      [(if ~e0 ~e1 ~e2) (or (r e0 false) (r e1 tail?) (r e2 tail?))]
      [(begin ~e* ... ~e) (or (r* e*) (r e tail?))]
      [(clj-case ~d ~e0 ~e1 ~e* ...) (or (r e0 false) (r e1 tail?) (some #(r % tail?) e*))]
      [(let ([~x* ~e*] ...) ~body) (or (r* e*) (r body tail?))]
      [(letrec ([~x* ~e*] ...) ~body) (or (r* e*) (r body tail?))]
      [(lambda ~nm ~cl* ...) (some #(check-refs v n (clause-body %) false true) cl*)]
      [else (r* (kids e))])))

(defn refs?
  "e 是否引用变量 v（含嵌套 lambda 内）。"
  [v e]
  (nanopass-case [L0 Expr] e
    [~x (= x v)]
    [(set! ~x ~e) (or (= x v) (refs? v e))]
    [else (boolean (some #(refs? v %) (kids e)))]))

(defn jumpify
  "把 node 的尾位置上对 v 的调用改成 jump。"
  [v node]
  (with-output-language [L0 Expr]
    (nanopass-case [L0 Expr] node
      [(call ~e ~e* ...)
       (if (var-ref? e v)
         (out (jump ~v ~e* ...))
         (let [[x* body] (simple-lambda e)]
           (if (and x* (= (count x*) (count e*)))
             (out (call (lambda false (clause (~x* ...) false ~(jumpify v body))) ~e* ...))
             node)))]
      [(if ~e0 ~e1 ~e2) (out (if ~e0 ~(jumpify v e1) ~(jumpify v e2)))]
      [(begin ~e* ... ~e) (out (begin ~e* ... ~(jumpify v e)))]
      [(clj-case ~d ~e0 ~e1 ~e* ...) (out (clj-case ~d ~e0 ~(jumpify v e1) ~(map #(jumpify v %) e*) ...))]
      [(let ([~x* ~e*] ...) ~body) (out (let ([~x* ~e*] ...) ~(jumpify v body)))]
      [(letrec ([~x* ~e*] ...) ~body) (out (letrec ([~x* ~e*] ...) ~(jumpify v body)))]
      [else node])))

;; —— lower-loops（iron-034b）——

;; 至今生成的 label 个数：编译驱动据此只对含循环的形式做类型推断（codegen 的 loop-forms）
(def ^:private loops-count (atom 0))
(defn loops-made [] @loops-count)

(define-pass lower-loops L0 [ir] -> L0 []
  (definitions
    ;; let 绑定的单子句 lambda，体里只在尾位置按相符的参数个数调用：变成 label（见 原版）
    (defn lower-let [x* e* body]
      (loop [xs (seq x*) es (seq e*) keep-x () keep-e () labels ()]
        (if xs
          (let [[p* fbody] (simple-lambda (first es))]
            (if (and p* (not (check-refs (first xs) (count p*) body true false)))
              (recur (next xs) (next es) keep-x keep-e (cons [(first xs) p* fbody] labels))
              (recur (next xs) (next es) (cons (first xs) keep-x) (cons (first es) keep-e) labels)))
          (if (empty? labels)
            (out (let ([~x* ~(mapv Expr e*)] ...) ~(Expr body)))
            (let [jbody (reduce (fn [b l] (jumpify (first l) b)) body labels)
                  inner (reduce (fn [b [f ps fb]]
                                  (swap! loops-count inc)
                                  (out (label ~f (~ps ...) ~b ~(Expr fb))))
                                (Expr jbody)
                                labels)]
              (if (empty? keep-x)
                inner
                (out (let ([~(reverse keep-x) ~(mapv Expr (reverse keep-e))] ...) ~inner))))))))
    (defn named-letrec? [e]
      (nanopass-case [L0 Expr] e
        [(letrec ([~x* ~e*] ...) ~body)
         (when (and (= (count x*) 1) (var-ref? body (first x*))) [(first x*) (first e*)])]
        [else nil])))
  (Expr [ir] -> Expr []
    [(call ~e ~e* ...)
     (if-let [[f le] (named-letrec? e)]
       (Expr (out (letrec ([~f ~le]) (call ~f ~e* ...))))
       (let [[p* b] (simple-lambda e)]
         (if (and p* (= (count p*) (count e*)))
           (lower-let p* e* b)                  ; 立即调用的 lambda 就是 let
           (out (call ~(Expr e) ~(mapv Expr e*) ...)))))]
    [(let ([~x* ~e*] ...) ~body) (lower-let x* e* body)]
    [(letrec ([~x* ~e*] ...) ~body)
     (let [[p* fbody] (if (= (count x*) 1) (simple-lambda (first e*)) [nil nil])]
       (if (and p*
                (let [f (first x*) n (count p*)]
                  (not (or (check-refs f n body true false) (check-refs f n fbody true false)))))
         (let [f (first x*)]
           (swap! loops-count inc)
           (out (label ~f (~p* ...) ~(Expr (jumpify f body)) ~(Expr (jumpify f fbody)))))
         (out (letrec ([~x* ~(mapv Expr e*)] ...) ~(Expr body)))))]))
