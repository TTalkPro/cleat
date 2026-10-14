;; 编译器的逐形式差分与 L0 普查（cleat-085，designs/19 D4 / D5）。在宿主上运行：
;;
;;   cleat -I src/clojure tools/compiler-diff.clj diff <目录A>/<一侧> <目录B>/<一侧> [--stage 阶段]… [--max n]
;;       比较两份转储（CLEAT_DUMP_IR 写出的 <单元>.ir，见 Cleat.Scheme.Clojure.IrDump）：逐单元、逐形式、逐阶段，
;;       临时名规范化之后比较文本，报告每个单元的第一个差异（附源形式）与统计。只在一侧出现的记录单独计数。
;;       例：Scheme 自己两次运行 diff /tmp/a/scheme /tmp/b/scheme；影子模式 diff /tmp/d/scheme /tmp/d/clojure
;;   cleat -I src/clojure tools/compiler-diff.clj census <目录>/scheme [--unit 单元]…
;;       普查：L0（p0 与 l0 阶段）每种产生式的出现次数（读回成 cleat.compiler.languages 的记录计数），各 pass 改变了多少个形式
;;
;; 规范化（19 D5 的 G1）：两个编译器的临时名来源不同（psyntax 的 g$名字$N$后缀、gensym 的 名字__N 与 名字__N__auto__、
;; lower-loops 等的 名字%N），按在一条记录里首次出现的顺序重新编号为 名字#k。
(ns compiler-diff
  (:require [clojure.string :as str]
            [cleat.nanopass :as np]
            [cleat.compiler.languages :as l]))

;; —— 读转储 ——

