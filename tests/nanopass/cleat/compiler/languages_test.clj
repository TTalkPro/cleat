;; cleat.compiler.languages 的验收（cleat-083）：Scheme 编译器的快照（tests/scheme/compiler/snapshots/*.l0 / .l2 / .l3，
;; Scheme 的 unparse 输出）读进来，按对应的 Clojure 语言解析成记录再 unparse，得到相同的数据。
(ns cleat.compiler.languages-test
  (:require [clojure.test :refer [deftest is testing]]
            [clojure.string :as str]
            [cleat.nanopass :as np]
            [cleat.compiler.languages :as l]))

;; —— Scheme 数据的读入（快照里只有：列表、'x、字符串、#t / #f、#\字符、#(向量)、数、符号）——
;; #f → false、#t → true；向量 → Clojure 向量（只出现在 quote 的数据里）

(defn- scheme-tokens [^String s]
  (let [n (count s)]
    (loop [i 0 out []]
      (if (>= i n)
        out
        (let [c (nth s i)]
          (cond
            (Char/IsWhiteSpace c) (recur (inc i) out)
            (= c \;) (recur (loop [j i] (if (or (>= j n) (= (nth s j) \newline)) j (recur (inc j)))) out)
            (#{\( \) \[ \] \'} c) (recur (inc i) (conj out (str c)))
            (and (= c \#) (< (inc i) n) (= (nth s (inc i)) \()) (recur (+ i 2) (conj out "#("))
            (= c \") (let [sb (StringBuilder.)
                           j (loop [j (inc i)]
                               (let [d (nth s j)]
                                 (cond (= d \") (inc j)
                                       (= d \\) (do (.Append sb (case (nth s (inc j)) \n \newline \t \tab (nth s (inc j)))) (recur (+ j 2)))
                                       :else (do (.Append sb d) (recur (inc j))))))]
                       (recur j (conj out {:string (str sb)})))
            (and (= c \#) (< (inc i) n) (= (nth s (inc i)) \\))
            (let [j (loop [j (+ i 3)] (if (or (>= j n) (Char/IsWhiteSpace (nth s j)) (#{\( \)} (nth s j))) j (recur (inc j))))]
              (recur j (conj out {:char (subs s (+ i 2) j)})))
            :else (let [j (loop [j i] (if (or (>= j n) (Char/IsWhiteSpace (nth s j)) (#{\( \) \[ \]} (nth s j))) j (recur (inc j))))]
                    (recur j (conj out {:atom (subs s i j)})))))))))

(defn- atom-value [a]
  (cond
    (= a "#t") true
    (= a "#f") false
    (re-matches #"[+-]?\d+" a) (parse-long a)
    (re-matches #"[+-]?\d+\.\d*(e[+-]?\d+)?" a) (parse-double a)
    :else (symbol a)))

(defn- read-scheme-all [s]
  (letfn [(rd [ts]
            (let [t (first ts)]
              (cond
                (#{"(" "["} t) (loop [ts (rest ts) acc []]
                                 (if (#{")" "]"} (first ts)) [(apply list acc) (rest ts)]
                                     (let [[v r] (rd ts)] (recur r (conj acc v)))))
                (= "#(" t) (loop [ts (rest ts) acc []]
                             (if (= ")" (first ts)) [acc (rest ts)]
                                 (let [[v r] (rd ts)] (recur r (conj acc v)))))
                (= "'" t) (let [[v r] (rd (rest ts))] [(list 'quote v) r])
                (:string t) [(:string t) (rest ts)]
                (:char t) [(let [c (:char t)] (if (= 1 (count c)) (first c) (symbol (str "#\\" c)))) (rest ts)]
                :else [(atom-value (:atom t)) (rest ts)])))]
    (loop [ts (scheme-tokens s) out []]
      (if (empty? ts) out (let [[v r] (rd ts)] (recur r (conj out v)))))))

(def snapshot-dir
  (System.IO.Path/Combine (System.IO.Directory/GetCurrentDirectory) "tests" "scheme" "compiler" "snapshots"))

(deftest scheme-snapshots-round-trip
  (let [langs {"l0" l/L0 "l2" l/L2 "l3" l/L3}
        files (sort (filter #(langs (subs (System.IO.Path/GetExtension ^String %) 1))
                            (System.IO.Directory/GetFiles snapshot-dir)))]
    (is (< 60 (count files)) "快照文件都在")
    (doseq [f files]
      (let [lang (langs (subs (System.IO.Path/GetExtension ^String f) 1))
            forms (read-scheme-all (slurp f))]
        (testing (System.IO.Path/GetFileName ^String f)
          (is (= 1 (count forms)))
          (let [x (first forms)
                n (np/parse lang x)]
            (is (= x (np/unparse n)))))))))

(deftest l1-letrec-binds-lambdas
  (is (l/L1:Expr:letrec? (np/parse l/L1 '(letrec ((f (lambda false (clause () false (quote 1))))) (call f)))))
  (is (thrown? Exception (np/parse l/L1 '(letrec ((f (quote 1))) f)))))

(deftest l3-lambda-free-variables
  (let [n (np/parse l/L3 '(lambda "f" (y z) (clause (x) false (primcall + x y z))))]
    (is (l/L3:LambdaExpr:lambda? n))
    (is (= '[y z] (vec (.-x* ^cleat.compiler.languages.L3-LambdaExpr-lambda n))))))

(deftest clj-fn-name-is-a-name
  (let [nm (l/->CljFnName "user$f" nil nil false)
        n (l/make-L0:LambdaExpr:lambda nm [(l/make-L0:Clause:clause '[x] nil 'x)])]
    (is (identical? nm (.-nm ^cleat.compiler.languages.L0-LambdaExpr-lambda n)))))
