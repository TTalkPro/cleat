;;; MAZE -- Constructs a maze on a hexagonal grid, written by Olin Shivers.
;;; 记录（原版用 vector 表示的 wall / cell / harr）→ 对象数组；被 set-car! / set-cdr! 的序对与点对 → r7rs.util 的可变序对；
;;; dig-maze 的 call/cc 只用作逃逸 → call-with-escape；原版的全局 output（set!）→ 一个可变序对当盒子。
;;; 期望输出是 Scheme 字符表（#\x 语法 Clojure 读不了），由 read-expected-chars 从标准输入的剩余部分解析。
(ns r7rs.maze
  (:refer-clojure :exclude [bit-test])
  (:require [r7rs.common :refer [hide read-input run-r7rs-benchmark]]
            [r7rs.util :refer [mcons mcar mcdr set-mcar! set-mcdr! mpair? make-vector call-with-escape char-alphabetic?]]))

;;; R6RS procedures needed by this benchmark.

(defn bitwise-not [x]
  (- (- x) 1))

(declare div)

(defn bitwise-and [x y] ;; two arguments are enough for this benchmark
  (cond (= x 0) 0
        (= y 0) 0
        (= x -1) y
        (= y -1) x
        :else
        (let [z (bitwise-and (div x 2) (div y 2))]
          (if (and (odd? x) (odd? y))
            (+ z z 1)
            (+ z z)))))

(defn div [x y]
  (cond (and (integer? x)
             (integer? y)
             (>= x 0))
        (quot x y)
        (< y 0)
        ;; x < 0, y < 0
        (let [q (quot x y)
              r (- x (* q y))]
          (if (= r 0)
            q
            (+ q 1)))
        :else
        ;; x < 0, y > 0
        (let [q (quot x y)
              r (- x (* q y))]
          (if (= r 0)
            q
            (- q 1)))))

(defn mod* [x y]
  (cond (and (integer? x)
             (integer? y)
             (>= x 0))
        (rem x y)
        (< y 0)
        ;; x < 0, y < 0
        (let [q (quot x y)
              r (- x (* q y))]
          (if (= r 0)
            0
            (- r y)))
        :else
        ;; x < 0, y > 0
        (let [q (quot x y)
              r (- x (* q y))]
          (if (= r 0)
            0
            (+ r y)))))

;;; End of R6RS procedures.

;; Minimal Standard Random Number Generator
;; Park & Miller, CACM 31(10), Oct 1988, 32 bit integer version.

(defn random-state [n]
  (mcons n false))

(defn rand* [state]
  (let [seed (mcar state)
        A 2813  ;; 48271
        M 8388607 ;; 2147483647
        Q 2787 ;; 44488
        R 2699] ;; 3399
    (let [hi (div seed Q)
          lo (mod* seed Q)
          test (- (* A lo) (* R hi))
          val (if (> test 0) test (+ test M))]
      (set-mcar! state val)
      val)))

(defn random-int [n state]
  (mod* (rand* state) n))

;;; Tarjan's amortised union-find data structure.
;;; Copyright (c) 1995 by Olin Shivers.

(defn base-set [nelts] (mcons nelts nil))

(defn get-set-root [s]
  (loop [r s] ;; Find the last pair
    (let [next (mcdr r)] ;; in the list. That's
      (if (mpair? next)
        (recur next) ;; the root r.
        (do
          (when-not (identical? r s) ;; Now zip down the list again,
            (loop [x s] ;; changing everyone's cdr to r.
              (let [next (mcdr x)]
                (when-not (identical? r next)
                  (set-mcdr! x r)
                  (recur next)))))
          r))))) ;; Then return r.

(defn set-equal? [s1 s2] (identical? (get-set-root s1) (get-set-root s2)))

(defn set-size [s] (mcar (get-set-root s)))

(defn union! [s1 s2]
  (let [r1 (get-set-root s1)
        r2 (get-set-root s2)
        n1 (set-size r1)
        n2 (set-size r2)
        n (+ n1 n2)]
    (if (> n1 n2)
      (do (set-mcdr! r2 r1)
          (set-mcar! r1 n))
      (do (set-mcdr! r1 r2)
          (set-mcar! r2 n)))))