(defn read-dump
  "<单元>.ir → [{:ix :stage :text} …]（文件顺序）。"
  [file]
  (let [lines (str/split-lines (slurp file))]
    (loop [ls lines cur nil out []]
      (if-let [[ln & more] (seq ls)]
        (if-let [[_ ix stage] (re-matches #";; (-?\d+) (\S+)" ln)]
          (recur more {:ix (parse-long ix) :stage stage :lines []} (if cur (conj out cur) out))
          (recur more (update cur :lines conj ln) out))
        (mapv (fn [r] (-> r (assoc :text (str/join "\n" (:lines r))) (dissoc :lines)))
              (if cur (conj out cur) out))))))

(defn dump-units
  "目录下的单元：单元名 → 文件。"
  [dir]
  (into (sorted-map)
        (for [f (System.IO.Directory/GetFiles dir "*.ir")]
          [(System.IO.Path/GetFileNameWithoutExtension ^String f) f])))

;; —— 规范化 ——

(def temp-pattern
  #"g\$[^\s()\[\]\"]+|[A-Za-z0-9_*+!?<>=$&.\-]*__\d+(?:__auto__)?|[A-Za-z0-9_*+!?<>=$&.\-]+%\d+")

(defn- temp-base
  "psyntax：g$名字$N$作用域 与 g$genN$作用域，内联副本再带 $inl；gensym：名字__N；fresh：名字%N。"
  [t]
  (cond
    (str/starts-with? t "g$") (let [parts (str/split t #"\$")
                                    nm (str/replace (nth parts 1 "") #"^gen\d+$" "gen")]
                                (str "g$" nm (when (= "inl" (last parts)) "$inl")))
    (str/includes? t "%") (str (first (str/split t #"%")) "%")
    :else (str/replace t #"__\d+" "__")))

(defn normalize
  "文本里的临时名按首次出现的顺序改名为 基名#k。"
  [text]
  (let [m (volatile! {})
        ;; 不能写成裸符号的标识符打印为 #cleat/id "…"（psyntax 的作用域后缀是随机的，可能含 + / =）：临时名先还原成裸名
        text (str/replace text #"#cleat/id \"(g\$[^\"]*)\"" "$1")]
    (str/replace text temp-pattern
                 (fn [t]
                   (or (@m t)
                       (let [r (str (temp-base t) "#" (inc (count @m)))]
                         (vswap! m assoc t r)
                         r))))))

;; —— diff ——

(def stage-order ["src" "p0" "l0" "sl" "cf" "c1" "in" "l1" "l2" "l3" "lcil"])

(defn- first-difference [a b]
  (let [n (min (count a) (count b))
        i (loop [i 0] (if (and (< i n) (= (nth a i) (nth b i))) (recur (inc i)) i))
        ctx (fn [s] (subs s (max 0 (- i 80)) (min (count s) (+ i 80))))]
    {:at i :a (ctx a) :b (ctx b)}))

(defn diff-dirs [a b {:keys [stages max-report] :or {max-report 3}}]
  (let [ua (dump-units a) ub (dump-units b)
        stats (atom {:compared 0 :same 0 :different 0 :only-a 0 :only-b 0 :units-only-a [] :units-only-b []})]
    (doseq [u (keys ua) :when (not (ub u))] (swap! stats update :units-only-a conj u))
    (doseq [u (keys ub) :when (not (ua u))] (swap! stats update :units-only-b conj u))
    (doseq [u (keys ua) :when (ub u)]
      (let [key-of (juxt :ix :stage)
            ra (read-dump (ua u)) rb (read-dump (ub u))
            ;; 源形式：影子模式写在 clojure 一侧；比较两份 scheme 转储时去旁边的 clojure 目录找
            sibling (fn [d] (let [f (System.IO.Path/Combine (System.IO.Path/GetDirectoryName (System.IO.Path/GetFullPath ^String d)) "clojure" (str u ".ir"))]
                              (when (System.IO.File/Exists f) (read-dump f))))
            src (into {} (for [r (concat ra rb (sibling a) (sibling b)) :when (= "src" (:stage r))] [(:ix r) (:text r)]))
            keep? (fn [r] (and (not= "src" (:stage r)) (or (empty? stages) (contains? stages (:stage r)))))
            ma (into {} (map (juxt key-of identity) (filter keep? ra)))
            mb (into {} (map (juxt key-of identity) (filter keep? rb)))
            reported (atom 0)
            stage-rank (fn [st] (let [i (.IndexOf ^System.Collections.IList stage-order st)] (if (neg? i) 100 i)))]
        ;; 逐形式：按流水线顺序找第一个不同的阶段（报告它），其余阶段只计数
        (doseq [[ix ks] (sort-by key (group-by first (keys ma)))]
          (let [first-diff (atom nil)]
            (doseq [k (sort-by (comp stage-rank second) ks)]
              (if-let [y (mb k)]
                (let [x (ma k)
                      ta (normalize (:text x)) tb (normalize (:text y))]
                  (swap! stats update :compared inc)
                  (if (= ta tb)
                    (swap! stats update :same inc)
                    (do (swap! stats update :different inc)
                        (when-not @first-diff (reset! first-diff [k ta tb])))))
                (swap! stats update :only-a inc)))
            (when-let [[k ta tb] @first-diff]
              (when (< @reported max-report)
                (swap! reported inc)
                (let [{:keys [at] :as d} (first-difference ta tb)]
                  (println (str "DIFF " u " form " ix " stage " (second k) " at char " at))
                  (when-let [s (src ix)] (println "  source:" s))
                  (println "  A:" (:a d))
                  (println "  B:" (:b d)))))))
        (doseq [k (keys mb) :when (not (ma k))] (swap! stats update :only-b inc))))
    (let [s @stats]
      (println (str "compared " (:compared s) " records: " (:same s) " same, " (:different s) " different; "
                    (:only-a s) " only in A, " (:only-b s) " only in B"))
      (when (seq (:units-only-a s)) (println "units only in A:" (str/join " " (:units-only-a s))))
      (when (seq (:units-only-b s)) (println "units only in B:" (str/join " " (:units-only-b s))))
      s)))

;; —— 普查 ——

(defn- census-reader [tag form]
  (case (str tag)
    "cleat/record" (if (= "clj-fn-name" (first form)) (apply l/->CljFnName (rest form)) (tagged-literal tag form))
    "cleat/ssym" (symbol form)
    "cleat/id" (symbol form)
    (tagged-literal tag form)))

(defn read-ir [text]
  (binding [*read-eval* false *default-data-reader-fn* census-reader]
    (read-string text)))

(defn- count-nodes! [counts x]
  (cond
    (instance? cleat.nanopass.records.INode x)
    (let [^cleat.nanopass.records.INode n x
          lang (cleat.nanopass.records/node-language n)
          prod ((:by-tag lang) (.nanoTag n))]
      (vswap! counts update (:keyword prod) (fnil inc 0))
      (doseq [v (.nanoFields n)] (count-nodes! counts v)))
    (sequential? x) (doseq [v x] (count-nodes! counts v))
    :else nil))

(def pass-stages
  [["lower-loops" "p0" "l0"] ["simplify-letrec" "l0" "sl"] ["check-forward" "sl" "cf"] ["cp0-lite" "cf" "c1"]
   ["cp0-inline+simplify-lets" "c1" "in"] ["simplify-if" "in" "l1"] ["convert-assignments" "l1" "l2"] ["uncover-free" "l2" "l3"]])

(defn census [dir {:keys [units]}]
  (let [us (cond-> (dump-units dir) (seq units) (select-keys units))
        p0 (volatile! {}) l0 (volatile! {}) forms (volatile! 0) changed (volatile! {}) failed (volatile! [])]
    (doseq [[u f] us]
      (let [rs (group-by :ix (remove #(neg? (:ix %)) (read-dump f)))]
        (doseq [[ix recs] rs]
          (let [by (into {} (map (juxt :stage :text) recs))]
            (when (by "p0")
              (vswap! forms inc)
              (try
                (count-nodes! p0 (np/parse l/L0 (read-ir (by "p0"))))
                (count-nodes! l0 (np/parse l/L0 (read-ir (by "l0"))))
                (catch Exception e (vswap! failed conj [u ix (.Message e)])))
              (doseq [[pass from to] pass-stages]
                (when (and (by from) (by to) (not= (by from) (by to)))
                  (vswap! changed update pass (fnil inc 0)))))))))
    (println (str "units: " (str/join " " (keys us)) "; forms: " @forms))
    (println "\nL0 productions (after parse-core p0 / after lower-loops l0):")
    (doseq [nt (:nonterminals (:desc l/L0)) p (:prods nt) :when (= :pair (:kind p))]
      (let [k (:keyword p)]
        (println (format "  %-20s %8d %8d" (str k) (get @p0 k 0) (get @l0 k 0)))))
    (println "\nforms changed by each pass:")
    (doseq [[pass] pass-stages] (println (format "  %-26s %8d" pass (get @changed pass 0))))
    (when (seq @failed)
      (println "\nnot parsed (" (count @failed) "):")
      (doseq [x (take 5 @failed)] (println " " x)))
    {:p0 @p0 :l0 @l0 :changed @changed :forms @forms}))

;; —— 命令行 ——

(defn- opts [args]
  (loop [as args o {:pos [] :stages #{} :units []}]
    (if-let [[a & more] (seq as)]
      (case a
        "--stage" (recur (next more) (update o :stages conj (first more)))
        "--unit" (recur (next more) (update o :units conj (first more)))
        "--max" (recur (next more) (assoc o :max-report (parse-long (first more))))
        (recur more (update o :pos conj a)))
      o)))

(let [[cmd & args] *command-line-args*
      o (opts args)]
  (case cmd
    "diff" (let [[a b] (:pos o)
                 s (diff-dirs a b o)]
             (System.Environment/Exit (if (zero? (:different s)) 0 1)))
    "census" (census (first (:pos o)) o)
    (do (println "usage: compiler-diff.clj diff <dirA>/<side> <dirB>/<side> [--stage s]… [--max n] | census <dir>/scheme [--unit u]…")
        (System.Environment/Exit 2))))
