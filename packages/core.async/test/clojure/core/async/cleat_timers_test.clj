;;; CLEAT-185: not upstream. timeout-worker used to wait (TimeSpan. (- earliest now)); when the earliest entry expired between
;;; process-entries and that computation the wait was negative: WaitOne truncates to whole milliseconds, so (-2, -1] ms became
;;; -1 = Timeout.Infinite (the worker slept until the next timeout call and no channel closed: timers-test hung, ~1 in 5) and
;;; below that it threw on the worker thread (the process died). Many short timeouts from several threads hit that window;
;;; the deref limits turn a regression into a failure instead of a hang.
(ns clojure.core.async.cleat-timers-test
  (:require [clojure.test :refer :all]
            [clojure.core.async :as a]))

(deftest many-short-timeouts-all-close
  (let [n 1500
        workers (doall (for [_ (range 4)]
                         (future (dotimes [_ n] (a/<!! (a/timeout (rand-int 4)))) :done)))]
    ;; one deadline for all four (each normally finishes in about 10 s)
    (let [deadline (+ (System.Environment/TickCount64) 45000)]
      (is (= [:done :done :done :done]
             (mapv #(deref % (max 0 (- deadline (System.Environment/TickCount64))) :timed-out) workers))))))

(deftest timeout-after-a-quiet-period-still-closes
  ;; the worker must not be left in an infinite wait once the set is empty again
  (a/<!! (a/timeout 5))
  (System.Threading.Thread/Sleep 50)
  (let [start (.Ticks DateTime/UtcNow)
        r (deref (future (a/alts!! [(a/timeout 20) (a/timeout 5000)])) 10000 :timed-out)]
    (is (not= :timed-out r))
    (is (< (- (.Ticks DateTime/UtcNow) start) (* 2000 TimeSpan/TicksPerMillisecond)))))
