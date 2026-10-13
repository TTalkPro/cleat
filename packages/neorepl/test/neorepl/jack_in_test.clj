(ns neorepl.jack-in-test
  (:require [clojure.string :as str]
            [clojure.test :refer [deftest is testing]]
            [neorepl.discovery :as discovery]
            [neorepl.jack-in :as jack-in]
            [neorepl.nrepl :as nrepl]
            [neorepl.test-server :as test-server :refer [call-with-temp-dir spit-file windows? with-client]])
  (:import (System.Diagnostics Process)
           (System.IO File Path)))

;; cleat: jack-in knows cleat projects alone (a cleat.edn; the JVM version's
;; Clojure CLI, Leiningen, babashka and shadow-cljs tests don't apply), and the
;; server it starts is cleat running nrepl.cmdline.

(defn- sh [& args]
  (test-server/run-process (vec args)))

(defn- children-of
  "The processes pid started, as pgrep lists them."
  [pid]
  (let [{:keys [out]} (sh "pgrep" "-P" (str pid))]
    (keep #(when-not (str/blank? %) (Int32/Parse (str/trim %))) (str/split-lines (or out "")))))

(defn- alive? [pid]
  (try (not (.HasExited (Process/GetProcessById (int pid)))) (catch Exception _ false)))

(defn- survives-sigint? [^Process process]
  (sh "kill" "-INT" (str (.Id process)))
  (System.Threading.Thread/Sleep 500)
  (not (.HasExited process)))

(deftest find-project-test
  (call-with-temp-dir
   (fn [dir]
     (spit-file dir "cleat.edn" "{}")
     (spit-file dir "src/demo/core.clj" "(ns demo.core)")
     (spit-file dir "sub/cleat.edn" "{:paths [\"lib\"]}")
     (let [path #(discovery/canonical-path (test-server/file dir %))]
       (testing "the nearest project"
         (is (= {:dir (path ".") :tool "cleat"} (jack-in/find-project (test-server/file dir "src/demo") nil)))
         (is (= {:dir (path "sub") :tool "cleat"} (jack-in/find-project (test-server/file dir "sub") nil))))
       (testing "or the one asked for"
         (is (= {:dir (path "sub") :tool "cleat"} (jack-in/find-project (test-server/file dir "sub") "cleat"))))
       (testing "or none, saying what's missing"
         (call-with-temp-dir
          (fn [empty-dir]
            ;; Unless a parent of the temp dir happens to have one.
            (when-not (jack-in/find-project empty-dir nil)
              (is (nil? (jack-in/find-project empty-dir "cleat"))))
            (is (= (str "No cleat.edn in " (discovery/canonical-path empty-dir) " or its parents")
                   (ex-message (jack-in/no-project empty-dir "cleat"))))))
         (is (= "cleat.edn" (jack-in/project-files nil))))))))

(deftest command-test
  (call-with-temp-dir
   (fn [dir]
     (spit-file dir "cleat.edn" "{:paths [\"src\" \"dev\"] :middleware [my.app/wrap-x]}")
     (let [command (jack-in/command "cleat" {:port 7888} dir)]
       (testing "cleat, with the nrepl package and the project's paths on the load path"
         ;; nREPL's sources, or the directory with its package assembly
         (is (some #(or (str/ends-with? % (str "nrepl" Path/DirectorySeparatorChar "src"))
                        (File/Exists (Path/Combine ^String % "Nrepl.dll")))
                   command))
         (is (= ["-I" "src" "-I" "dev" "-m" "nrepl.cmdline"]
                (->> command (drop-while #(not= "src" %)) (cons "-I") (take 6)))))
       (testing "and its middleware, and the port"
         (is (= ["--middleware" "[my.app/wrap-x]" "--port" "7888"] (take-last 4 command)))))
     (testing "src by default, and no port"
       (spit-file dir "cleat.edn" "")
       (is (= ["-I" "src" "-m" "nrepl.cmdline"] (take-last 4 (jack-in/command "cleat" {} dir))))))))

(deftest with-server-test
  (call-with-temp-dir
   (fn [dir]
     (spit-file dir "cleat.edn" "{}")
     (spit-file dir "src/demo/core.clj" "(ns demo.core) (defn hello [] :hello)")
     (let [lines (atom [])
           process (jack-in/with-server dir (jack-in/command "cleat" {} dir) #(swap! lines conj %)
                     (fn [{:keys [^Process process] :as server}]
                       (testing "says where the server listens"
                         (with-client [client (select-keys server [:host :port])]
                           (is (= ["3"] (keep :value (nrepl/request client {:op "eval" :code "(+ 1 2)"}))))
                           (testing "with the project's source on its load path"
                             (is (= ["nil" ":hello"] (keep :value (nrepl/request client {:op "eval" :code "(require 'demo.core) (demo.core/hello)"} {:timeout-ms 60000})))))))
                       (testing "passes on what it prints"
                         (is (some #(str/starts-with? % "nREPL server started") @lines)))
                       (when-not (windows?)
                         (testing "with the server ignoring Ctrl-C, which is for whatever runs it"
                           (is (survives-sigint? process))))
                       process))]
       (testing "stops it after"
         (is (.HasExited ^Process process))
         ;; Windows has no SIGTERM, so the server has no say in it.
         (when-not (windows?)
           (testing "the way it cleans up after itself"
             (is (not (File/Exists (test-server/file dir ".nrepl-port")))))))))))

(deftest stop-test
  (when-not (windows?)
    (testing "the processes the server started go too"
      (call-with-temp-dir
       (fn [dir]
         (let [[process children]
               (jack-in/with-server dir ["sh" "-c" "sleep 60 & sleep 60 & echo 'nREPL server started on port 1 on host x'; wait"]
                 (fn [_])
                 (fn [{:keys [^Process process] :as server}]
                   (is (= {:host "x" :port 1} (select-keys server [:host :port])))
                   [process (children-of (.Id process))]))]
           (is (seq children))
           (is (.HasExited ^Process process))
           (is (not-any? alive? children))))))))

(deftest failed-test
  (call-with-temp-dir
   (fn [dir]
     (let [lines (atom [])]
       (when-not (windows?)
         (testing "a server that ends before it's up"
           (is (thrown-with-msg? clojure.lang.ExceptionInfo #"ended before it started, with exit status 3"
                                 (jack-in/with-server dir ["sh" "-c" "echo :oops >&2; exit 3"]
                                   #(swap! lines conj %)
                                   (fn [_]))))
           (testing "after what it said"
             (is (= [":oops"] @lines)))))
       (testing "one that can't start"
         (is (= ::jack-in/failed
                (:type (ex-data (is (thrown? clojure.lang.ExceptionInfo
                                             (jack-in/with-server (test-server/file dir "nope") ["no-such-program-anywhere"] (fn [_]) (fn [_])))))))))))))
