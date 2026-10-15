;;; RAY -- Ray-trace a simple scene with spheres, generating a ".pgm" file.
;;; Translated to Scheme from Paul Graham's book ANSI Common Lisp, Example 9.8
;;; 点、球、(vector surface hit) 都不被修改 → Clojure 向量；全局 *world* 的 set! → volatile。
;;; 输出文件：私有的 open-output / port-write / file-exists? / delete-file（读者条件：JVM BufferedWriter，CLR StreamWriter），
;;; 原版的 display / write 对字符串与整数都只是写出文本。
(ns r7rs.ray
  (:require [r7rs.common :refer [hide read-input run-r7rs-benchmark]]
            [r7rs.util :as u]))

(defn- file-exists? [path]
  #?(:clj (.exists (java.io.File. ^String path)) :cljr (System.IO.File/Exists ^String path)))
(defn- delete-file [path]
  #?(:clj (.delete (java.io.File. ^String path)) :cljr (System.IO.File/Delete ^String path)))
(defn- open-output [path]
  #?(:clj (java.io.BufferedWriter. (java.io.FileWriter. ^String path))
     :cljr (System.IO.StreamWriter. ^String path)))
(defn- port-write [p x]
  #?(:clj (.write ^java.io.Writer p (str x)) :cljr (.Write ^System.IO.TextWriter p (str x))))
(defn- port-newline [p] (port-write p "\n"))
(defn- close-port [p]
  #?(:clj (.close ^java.io.Writer p) :cljr (.Close ^System.IO.TextWriter p)))
(defn- call-with-output-file [path f]
  (let [p (open-output path)]
    (try (f p) (finally (close-port p)))))

(defn make-point [x y z]
  [x y z])

(defn point-x [p] (nth p 0))
(defn point-y [p] (nth p 1))
(defn point-z [p] (nth p 2))

(defn sq [x] (* x x))

(defn mag [x y z]
  (u/sqrt (+ (sq x) (sq y) (sq z))))

(defn unit-vector [x y z]
  (let [d (mag x y z)]
    (make-point (/ x d) (/ y d) (/ z d))))

(defn distance [p1 p2]
  (mag (- (point-x p1) (point-x p2))
       (- (point-y p1) (point-y p2))
       (- (point-z p1) (point-z p2))))

(defn minroot [a b c]
  (if (zero? a)
    (/ (- c) b)
    (let [disc (- (sq b) (* 4.0 a c))]
      (if (neg? disc)
        false
        (let [discrt (u/sqrt disc)
              minus-b (- b)
              two-a (* 2.0 a)]
          (min (/ (+ minus-b discrt) two-a)
               (/ (- minus-b discrt) two-a)))))))

(def ^:private world (volatile! '()))

(def eye (make-point 0.0 0.0 200.0))

(declare color-at sendray first-hit lambert surface-color intersect normal sphere-intersect sphere-normal)

(defn tracer [pathname res]
  (when (file-exists? pathname)
    (delete-file pathname))
  (call-with-output-file
    pathname
    (fn [p]
      (let [extent (* res 100)]
        (port-write p "P2 ")
        (port-write p extent)
        (port-write p " ")
        (port-write p extent)
        (port-write p " 255")
        (port-newline p)
        (loop [y 0]
          (when-not (= y extent)
            (loop [x 0]
              (when-not (= x extent)
                (port-write p (color-at
                               (+ -50.0
                                  (/ (double x) (double res)))
                               (+ -50.0
                                  (/ (double y) (double res)))))
                (port-newline p)
                (recur (+ x 1))))
            (recur (+ y 1))))))))

(defn color-at [x y]
  (let [ray (unit-vector (- x (point-x eye))
                         (- y (point-y eye))
                         (- (point-z eye)))]
    (long (u/fl-round (* (sendray eye ray) 255.0)))))

(defn sendray [pt ray]
  (let [x (first-hit pt ray)
        s (nth x 0)
        int (nth x 1)]
    (if s
      (* (lambert s int ray)
         (surface-color s))
      0.0)))

(defn first-hit [pt ray]
  (loop [lst @world surface false hit false dist 1e308]
    (if (empty? lst)
      [surface hit]
      (let [s (first lst)]
        (let [h (intersect s pt ray)]
          (if h
            (let [d (distance h pt)]
              (if (< d dist)
                (recur (rest lst) s h d)
                (recur (rest lst) surface hit dist)))
            (recur (rest lst) surface hit dist)))))))

(defn lambert [s int ray]
  (let [n (normal s int)]
    (max 0.0
         (+ (* (point-x ray) (point-x n))
            (* (point-y ray) (point-y n))
            (* (point-z ray) (point-z n))))))

(defn make-sphere [color radius center]
  [color radius center])

(defn sphere-color [s] (nth s 0))
(defn sphere-radius [s] (nth s 1))
(defn sphere-center [s] (nth s 2))

(defn defsphere [x y z r c]
  (let [s (make-sphere c r (make-point x y z))]
    (vreset! world (cons s @world))
    s))

(defn surface-color [s]
  (sphere-color s))

(defn intersect [s pt ray]
  (sphere-intersect s pt ray))

(defn sphere-intersect [s pt ray]
  (let [xr (point-x ray)
        yr (point-y ray)
        zr (point-z ray)
        c (sphere-center s)
        n (minroot
           (+ (sq xr) (sq yr) (sq zr))
           (* 2.0
              (+ (* (- (point-x pt) (point-x c)) xr)
                 (* (- (point-y pt) (point-y c)) yr)
                 (* (- (point-z pt) (point-z c)) zr)))
           (+ (sq (- (point-x pt) (point-x c)))
              (sq (- (point-y pt) (point-y c)))
              (sq (- (point-z pt) (point-z c)))
              (- (sq (sphere-radius s)))))]
    (if n
      (make-point (+ (point-x pt) (* n xr))
                  (+ (point-y pt) (* n yr))
                  (+ (point-z pt) (* n zr)))
      false)))

(defn normal [s pt]
  (sphere-normal s pt))

(defn sphere-normal [s pt]
  (let [c (sphere-center s)]
    (unit-vector (- (point-x c) (point-x pt))
                 (- (point-y c) (point-y pt))
                 (- (point-z c) (point-z pt)))))

(defn ray-test [res output-file]
  (vreset! world '())
  (defsphere 0.0 -300.0 -1200.0 200.0 0.8)
  (defsphere -80.0 -150.0 -1200.0 200.0 0.7)
  (defsphere 70.0 -100.0 -1200.0 200.0 0.9)
  (loop [x -2]
    (when-not (> x 2)
      (loop [z 2]
        (when-not (> z 7)
          (defsphere
            (* (double x) 200.0)
            300.0
            (* (double z) -400.0)
            40.0
            0.75)
          (recur (+ z 1))))
      (recur (+ x 1))))
  (tracer output-file res))

(defn run [input output]
  (ray-test input output)
  'ok)

(defn -main [& _]
  (let [count (read-input) input1 (read-input) input2 (read-input) output (read-input)]
    (run-r7rs-benchmark (str "ray" ":" count) count
                        (fn [] (run (hide count input1) (hide count input2)))
                        (fn [result] (= result output)))))
