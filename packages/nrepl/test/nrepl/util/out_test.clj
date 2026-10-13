(ns nrepl.util.out-test
  (:require [clojure.test :refer :all]
            [clojure.string :as str]
            [nrepl.test-helpers :refer [is+ newline->sys]]
            [nrepl.util.out :as out]
            [nrepl.util.streams :as streams])
  (:import (System Console)
           (System.Threading Thread)))

;; cleat: the standard streams are Console.Out and Console.Error. CLR writers
;; take characters, so instead of UTF-8 tearing the last test checks that a
;; surrogate pair isn't split between two chunks.

(defn- reset-callbacks-fixture
  "Helper to reset the internal state for testing."
  [f]
  (reset! @#'out/callbacks {:out {}, :err {}})
  (f))

(use-fixtures :each reset-callbacks-fixture)

(defn- wait-for
  "Polls `pred` until it returns a truthy value or `timeout-ms` elapses.
  Callbacks are invoked on nREPL's transport executor, so the tests have to
  wait for them to have run instead of assuming they already have."
  [pred timeout-ms]
  (let [deadline (+ (Environment/TickCount64) timeout-ms)]
    (loop []
      (or (pred)
          (when (< (Environment/TickCount64) deadline)
            (Thread/Sleep 10)
            (recur))))))

(deftest test-wrap-standard-streams-idempotency
  (testing "wrap-standard-streams is idempotent"
    (out/wrap-standard-streams)
    (let [original-out (:out @@#'out/original-print-streams)
          original-err (:err @@#'out/original-print-streams)
          first-out Console/Out
          first-err Console/Error]
      (is (not= original-out first-out))
      (is (not= original-err first-err))

      ;; Second call should not change streams again
      (out/wrap-standard-streams)
      (is (identical? first-out Console/Out))
      (is (identical? first-err Console/Error)))))

(deftest test-output-interception
  (testing "callbacks are invoked when output is written"
    (out/wrap-standard-streams)
    (let [out-calls (atom [])
          err-calls (atom [])]
      (out/set-callback :out :test1 #(swap! out-calls conj [:test1 %]))
      (out/set-callback :out :test2 #(swap! out-calls conj [:test2 %]))
      (out/set-callback :err :test3 #(swap! err-calls conj [:test3 %]))

      ;; Write to streams
      (.WriteLine Console/Out "Hello stdout")
      (.WriteLine Console/Error "Hello stderr")
      (wait-for #(and (= 2 (count @out-calls)) (= 1 (count @err-calls))) 5000)

      (is+ #{[:test1 (newline->sys "Hello stdout\n")]
             [:test2 (newline->sys "Hello stdout\n")]}
           (set @out-calls))
      (is+ [[:test3 (newline->sys "Hello stderr\n")]]
           @err-calls))))

(deftest test-root-binding-updates
  (testing "*out* and *err* root bindings are updated to point to wrapped streams"
    (out/wrap-standard-streams)
    (let [out-calls (atom [])
          err-calls (atom [])
          thread (fn [f]
                   (doto (Thread. ^System.Threading.ThreadStart (gen-delegate System.Threading.ThreadStart [] (f)))
                     (.Start)))]
      (out/set-callback :out :test1 #(swap! out-calls conj [:test1 %]))
      (out/set-callback :out :test2 #(swap! out-calls conj [:test2 %]))
      (out/set-callback :err :test3 #(swap! err-calls conj [:test3 %]))

      ;; Write to streams from threads that see the root bindings.
      (thread #(println "Hello stdout"))
      (thread #(binding [*out* *err*]
                 (println "Hello stderr")))
      (wait-for #(and (= 2 (count @out-calls)) (= 1 (count @err-calls))) 5000)

      (is+ #{[:test1 (newline->sys "Hello stdout\n")]
             [:test2 (newline->sys "Hello stdout\n")]}
           (set @out-calls))
      (is+ [[:test3 (newline->sys "Hello stderr\n")]]
           @err-calls))))

(deftest test-error-handling-in-callbacks
  (testing "exceptions in one callback shouldn't break others"
    (out/wrap-standard-streams)
    (let [out-calls (atom [])]
      (out/set-callback :out ::bad-callback #(/ 1 %))
      (out/set-callback :out ::good-callback #(swap! out-calls conj %))

      (dotimes [_ 10]
        (.WriteLine Console/Out "1"))
      (wait-for #(= 10 (count @out-calls)) 5000)

      (is+ (vec (repeat 10 (newline->sys "1\n")))
           @out-calls))))

(deftest test-surrogate-pair-handling
  (doseq [buffer-len (range 10 20)
          prefix-len (range 1 (inc buffer-len))]
    (let [chunks   (atom [])
          callback (fn [s] (swap! chunks conj s))
          ^System.IO.TextWriter writer (streams/callback-writer callback buffer-len)
          emoji (String. (char-array [(char 0xD83D) (char 0xDE00)]))]
      (.Write writer (apply str (repeat prefix-len \-)))
      (.Write writer emoji)
      (.Flush writer)
      (is (str/ends-with? (last @chunks) emoji) (str @chunks))
      (is (every? #(not (Char/IsHighSurrogate (last %))) @chunks) (str @chunks)))))
