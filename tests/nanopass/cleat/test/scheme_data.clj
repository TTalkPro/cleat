;; 测试辅助（cleat-084）：读 Scheme 编译器的快照（tests/scheme/compiler/snapshots，Scheme 的 pretty-print 输出）。
(ns cleat.test.scheme-data)

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

(defn read-scheme-all [s]
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

(defn snapshot-names
  "有给定阶段文件的快照名（排序）。"
  [stage]
  (sort (keep #(let [f (System.IO.Path/GetFileName ^String %)]
                 (when (.EndsWith f (str "." stage)) (subs f 0 (- (count f) (count stage) 1))))
              (System.IO.Directory/GetFiles snapshot-dir))))

(defn snapshot
  "快照 name 的 stage 阶段（读成数据）；没有时 nil。"
  [name stage]
  (let [f (System.IO.Path/Combine snapshot-dir (str name "." stage))]
    (when (System.IO.File/Exists f)
      (let [forms (read-scheme-all (slurp f))]
        (assert (= 1 (count forms)) f)
        (first forms)))))

(defn canonical-temps
  "临时变量名（名字%N，lower-loops 等的 fresh）按首次出现的顺序重新编号：两个编译器的实参求值顺序不同，编号可能不同
  （IronScheme 构造记录时不是从左到右），designs/19 D5 的 G1 在规范化之后比较。"
  [form]
  (let [m (atom {})
        rename (fn [s]
                 (if-let [[_ base] (and (symbol? s) (re-matches #"(.*)%\d+" (name s)))]
                   (or (@m s) (let [r (symbol (str base "%" (inc (count @m))))] (swap! m assoc s r) r))
                   s))
        walk (fn walk [x]
               (cond
                 (seq? x) (apply list (map walk x))
                 (vector? x) (mapv walk x)
                 :else (rename x)))]
    (walk form)))
