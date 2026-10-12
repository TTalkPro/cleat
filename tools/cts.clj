;; clojure-test-suite / test_clojure 基线运行器（cleat-032；designs/09 §3）：在 cleat 宿主上逐命名空间加载测试文件，
;; 用 clojure.test 运行，记录每个命名空间的 {:test :pass :fail :error}（加载失败记 {:load-error "…"}，超时记 {:timeout true}）。
;;
;;   cleat tools/cts.clj cts          [--write] [--only ns-regex] [--timeout ms]   ; tests/clojure-test-suite → tests/cts-baseline.edn
;;   cleat tools/cts.clj test-clojure [--write] [--only ns-regex] [--timeout ms]   ; tests/test_clojure → tests/test-clojure-baseline.edn
;;
;; 没有 --write 时与基线比较：任何命名空间的计数与基线不同（更好或更差）都算失败，退出码 1——更好时也要有意地更新基线（与 jolt 相同）。
(ns cleat.cts
  (:require [clojure.edn :as edn]
            [clojure.string :as str]
            [clojure.test :as t]))

(defn- arg [args flag default]
  (let [m (apply hash-map (drop-while #(not (str/starts-with? % "--")) (remove #{"--write"} args)))]
    (get m flag default)))

(def ^:private root (.FullName (.Parent (System.IO.DirectoryInfo. (str (System.IO.Path/GetDirectoryName *file*))))))
(defn- path [& parts] (str/join (str System.IO.Path/DirectorySeparatorChar) (cons root parts)))

(def ^:private tmp-root (System.IO.Path/Combine (System.IO.Path/GetTempPath) "cleat-test-clojure"))

;; —— 两个套件 ——

(defn- files-under [dir]
  (->> (System.IO.Directory/GetFiles dir "*.*" System.IO.SearchOption/AllDirectories)
       (filter #(re-find #"\.(clj|cljc|cljr)$" %))
       (map #(str/replace % "\\" "/"))
       sort))

(defn- file->ns [rel]
  (-> rel (str/replace #"\.(clj|cljc|cljr)$" "") (str/replace "/" ".") (str/replace "_" "-") symbol))

;; clojure-test-suite：test/ 是加载根；portability、number-range 是辅助命名空间，不是测试
(defn- cts-suite []
  (let [dir (path "tests" "clojure-test-suite" "test")]
    {:baseline (path "tests" "cts-baseline.edn")
     :load-path [dir]
     :namespaces (->> (files-under dir)
                      (map #(subs % (inc (count (str/replace dir "\\" "/")))))
                      (map file->ns)
                      (remove #{'clojure.core-test.portability 'clojure.core-test.number-range}))}))

;; test_clojure（ClojureCLR 的 Clojure.Tests/clojure/test_clojure）：文件平放在 tests/test_clojure/，命名空间是
;; clojure.test-clojure.*，于是在临时目录里建 clojure/test_clojure → tests/test_clojure 的符号链接作加载根；
;; clojure.test-helper 是 tests/test_clojure/test_helper.clj。不跑的文件（外部库、JVM 专用、cleat-002 结论）见 skip。
(def ^:private test-clojure-skip
  (let [gen "needs clojure.test.generative / clojure.data.generators (external libs, cleat-002)"
        check "needs clojure.test.check (external lib)"
        support "needs ClojureCLR's compiled test-support assemblies (Clojure.Tests.Support.dll / Clojure.Tests.dll)"
        helper "helper namespace loaded by another test file"]
    {'clojure.test-clojure.generators gen
     'clojure.test-clojure.data-structures gen
     'clojure.test-clojure.edn gen
     'clojure.test-clojure.numbers gen
     'clojure.test-clojure.reader gen
     'clojure.test-clojure.reducers gen
     'clojure.test-clojure.clr.added gen
     'clojure.test-clojure.sequences check
     'clojure.test-clojure.data-structures-interop check
     'clojure.test-clojure.parse check
     'clojure.test-clojure.transducers check
     'clojure.test-clojure.attributes support
     'clojure.test-clojure.clr.interop support
     'clojure.test-clojure.try-catch support
     'clojure.test-clojure.clr.process "clojure.clr.process is not in cleat's src/clojure"
     'clojure.test-clojure.repl.deps "clojure.repl.deps needs clojure.clr.basis (tools.deps)"
     'clojure.test-clojure.clojure-xml "clojure.xml is not in cleat's src/clojure"
     'clojure.test-clojure.run-single-test "driver script, not a test namespace"
     'clojure.test-clojure.test-helper "loaded as clojure.test-helper"
     'clojure.test-clojure.ns-libs-load-later helper
     'clojure.test-clojure.compilation.examples helper
     'clojure.test-clojure.compilation.line-number-examples helper
     'clojure.test-clojure.compilation.load-ns helper
     'clojure.test-clojure.genclass.examples helper
     'clojure.test-clojure.pprint.test-helper helper
     'clojure.test-clojure.pprint.test-cl-format "loaded by clojure.test-clojure.pprint"
     'clojure.test-clojure.pprint.test-pretty "loaded by clojure.test-clojure.pprint"
     'clojure.test-clojure.protocols.examples helper
     'clojure.test-clojure.protocols.more-examples helper
     'clojure.test-clojure.proxy.examples helper
     'clojure.test-clojure.repl.example helper}))

;; ClojureCLR 先 AOT 编译这些辅助命名空间（gen-class 的类型）再跑测试；测试文件开头 (assembly-load "类名")
;; 在 cleat 里经 Loader 的程序集名桥接找到生成该类型的单元（cleat-043），所以先加载它们
(def ^:private test-clojure-preload
  {'clojure.test-clojure.genclass ['clojure.test-clojure.genclass.examples]})

(defn- test-clojure-suite []
  (let [dir (path "tests" "test_clojure")
        tmp tmp-root
        cdir (System.IO.Path/Combine tmp "clojure")]
    (when (System.IO.Directory/Exists tmp) (System.IO.Directory/Delete tmp true))
    (System.IO.Directory/CreateDirectory cdir)
    (System.IO.Directory/CreateSymbolicLink (System.IO.Path/Combine cdir "test_clojure") dir)
    (System.IO.File/CreateSymbolicLink (System.IO.Path/Combine cdir "test_helper.clj") (System.IO.Path/Combine dir "test_helper.clj"))
    {:baseline (path "tests" "test-clojure-baseline.edn")
     :load-path [tmp]
     :skip test-clojure-skip
     :preload test-clojure-preload
     :namespaces (->> (files-under dir)
                      (map #(subs % (inc (count (str/replace dir "\\" "/")))))
                      (map #(symbol (str "clojure.test-clojure." (file->ns %)))))}))

;; —— 运行 ——

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

(defn- short-msg
  "异常的类名与消息首行；去掉机器相关的部分（加载路径列表、仓库与临时目录的绝对路径），基线因此可移植"
  [^Exception e]
  (let [m (or (.Message e) "")
        m (first (str/split-lines m))
        m (str/replace m #" on load path \[.*" "")
        m (-> m (str/replace (str root "/") "") (str/replace (str tmp-root "/") ""))]
    (str (.Name (class e)) ": " (subs m 0 (min 160 (count m))))))

(defn- run-one [ns-sym ms preload]
  (let [out (System.IO.StringWriter.)
        r (run-thread
            (fn []
              (binding [*out* out *err* out t/*test-out* out]
                (doseq [p preload] (require p))
                (require ns-sym)
                (select-keys (t/test-ns ns-sym) [:test :pass :fail :error])))
            ms)]
    (cond
      (= r :timeout) {:timeout true}
      (:ex r) {:load-error (short-msg (:ex r))}
      :else (merge {:test 0 :pass 0 :fail 0 :error 0} (:ok r)))))

(defn- totals [m]
  (reduce (fn [acc [_ v]] (merge-with + acc (select-keys v [:test :pass :fail :error]))) {:test 0 :pass 0 :fail 0 :error 0} m))

(defn -main [& args]
  (let [which (first args)
        suite (case which "cts" (cts-suite) "test-clojure" (test-clojure-suite)
                (do (println "usage: cleat tools/cts.clj cts|test-clojure [--write] [--only re] [--timeout ms]") (System.Environment/Exit 2)))
        ms (Int64/Parse (arg args "--timeout" "60000"))
        only (when-let [o (arg args "--only" nil)] (re-pattern o))
        skip (:skip suite {})
        nss (cond->> (remove skip (:namespaces suite)) only (filter #(re-find only (str %))))
        _ (doseq [d (:load-path suite)] (Cleat.Core.Runtime.Loader/AddPath d))
        sw (System.Diagnostics.Stopwatch/StartNew)
        results (into (sorted-map)
                      (for [n nss]
                        (let [r (run-one n ms (get (:preload suite) n))]
                          (binding [*out* *err*] (println (format "%-55s %s" n (pr-str r))))
                          [n r])))
        tot (totals results)
        broken (count (filter (fn [[_ v]] (or (:load-error v) (:timeout v))) results))]
    (println (format "%s: %d namespaces (%d skipped, %d not loaded / timed out) in %d s; tests %d, assertions pass %d, fail %d, error %d"
                     which (count results) (count skip) broken (quot (.ElapsedMilliseconds sw) 1000)
                     (:test tot) (:pass tot) (:fail tot) (:error tot)))
    (if (or (some #{"--write"} args) only)
      (when-not only
        (spit (:baseline suite)
              (with-out-str
                (println ";; 生成：cleat tools/cts.clj" which "--write（cleat-032）。每个命名空间的 clojure.test 计数；门禁要求与此完全相同。")
                (println ";; :load-error = 加载或运行时抛出（消息截断）；:timeout = 超时。")
                (println (str "{:date " (pr-str (.ToString DateTime/Now "yyyy-MM-dd")) " :totals " (pr-str tot) " :not-run " broken))
                (println (str " :skipped " (pr-str (into (sorted-map) skip))))
                (println " :namespaces")
                (println " {")
                (doseq [[k v] results] (println (str "  " (pr-str k) " " (pr-str v))))
                (println " }}")))
        (println "baseline written:" (:baseline suite)))
      (let [base (:namespaces (edn/read-string (slurp (:baseline suite))))
            diffs (for [k (sort (set (concat (keys base) (keys results))))
                        :let [b (get base k) r (get results k)]
                        :when (not= b r)]
                    [k b r])]
        (if (empty? diffs)
          (println "baseline: OK")
          (do (println "baseline: DIFFERS (update deliberately with --write)")
              (doseq [[k b r] diffs] (println (format "  %s\n    baseline %s\n    now      %s" k (pr-str b) (pr-str r))))
              (System.Environment/Exit 1)))))
    ;; 测试里起的 agent / future / 服务线程不阻止退出
    (shutdown-agents)
    (System.Environment/Exit 0)))

(apply -main *command-line-args*)
