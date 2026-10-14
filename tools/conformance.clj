;; 语料运行器（cleat-053 基线；designs/09，tests/conformance/SPEC.md）：在 cleat 宿主上（Clojure 镜像已加载）对
;; tests/conformance/corpus.edn 的行求值并按 = 比较。
;;
;;   cleat tools/conformance.clj [corpus.edn] [--portability common|jvm|all] [--timeout ms] [--report out.edn] [--limit n]
;;                               [--results out.edn] [--gate [--divergences tests/conformance/cleat-divergences.edn]]
;;
;; 每行在新的 user 命名空间里（refer clojure.core）先读 :expected、再求值 :actual，再在同一命名空间里求值 :expected 并比较；:expected 为 :throws
;; 时 :actual 必须抛异常。每行在一个 256 MB 栈的线程上运行（展开器递归深），超时的行记为 :timeout（线程放弃，后台线程不阻止退出）。
;; 输出：通过数、失败 / 错误 / 超时数、按套件（suite 的 " / " 前缀）与按异常消息分组的前若干组；--report 写出失败行的详情，
;; --results 写出每行的结果（tests/conformance/cleat-results.edn），--gate 按偏离表门禁（新失败或过期条目 → 退出码 1）。
(ns cleat.conformance
  (:require [clojure.edn :as edn]
            [clojure.string :as str]))

