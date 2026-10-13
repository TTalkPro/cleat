;   Copyright (c) Rich Hickey. All rights reserved.
;   The use and distribution terms for this software are covered by the
;   Eclipse Public License 1.0 (http://opensource.org/licenses/eclipse-1.0.php)
;   which can be found in the file epl-v10.html at the root of this distribution.
;   By using this software in any fashion, you are agreeing to be bound by
;   the terms of this license.
;   You must not remove this notice, or any other, from this software.
;; cleat: running build functions (clj -T:build upstream), designs/18 §5.

(ns clojure.tools.build.test-main
  (:require
    [clojure.edn :as edn]
    [clojure.string :as str]
    [clojure.test :refer :all]
    [clojure.tools.build.api :as api]
    [clojure.tools.build.main :as main]
    [clojure.tools.build.test-util :refer :all])
  (:import
    [System.IO Path]))

(deftest test-run-build-function
  (with-test-dir "test-data/p1"
    (spit (project-path "build.clj")
          (str "(ns build (:require [clojure.tools.build.api :as b]))\n"
               "(defn write [{:keys [x path]}] (b/write-file {:path path :content x}))\n"))
    (let [{:keys [exit err]} (cleat *test-dir*
                                    "-I" (Path/Combine package-dir "src")
                                    "-I" (Path/Combine package-dir ".." "tools.namespace" "src")
                                    "-I" "."
                                    "-m" "clojure.tools.build.main" "write" ":x" "[1 :two \"3\"]" ":path" "\"target/out.edn\"")]
      (is (= 0 exit) err)
      (is (= [1 :two "3"] (edn/read-string (slurp (project-path "target/out.edn"))))))
    (let [{:keys [exit err]} (cleat *test-dir* "-I" (Path/Combine package-dir "src") "-I" "."
                                    "-m" "clojure.tools.build.main" "write" ":x")]
      (is (= 2 exit))
      (is (re-find #"must come in pairs" err)))))

;; cleat-072: the -X / -T modes (clj -X / -T), as plans and through the cleat host

(defn- tool-project []
  (api/write-file {:path "cleat.edn"
                   :content '{:paths ["src"]
                              :deps {my/lib {:local/root "lib"}}
                              :aliases {:greet {:exec-fn app.core/greet :exec-args {:name "world" :punct "!"}}
                                        :app {:ns-default app.core}
                                        :mytool {:paths ["tools"] :ns-default my.tool :exec-args {:verbose false}}}}})
  (api/write-file {:path "lib/cleat.edn" :content {:paths ["src"]}})
  (api/write-file {:path "src/app/core.clj"
                   :string "(ns app.core)\n(defn greet [{:keys [name punct]}] (println (str \"hello, \" name punct)))\n"})
  (api/write-file {:path "tools/my/tool.clj"
                   :string "(ns my.tool)\n(defn hi [m] (prn (into (sorted-map) m)) (prn (some? (find-ns 'app.core))))\n"}))

(deftest test-exec-plan
  (with-test-dir "test-data/p1"
    (api/set-project-root! *test-dir*)
    (tool-project)
    (api/with-project-root *test-dir*
      (testing "-X: :exec-fn and :exec-args from the alias, key/value pairs (paths too) over them"
        (let [{f :fn params :params load-path :load-path} (main/exec-plan "X" "-X:greet" [":name" "\"you\"" "[:opts :x]" "1"] nil)]
          (is (= 'app.core/greet f))
          (is (= {:name "you" :punct "!" :opts {:x 1}} params))
          (is (some #(str/ends-with? % "lib/src") load-path) "the project's deps")))
      (testing "-X: an unqualified function takes the :ns-default"
        (is (= 'app.core/greet (:fn (main/exec-plan "X" "-X:app" ["greet"] nil)))))
      (testing "-X: a qualified function needs no alias"
        (is (= 'app.core/greet (:fn (main/exec-plan "X" "-X" ["app.core/greet"] nil)))))
      (testing "-T with a tool alias: its :paths, not the project's paths or deps"
        (let [{f :fn params :params load-path :load-path} (main/exec-plan "T" "-T:mytool" ["hi" ":x" "2"] nil)]
          (is (= 'my.tool/hi f))
          (is (= {:verbose false :x 2} params))
          (is (= ["tools"] load-path))))
      (testing "-T without such an alias: the name is the namespace, the project root the load path (cleat)"
        (let [{f :fn load-path :load-path} (main/exec-plan "T" "-T:build" ["jar"] nil)]
          (is (= 'build/jar f))
          (is (= ["."] load-path))))
      (testing "-Sdeps merges over cleat.edn"
        (is (= ["src" "extra"] (vec (take 2 (:load-path (main/deps-args "-M" "{:paths [\"src\" \"extra\"]}"))))))))))

(deftest test-cleat-host-modes
  (with-test-dir "test-data/p1"
    (api/set-project-root! *test-dir*)
    (tool-project)
    (testing "cleat -X:alias"
      (is (= "hello, there!\n" (:out (cleat *test-dir* "-X:greet" ":name" "\"there\"")))))
    (testing "cleat -T:alias: the tool doesn't see the project's code"
      (is (= "{:verbose false, :x 3}\nfalse\n" (:out (cleat *test-dir* "-T:mytool" "hi" ":x" "3")))))
    (testing "cleat -Spath"
      (is (str/starts-with? (:out (cleat *test-dir* "-Spath")) "src")))))
