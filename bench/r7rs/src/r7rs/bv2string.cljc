;;; BV2STRING -- Tests of string <-> bytevector conversions.
;;; Copyright 2007 William D Clinger (see the original for the full notice).
;;; 字节向量 → 字节数组；utf8->string / string->utf8 用各平台库的 UTF-8 编解码（测的就是宿主库）。
(ns r7rs.bv2string
  (:refer-clojure :exclude [test])
  (:require [r7rs.common :refer [hide read-input run-r7rs-benchmark]]
            [r7rs.util :refer [mcons mcar set-mcar!]]))

(defn- utf8->string [^bytes bv]
  #?(:clj (String. bv java.nio.charset.StandardCharsets/UTF_8)
     :cljr (.GetString System.Text.Encoding/UTF8 bv)))
(defn- string->utf8 [^String s]
  #?(:clj (.getBytes s java.nio.charset.StandardCharsets/UTF_8)
     :cljr (.GetBytes System.Text.Encoding/UTF8 s)))

;; Crude test rig, just for benchmarking.
(def failed-tests (atom ()))

(defn test [name actual expected]
  (when-not (= actual expected)
    (print "******** FAILED TEST ******** ")
    (println name)
    (swap! failed-tests #(cons name %))))

;; Scheme 的 round（就近取偶）作用于精确有理数 n/4
(defn- round-quarter [n]
  (let [q (quot n 4) r (rem n 4)]
    (cond (< r 2) q
          (> r 2) (+ q 1)
          :else (if (even? q) q (+ q 1)))))

(defn string-bytevector-tests [random-stress-tests random-stress-test-max-size]
  (let [test-roundtrip
        (fn [bvec tostring tobvec]
          (let [s1 (tostring bvec)
                b2 (tobvec s1)
                s2 (tostring b2)]
            (test "round trip of string conversion" (= s1 s2) true)))

        ;; This random number generator doesn't have to be good.
        ;; It just has to be fast.  （原版 letrec 里被 set! 的 x：可变序对当盒子）
        random
        (let [a 701 x (mcons 1 nil) c 743483 m 524287
              random14 (fn [n]
                         (set-mcar! x (rem (+ (* a (mcar x)) c) (+ m 1)))
                         (rem (quot (mcar x) 8) n))
              rand-loop (fn [q r n]
                      (if (zero? q)
                        (rem r n)
                        (recur (quot q 16384)
                               (+ (* 16384 r) (random14 16384))
                               n)))]
          (fn [n]
            (if (< n 16384)
              (random14 n)
              (rand-loop (quot n 16384) (random14 16384) n))))

        ;; Returns a random bytevector of length up to n,
        ;; with all elements less than 128.
        random-bytevector
        (fn [n]
          (let [n (random n)
                ^bytes bv (byte-array n)]
            (loop [i 0]
              (if (= i n)
                bv
                (do (aset bv i (byte (random 128)))
                    (recur (+ i 1)))))))

        ;; Returns a random bytevector of even length up to n.
        random-bytevector2
        (fn [n]
          (let [n (random n)
                n (if (odd? n) (+ n 1) n)
                ^bytes bv (byte-array n)]
            (loop [i 0]
              (if (= i n)
                bv
                (do (aset bv i (byte (random 128)))
                    (recur (+ i 1)))))))

        ;; Returns a random bytevector of multiple-of-4 length up to n.
        random-bytevector4
        (fn [n]
          (let [n (random n)
                n (* 4 (round-quarter n))
                ^bytes bv (byte-array n)]
            (loop [i 0]
              (if (= i n)
                bv
                (do (aset bv i (byte (random 128)))
                    (recur (+ i 1)))))))]

    (test-roundtrip (random-bytevector 10) utf8->string string->utf8)

    (loop [i 0]
      (when-not (= i random-stress-tests)
        (test-roundtrip (random-bytevector random-stress-test-max-size)
                        utf8->string string->utf8)
        (recur (+ i 1))))))

(defn -main [& _]
  (let [count (read-input) input1 (read-input) input2 (read-input) output (read-input)
        s3 (str count) s2 (str input2) s1 (str input1) name "bv2string"]
    (run-r7rs-benchmark (str name ":" s1 ":" s2 ":" s3) count
                        (fn []
                          (string-bytevector-tests (hide count input1) (hide count input2))
                          (clojure.core/count @failed-tests))
                        (fn [result] (= result output)))))