(defn- arg [args flag default]
  (let [m (apply hash-map (drop-while #(not (str/starts-with? % "--")) (remove #{"--gate"} args)))]
    (get m flag default)))

(defn- run-thread
  "f 在大栈线程上运行，最多等 ms 毫秒：{:ok v} / {:ex e} / :timeout"
  [f ms]
  (let [result (atom :timeout)
        g (bound-fn [] (reset! result (try {:ok (f)} (catch Exception e {:ex e}))))
        t (System.Threading.Thread. (gen-delegate System.Threading.ThreadStart [] (g)) (int (* 256 1024 1024)))]
    (.set_IsBackground t true)
    (.Start t)
    (.Join t (int ms))
    @result))

(def ^:private counter (atom 0))

(defn- read-in [ns src]
  (binding [*ns* ns]
    (read-string {:read-cond :allow :features #{:cljr :cleat}} (str "(do " src "\n)"))))

(defn- eval-in [ns src]
  (binding [*ns* ns] (eval (if (string? src) (read-in ns src) src))))

(defn- fresh-user-ns
  "语料按 JVM 取证的方式运行：每行在新的 user 命名空间里求值（SPEC.md：fresh user namespace）"
  []
  (remove-ns 'user)
  (let [ns (create-ns 'user)]
    (binding [*ns* ns] (refer 'clojure.core))
    ns))

(defn- run-row [row ms]
  (let [ns (fresh-user-ns)
        expected (:expected row)
        throws? (= expected :throws)
        r (run-thread
            (fn []
              ;; JVM 的取证进程先读入整份语料：:expected 里的关键字（find-keyword）在求值 :actual 之前就已 intern。
              ;; 先读 :expected（读不了——例如要用 :actual 建立的别名——就留到求值之后再读）
              (let [expected (if throws? expected (try (read-in ns expected) (catch Exception _ expected)))
                    a (try {:val (eval-in ns (:actual row))} (catch Exception e {:ex e}))]
                (cond
                  throws? (if (:ex a) :pass {:fail :no-throw :got (pr-str (:val a))})
                  (:ex a) {:error (:ex a)}
                  :else (let [e (eval-in ns expected)]
                          (if (= (:val a) e) :pass {:fail :not-equal :got (pr-str (:val a))})))))
            ms)]
    (cond
      (= r :timeout) {:status :timeout}
      (:ex r) {:status :error :ex (:ex r)}
      (= (:ok r) :pass) {:status :pass}
      (:error (:ok r)) {:status :error :ex (:error (:ok r))}
      :else (merge {:status :fail} (:ok r)))))

(defn- suite-group [row] (first (str/split (:suite row) #" / ")))

(defn- ex-key [^Exception e]
  (let [m (or (if (instance? Cleat.Core.Expand.ExpanderException e) (.Reason ^Cleat.Core.Expand.ExpanderException e) (.Message e)) "")
        m (str/replace m #"\d+" "N")]
    (str (.Name (class e)) ": " (subs m 0 (min 100 (count m))))))

(defn- top [n m] (take n (sort-by (comp - val) m)))

(defn -main [& args]
  (let [path (or (first (remove #(str/starts-with? % "--") (take 1 args))) "tests/conformance/corpus.edn")
        portability (keyword (arg args "--portability" "common"))
        ms (Int64/Parse (arg args "--timeout" "10000"))
        limit (when-let [l (arg args "--limit" nil)] (Int64/Parse l))
        rows (edn/read-string (slurp path))
        rows (if (= portability :all) rows (filter #(= (:portability %) portability) rows))
        rows (vec (if limit (take limit rows) rows))
        sw (System.Diagnostics.Stopwatch/StartNew)
        results (vec (map-indexed
                       (fn [i row]
                         (when (zero? (mod (inc i) 250))
                           (binding [*out* *err*] (println "conformance:" (inc i) "/" (count rows) (.ElapsedMilliseconds sw) "ms")))
                         (assoc (run-row row ms) :row row))
                       rows))
        by-status (frequencies (map :status results))
        bad (remove #(= :pass (:status %)) results)]
    (println "rows:" (count rows) (str "(" (name portability) ")") "in" (quot (.ElapsedMilliseconds sw) 1000) "s")
    (println "pass:" (get by-status :pass 0) " fail:" (get by-status :fail 0)
             " error:" (get by-status :error 0) " timeout:" (get by-status :timeout 0))
    (println "\nfailing rows by suite group:")
    (doseq [[g n] (top 25 (frequencies (map (comp suite-group :row) bad)))] (println (format "  %5d  %s" n g)))
    (println "\nerrors by exception:")
    (doseq [[k n] (top 25 (frequencies (map (comp ex-key :ex) (filter :ex bad))))] (println (format "  %5d  %s" n k)))
    (when-let [out (arg args "--report" nil)]
      (spit out (with-out-str
                  (doseq [r bad]
                    (prn {:suite (:suite (:row r)) :label (:label (:row r)) :status (:status r)
                          :actual (:actual (:row r)) :expected (:expected (:row r))
                          :got (:got r) :ex (when (:ex r) (ex-key (:ex r)))}))))
      (println "\nreport:" out))
    ;; 每行结果（cleat-032）：[[suite label] 状态 原因]，状态 :pass / :fail（值不等或该抛未抛）/ :throw / :timeout
    (when-let [out (arg args "--results" nil)]
      (spit out (with-out-str
                  (println ";; 生成：cleat tools/conformance.clj --results（cleat-032）。语料每行在 cleat 上的结果：")
                  (println ";; [[suite label] :pass] | [[suite label] :fail \"得到的值\"] | [[suite label] :throw \"异常\"] | [[suite label] :timeout]")
                  (println (str "{:portability " portability " :rows " (count rows) " :pass " (get by-status :pass 0)
                                " :fail " (get by-status :fail 0) " :throw " (get by-status :error 0) " :timeout " (get by-status :timeout 0)))
                  (println " :results")
                  (println " [")
                  (doseq [r results]
                    (let [id [(:suite (:row r)) (:label (:row r))]
                          g (fn [s] (let [s (str s)] (subs s 0 (min 120 (count s)))))]
                      (prn (case (:status r)
                             :pass [id :pass]
                             :fail [id :fail (g (if (= (:fail r) :no-throw) (str "no throw: " (:got r)) (:got r)))]
                             :error [id :throw (g (ex-key (:ex r)))]
                             :timeout [id :timeout]))))
                  (println " ]}")))
      (println "results:" out))
    ;; 门禁（cleat-032，与 jolt 的 certify 相同）：每个失败行都要在偏离表里；表里的行都要仍然失败、仍在语料中
    (when (some #{"--gate"} args)
      (let [dpath (arg args "--divergences" "tests/conformance/cleat-divergences.edn")
            ;; :compiler 只适用于一个编译器的条目（cleat-090：Scheme 编译器的偏离在 Clojure 编译器下已不存在）；缺省两个都适用
            compiler (let [c (System.Environment/GetEnvironmentVariable "CLEAT_COMPILER")] (if (= c "clojure") :clojure :scheme))
            entries (filter #(or (nil? (:compiler %)) (= compiler (:compiler %))) (:entries (edn/read-string (slurp dpath))))
            listed (into {} (map (fn [e] [[(:suite e) (:label e)] e]) entries))
            ids (set (map (fn [r] [(:suite r) (:label r)]) rows))
            failing (set (map (fn [r] [(:suite (:row r)) (:label (:row r))]) bad))
            fresh (sort (remove listed failing))
            ;; :flaky 的条目可过可不过（结果取决于对象哈希等进程内的偶然顺序），不算过期
            stale (sort (remove #(or (failing %) (and (ids %) (:flaky (listed %)))) (keys listed)))]
        (println "\ngate:" (count listed) "classified divergences," (count failing) "failing rows")
        (doseq [[s l] fresh] (println "  NEW failure (not in" dpath "):" (pr-str s) (pr-str l)))
        (doseq [[s l] stale] (println (if (ids [s l]) "  STALE entry (row passes now):" "  STALE entry (no such row):") (pr-str s) (pr-str l)))
        (if (and (empty? fresh) (empty? stale))
          (println "gate: OK")
          (do (println "gate: FAILED") (System.Environment/Exit 1)))))))

(apply -main *command-line-args*)