;;; Building mazes with union/find disjoint sets.
;;; Copyright (c) 1995 by Olin Shivers.

(defn make-wall [owner neighbor bit]
  (let [^objects v (make-vector 4)]
    (aset v 0 'wall) (aset v 1 owner) (aset v 2 neighbor) (aset v 3 bit)
    v))

(defn wall:owner [^objects o]          (aget o 1))
(defn set-wall:owner [^objects o v]    (aset o 1 v))
(defn wall:neighbor [^objects o]       (aget o 2))
(defn set-wall:neighbor [^objects o v] (aset o 2 v))
(defn wall:bit [^objects o]            (aget o 3))
(defn set-wall:bit [^objects o v]      (aset o 3 v))

(defn make-cell [reachable id]
  (let [^objects v (make-vector 6)]
    (aset v 0 'cell) (aset v 1 reachable) (aset v 2 id) (aset v 3 (num -1)) (aset v 4 false) (aset v 5 false)
    v))

(defn cell:reachable [^objects o]       (aget o 1))
(defn set-cell:reachable [^objects o v] (aset o 1 v))
(defn cell:id [^objects o]              (aget o 2))
(defn set-cell:id [^objects o v]        (aset o 2 v))
(defn cell:walls [^objects o]           (aget o 3))
(defn set-cell:walls [^objects o v]     (aset o 3 v))
(defn cell:parent [^objects o]          (aget o 4))
(defn set-cell:parent [^objects o v]    (aset o 4 v))
(defn cell:mark [^objects o]            (aget o 5))
(defn set-cell:mark [^objects o v]      (aset o 5 v))

;;; Iterates in reverse order.

(defn vector-for-each-rev [proc ^objects v]
  (loop [i (- (alength v) 1)]
    (when (>= i 0)
      (proc (aget v i))
      (recur (- i 1)))))

;;; Randomly permute a vector.

(defn permute-vec! [^objects v random-state]
  (loop [i (- (alength v) 1)]
    (when (> i 1)
      (let [elt-i (aget v i)
            j (random-int i random-state)] ;; j in [0,i)
        (aset v i (aget v j))
        (aset v j elt-i))
      (recur (- i 1))))
  v)

;;; This is the core of the algorithm.

(defn dig-maze [walls ncells]
  (call-with-escape
   (fn [quit]
     (vector-for-each-rev
      (fn [wall] ;; For each wall,
        (let [c1 (wall:owner wall) ;; find the cells on
              set1 (cell:reachable c1)
              c2 (wall:neighbor wall) ;; each side of the wall
              set2 (cell:reachable c2)]
          ;; If there is no path from c1 to c2, knock down the
          ;; wall and union the two sets of reachable cells.
          ;; If the new set of reachable cells is the whole set
          ;; of cells, quit.
          (when-not (set-equal? set1 set2)
            (let [walls (cell:walls c1)
                  wall-mask (bitwise-not (wall:bit wall))]
              (union! set1 set2)
              (set-cell:walls c1 (bitwise-and walls wall-mask))
              (when (= (set-size set1) ncells) (quit false))))))
      walls))))

;;; Build a DFS tree from ROOT.

(defn dfs-maze [maze root do-children]
  (letfn [(search [node parent]
            (set-cell:parent node parent)
            (do-children (fn [child]
                           (when-not (identical? child parent)
                             (search child node)))
                         maze node))]
    (search root false)))

;;; Move the root to NEW-ROOT.

(defn reroot-maze [new-root]
  (loop [node new-root new-parent false]
    (let [old-parent (cell:parent node)]
      (set-cell:parent node new-parent)
      (when old-parent (recur old-parent node)))))

;;; How far from CELL to the root?

(defn path-length [cell]
  (loop [len 0 node (cell:parent cell)]
    (if (not node)
      len
      (recur (+ len 1) (cell:parent node)))))

;;; Mark the nodes from NODE back to root. Used to mark the winning path.

(defn mark-path [node]
  (loop [node node]
    (set-cell:mark node true)
    (when-let [p (cell:parent node)] (recur p))))

;;; Hex arrays
;;; Copyright (c) 1995 by Olin Shivers.

(defn make-harr [nrows ncols elts]
  (let [^objects v (make-vector 4)]
    (aset v 0 'harr) (aset v 1 nrows) (aset v 2 ncols) (aset v 3 elts)
    v))

(defn harr:nrows [^objects o]       (aget o 1))
(defn set-harr:nrows [^objects o v] (aset o 1 v))
(defn harr:ncols [^objects o]       (aget o 2))
(defn set-harr:ncols [^objects o v] (aset o 2 v))
(defn harr:elts [^objects o]        (aget o 3))
(defn set-harr:elts [^objects o v]  (aset o 3 v))

(defn harr [r c]
  (make-harr r c (make-vector (* r c))))

(defn href [ha x y]
  (let [r (div y 2)
        c (div x 3)]
    (aget ^objects (harr:elts ha)
          (+ (* (harr:ncols ha) r) c))))

(defn hset! [ha x y val]
  (let [r (div y 2)
        c (div x 3)]
    (aset ^objects (harr:elts ha)
          (+ (* (harr:ncols ha) r) c)
          val)))

(defn href-rc [ha r c]
  (aget ^objects (harr:elts ha)
        (+ (* (harr:ncols ha) r) c)))

;;; Create a nrows x ncols hex array. The elt centered on coord (x, y)
;;; is the value returned by (PROC x y).

(defn harr-tabulate [nrows ncols proc]
  (let [^objects v (make-vector (* nrows ncols))]
    (loop [r (- nrows 1)]
      (when-not (< r 0)
        (loop [c 0 i (* r ncols)]
          (when-not (= c ncols)
            (aset v i (proc (* 3 c) (+ (* 2 r) (bitwise-and c 1))))
            (recur (+ c 1) (+ i 1))))
        (recur (- r 1))))
    (make-harr nrows ncols v)))

(defn harr-for-each [proc harr]
  (vector-for-each-rev proc (harr:elts harr)))

;;; Hexagonal hackery for maze generation.
;;; Copyright (c) 1995 by Olin Shivers.

(def south-west 1)
(def south      2)
(def south-east 4)

(defn gen-maze-array [r c]
  (harr-tabulate r c (fn [x y] (make-cell (base-set 1) (mcons x y)))))

;;; This could be made more efficient.
(defn make-wall-vec [harr]
  (let [nrows (harr:nrows harr)
        ncols (harr:ncols harr)
        xmax (* 3 (- ncols 1))
        ;; Accumulate walls.  （原版 set! 的局部变量 walls：可变序对当盒子）
        walls (mcons () nil)
        add-wall (fn [o n b] ;; owner neighbor bit
                   (set-mcar! walls (cons (make-wall o n b) (mcar walls))))]

    ;; Do everything but the bottom row.
    (loop [x (* (- ncols 1) 3)]
      (when-not (< x 0)
        (loop [y (+ (* (- nrows 1) 2) (bitwise-and x 1))]
          (when-not (<= y 1) ;; Don't do bottom row.
            (let [hex (href harr x y)]
              (when-not (zero? x)
                (add-wall hex (href harr (- x 3) (- y 1)) south-west))
              (add-wall hex (href harr x (- y 2)) south)
              (when (< x xmax)
                (add-wall hex (href harr (+ x 3) (- y 1)) south-east)))
            (recur (- y 2))))
        (recur (- x 3))))

    ;; Do the SE and SW walls of the odd columns on the bottom row.
    ;; If the rightmost bottom hex lies in an odd column, however,
    ;; don't add it's SE wall -- it's a corner hex, and has no SE neighbor.
    (when (> ncols 1)
      (let [rmoc-x (+ 3 (* 6 (div (- ncols 2) 2)))]
        ;; Do rightmost odd col.
        (let [rmoc-hex (href harr rmoc-x 1)]
          (when (< rmoc-x xmax) ;; Not  a corner -- do E wall.
            (add-wall rmoc-hex (href harr xmax 0) south-east))
          (add-wall rmoc-hex (href harr (- rmoc-x 3) 0) south-west))

        (loop [x (- rmoc-x 6)] ;; Do the rest of the bottom row's odd cols.
          (when-not (< x 3) ;; 3 is X coord of leftmost odd column.
            (add-wall (href harr x 1) (href harr (- x 3) 0) south-west)
            (add-wall (href harr x 1) (href harr (+ x 3) 0) south-east)
            (recur (- x 6))))))

    ;; list->vector
    (object-array (mcar walls))))

(declare for-each-hex-child)

;;; Find the cell ctop from the top row, and the cell cbot from the bottom
;;; row such that cbot is furthest from ctop.
;;; Return [ctop-x, ctop-y, cbot-x, cbot-y].

(defn pick-entrances [harr]
  (dfs-maze harr (href-rc harr 0 0) for-each-hex-child)
  (let [nrows (harr:nrows harr)
        ncols (harr:ncols harr)]
    (loop [max-len -1
           entrance false
           exit false
           tcol (- ncols 1)]
      (if (< tcol 0)
        [entrance exit]
        (let [top-cell (href-rc harr (- nrows 1) tcol)]
          (reroot-maze top-cell)
          (let [result
                (loop [max-len max-len
                       entrance entrance
                       exit exit
                       bcol (- ncols 1)]
                  (if (< bcol 0)
                    [max-len entrance exit]
                    (let [this-len (path-length (href-rc harr 0 bcol))]
                      (if (> this-len max-len)
                        (recur this-len tcol bcol (- bcol 1))
                        (recur max-len entrance exit (- bcol 1))))))]
            (let [max-len (nth result 0)
                  entrance (nth result 1)
                  exit (nth result 2)]
              (recur max-len entrance exit (- tcol 1)))))))))

(declare bit-test)

;;; Apply PROC to each node reachable from CELL.
(defn for-each-hex-child [proc harr cell]
  (let [walls (cell:walls cell)
        id (cell:id cell)
        x (mcar id)
        y (mcdr id)
        nr (harr:nrows harr)
        nc (harr:ncols harr)
        maxy (* 2 (- nr 1))
        maxx (* 3 (- nc 1))]
    (when-not (bit-test walls south-west) (proc (href harr (- x 3) (- y 1))))
    (when-not (bit-test walls south)      (proc (href harr x       (- y 2))))
    (when-not (bit-test walls south-east) (proc (href harr (+ x 3) (- y 1))))

    ;; NW neighbor, if there is one (we may be in col 1, or top row/odd col)
    (when (and (> x 0) ;; Not in first column.
               (or (<= y maxy) ;; Not on top row or
                   (zero? (mod* x 6)))) ;; not in an odd column.
      (let [nw (href harr (- x 3) (+ y 1))]
        (when-not (bit-test (cell:walls nw) south-east) (proc nw))))

    ;; N neighbor, if there is one (we may be on top row).
    (when (< y maxy) ;; Not on top row
      (let [n (href harr x (+ y 2))]
        (when-not (bit-test (cell:walls n) south) (proc n))))

    ;; NE neighbor, if there is one (we may be in last col, or top row/odd col)
    (when (and (< x maxx) ;; Not in last column.
               (or (<= y maxy) ;; Not on top row or
                   (zero? (mod* x 6)))) ;; not in an odd column.
      (let [ne (href harr (+ x 3) (+ y 1))]
        (when-not (bit-test (cell:walls ne) south-west) (proc ne))))))

;;; The top-level
(defn make-maze [nrows ncols]
  (let [cells (gen-maze-array nrows ncols)
        walls (permute-vec! (make-wall-vec cells) (random-state 20))]
    (dig-maze walls (* nrows ncols))
    (let [result (pick-entrances cells)]
      (let [entrance (nth result 0)
            exit (nth result 1)]
        (let [exit-cell (href-rc cells 0 exit)
              walls (cell:walls exit-cell)]
          (reroot-maze (href-rc cells (- nrows 1) entrance))
          (mark-path exit-cell)
          (set-cell:walls exit-cell (bitwise-and walls (bitwise-not south)))
          [cells entrance exit])))))

(declare print-hexmaze)

(defn pmaze [nrows ncols]
  (let [result (make-maze nrows ncols)]
    (let [cells (nth result 0)
          entrance (nth result 1)
          exit (nth result 2)]
      (print-hexmaze cells entrance))))

;;; Print out a hex array with characters.
;;; Copyright (c) 1995 by Olin Shivers.

(def output (mcons nil nil)) ;; the list of all characters written out, in reverse order.

(defn write-ch [c]
  (set-mcar! output (cons c (mcar output))))

(declare dot-space display-hexbottom)

(defn print-hexmaze [harr entrance]
  (let [nrows (harr:nrows harr)
        ncols (harr:ncols harr)
        ncols2 (* 2 (div ncols 2))]

    ;; Print out the flat tops for the top row's odd cols.
    (loop [c 1]
      (when-not (>= c ncols)
        (write-ch \space)
        (write-ch \space)
        (write-ch \space)
        (write-ch (if (= c entrance) \space \_))
        (recur (+ c 2))))
    (write-ch \newline)

    ;; Print out the slanted tops for the top row's odd cols
    ;; and the flat tops for the top row's even cols.
    (write-ch \space)
    (loop [c 0]
      (when-not (>= c ncols2)
        (write-ch (if (= c entrance) \space \_))
        (write-ch \/)
        (write-ch (dot-space harr (- nrows 1) (+ c 1)))
        (write-ch \\)
        (recur (+ c 2))))
    (when (odd? ncols)
      (write-ch (if (= entrance (- ncols 1)) \space \_)))
    (write-ch \newline)

    (loop [r (- nrows 1)]
      (when-not (< r 0)

        ;; Do the bottoms for row r's odd cols.
        (write-ch \/)
        (loop [c 1]
          (when-not (>= c ncols2)
            ;; The dot/space for the even col just behind c.
            (write-ch (dot-space harr r (- c 1)))
            (display-hexbottom (cell:walls (href-rc harr r c)))
            (recur (+ c 2))))

        (when (odd? ncols)
          (write-ch (dot-space harr r (- ncols 1)))
          (write-ch \\))
        (write-ch \newline)

        ;; Do the bottoms for row r's even cols.
        (loop [c 0]
          (when-not (>= c ncols2)
            (display-hexbottom (cell:walls (href-rc harr r c)))
            ;; The dot/space is for the odd col just after c, on row below.
            (write-ch (dot-space harr (- r 1) (+ c 1)))
            (recur (+ c 2))))

        (cond (odd? ncols)
              (display-hexbottom (cell:walls (href-rc harr r (- ncols 1))))
              (not (zero? r)) (write-ch \\))
        (write-ch \newline)
        (recur (- r 1))))))

(defn bit-test [j bit]
  (not (zero? (bitwise-and j bit))))

;;; Return a . if harr[r,c] is marked, otherwise a space.
;;; We use the dot to mark the solution path.
(defn dot-space [harr r c]
  (if (and (>= r 0) (cell:mark (href-rc harr r c))) \. \space))

;;; Print a \_/ hex bottom.
(defn display-hexbottom [hexwalls]
  (write-ch (if (bit-test hexwalls south-west) \\ \space))
  (write-ch (if (bit-test hexwalls south) \_ \space))
  (write-ch (if (bit-test hexwalls south-east) \/ \space)))

(defn run [nrows ncols]
  (set-mcar! output ())
  (pmaze nrows ncols)
  (reverse (mcar output)))

;; 期望输出：标准输入剩余部分里的 Scheme 字符表 (#\a #\  #\newline …)
(defn- read-expected-chars []
  (let [text (loop [acc []]
               (if-let [l (read-line)] (recur (conj acc l)) (apply str (interpose "\n" acc))))
        n (count text)]
    (loop [i 0 acc []]
      (cond (>= i n) (apply list acc)
            (and (= (nth text i) \#) (< (+ i 2) n) (= (nth text (+ i 1)) \\))
            (let [c (nth text (+ i 2))
                  j (loop [j (+ i 3)]
                      (if (and (< j n) (char-alphabetic? (nth text j))) (recur (+ j 1)) j))]
              (if (> j (+ i 3))
                (let [nm (subs text (+ i 2) j)]
                  (recur j (conj acc (case nm "newline" \newline "space" \space))))
                (recur (+ i 3) (conj acc c))))
            :else (recur (+ i 1) acc)))))

(defn -main [& _]
  (let [count (read-input) input1 (read-input) input2 (read-input) output (read-expected-chars)
        s3 (str count) s2 (str input2) s1 (str input1) name "maze"]
    (run-r7rs-benchmark (str name ":" s1 ":" s2 ":" s3) count
                        (fn [] (run (hide count input1) (hide count input2)))
                        (fn [result] (= result output)))))
