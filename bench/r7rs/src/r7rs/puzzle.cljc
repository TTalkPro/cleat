;;; PUZZLE -- Forest Baskett's Puzzle benchmark, originally written in Pascal.
;; 向量 → 对象数组（^objects 只为避免 JVM 上 aget 的反射）；被 set! 的全局 *iii* / *kount* → volatile；
;; trial 里的 call/cc 只作逃逸 → util/call-with-escape。
(ns r7rs.puzzle
  (:require [r7rs.common :refer [hide read-input run-r7rs-benchmark]]
            [r7rs.util :refer [make-vector call-with-escape]]))

(defn- my-iota [n]
  (loop [n n list ()]
    (if (zero? n) list (recur (- n 1) (cons (- n 1) list)))))

(def ^:private size 511)
(def ^:private classmax 3)
(def ^:private typemax 12)

(def ^:private iii (volatile! 0))
(def ^:private kount (volatile! 0))
(def ^:private d 8)

(def ^{:private true :tag 'objects} piececount (make-vector (+ classmax 1) 0))
(def ^{:private true :tag 'objects} klass (make-vector (+ typemax 1) 0))
(def ^{:private true :tag 'objects} piecemax (make-vector (+ typemax 1) 0))
(def ^{:private true :tag 'objects} puzzle (make-vector (+ size 1)))
(def ^{:private true :tag 'objects} p (make-vector (+ typemax 1)))

(defn- fit [i j]
  (let [end (aget piecemax i)]
    (loop [k 0]
      (if (or (> k end)
              (and (aget ^objects (aget p i) k)
                   (aget puzzle (+ j k))))
        (if (> k end) true false)
        (recur (+ k 1))))))

(defn- place [i j]
  (let [end (aget piecemax i)]
    (loop [k 0]
      (when-not (> k end)
        (when (aget ^objects (aget p i) k)
          (aset puzzle (+ j k) true)
          true)
        (recur (+ k 1))))
    (aset piececount
          (aget klass i)
          (- (aget piececount (aget klass i)) 1))
    (loop [k j]
      (if (or (> k size) (not (aget puzzle k)))
        (if (> k size) 0 k)
        (recur (+ k 1))))))

(defn- puzzle-remove [i j]
  (let [end (aget piecemax i)]
    (loop [k 0]
      (when-not (> k end)
        (when (aget ^objects (aget p i) k)
          (aset puzzle (+ j k) false)
          false)
        (recur (+ k 1))))
    (aset piececount
          (aget klass i)
          (+ (aget piececount (aget klass i)) 1))))

(defn- trial [j]
  (let [k (volatile! 0)]
    (call-with-escape
     (fn [return]
       (loop [i 0]
         (if (> i typemax)
           (do (vswap! kount + 1) false)
           (do
             (when-not (zero? (aget piececount (aget klass i)))
               (when (fit i j)
                 (vreset! k (place i j))
                 (if (or (trial @k) (zero? @k))
                   (do (vswap! kount + 1)
                       (return true))
                   (puzzle-remove i j))))
             (recur (+ i 1)))))))))

(defn- definePiece [iclass ii jj kk]
  (let [index (volatile! 0)]
    (loop [i 0]
      (when-not (> i ii)
        (loop [j 0]
          (when-not (> j jj)
            (loop [k 0]
              (when-not (> k kk)
                (vreset! index (+ i (* d (+ j (* d k)))))
                (aset ^objects (aget p @iii) @index true)
                (recur (+ k 1))))
            (recur (+ j 1))))
        (recur (+ i 1))))
    (aset klass @iii iclass)
    (aset piecemax @iii @index)
    (when-not (= @iii typemax)
      (vswap! iii + 1))))

(defn start [size]
  (vreset! kount 0)
  (loop [m 0]
    (when-not (> m size)
      (aset puzzle m true)
      (recur (+ m 1))))
  (loop [i 1]
    (when-not (> i 5)
      (loop [j 1]
        (when-not (> j 5)
          (loop [k 1]
            (when-not (> k 5)
              (aset puzzle (+ i (* d (+ j (* d k)))) false)
              (recur (+ k 1))))
          (recur (+ j 1))))
      (recur (+ i 1))))
  (loop [i 0]
    (when-not (> i typemax)
      (loop [m 0]
        (when-not (> m size)
          (aset ^objects (aget p i) m false)
          (recur (+ m 1))))
      (recur (+ i 1))))
  (vreset! iii 0)
  (definePiece 0 3 1 0)
  (definePiece 0 1 0 3)
  (definePiece 0 0 3 1)
  (definePiece 0 1 3 0)
  (definePiece 0 3 0 1)
  (definePiece 0 0 1 3)

  (definePiece 1 2 0 0)
  (definePiece 1 0 2 0)
  (definePiece 1 0 0 2)

  (definePiece 2 1 1 0)
  (definePiece 2 1 0 1)
  (definePiece 2 0 1 1)

  (definePiece 3 1 1 1)

  (aset piececount 0 (num 13))
  (aset piececount 1 (num 3))
  (aset piececount 2 (num 1))
  (aset piececount 3 (num 1))
  (let [m (+ (* d (+ d 1)) 1)
        n (volatile! 0)]
    (if (fit 0 m)
      (vreset! n (place 0 m))
      (do (newline) (print "Error.")))
    (if (trial @n)
      @kount
      false)))

(doseq [i (my-iota (+ typemax 1))]
  (aset p i (make-vector (+ size 1))))

(defn -main [& _]
  (let [count (read-input) input1 (read-input) output (read-input)]
    (run-r7rs-benchmark (str "puzzle:" count) count
                        (fn [] (start (hide count input1)))
                        (fn [result] (= result output)))))
