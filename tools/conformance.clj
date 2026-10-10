;; 语料运行器（cleat-053 基线；designs/09，tests/conformance/SPEC.md）：在 cleat 宿主上（Clojure 镜像已加载）对
;; tests/conformance/corpus.edn 的行求值并按 = 比较。
;;
;;   cleat tools/conformance.clj [corpus.edn] [--portability common|jvm|all] [--timeout ms] [--report out.edn] [--limit n]
;;
;; 每行在新的命名空间里（refer clojure.core）求值 :actual，再在同一命名空间里求值 :expected 并比较；:expected 为 :throws
;; 时 :actual 必须抛异常。每行在一个 256 MB 栈的线程上运行（展开器递归深），超时的行记为 :timeout（线程放弃，后台线程不阻止退出）。
;; 输出：通过数、失败 / 错误 / 超时数、按套件（suite 的 " / " 前缀）与按异常消息分组的前若干组；--report 写出每行结果。
(ns cleat.conformance
  (:require [clojure.edn :as edn]
            [clojure.string :as str]))

(defn- arg [args flag default]
  (let [m (apply hash-map (drop-while #(not (str/starts-with? % "--")) args))]
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

(defn- eval-in [ns src]
  (binding [*ns* ns]
    (eval (read-string {:read-cond :allow :features #{:cljr :cleat}} (str "(do " src "\n)")))))

(defn- run-row [row ms]
  (let [ns (create-ns (symbol (str "conformance.row" (swap! counter inc))))
        _ (binding [*ns* ns] (refer 'clojure.core))
        expected (:expected row)
        throws? (= expected :throws)
        r (run-thread
            (fn []
              (let [a (try {:val (eval-in ns (:actual row))} (catch Exception e {:ex e}))]
                (cond
                  throws? (if (:ex a) :pass {:fail :no-throw :got (pr-str (:val a))})
                  (:ex a) {:error (:ex a)}
                  :else (let [e (eval-in ns expected)]
                          (if (= (:val a) e) :pass {:fail :not-equal :got (pr-str (:val a))})))))
            ms)]
    (remove-ns (ns-name ns))
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
      (println "\nreport:" out))))

(apply -main *command-line-args*)
