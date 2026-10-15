;;; GCBENCH -- Ellis / Kovac / Boehm GC benchmark, Scheme translation by William D Clinger.
;; 记录 classNode → deftype（四个字段都可变，与原版的 setter 对应）+ definterface 读写 left / right；
;; 树的形状与分配顺序（自顶向下 Populate、自底向上 MakeTree）照原版；长寿数组 → 对象数组（装箱 double）。
(ns r7rs.gcbench
  (:require [r7rs.common :refer [hide read-input run-r7rs-benchmark]]
            [r7rs.util :refer [make-vector]]))

(definterface INode
  (left [])
  (right [])
  (setLeft [v])
  (setRight [v]))

(deftype ClassNode [^:unsynchronized-mutable l
                    ^:unsynchronized-mutable r
                    ^:unsynchronized-mutable i
                    ^:unsynchronized-mutable j]
  INode
  (left [_] l)
  (right [_] r)
  (setLeft [_ v] (set! l v) v)
  (setRight [_ v] (set! r v) v))

(defn- make-node-raw [l r i j] (ClassNode. l r i j))
(defn- node-left [n] (.left ^r7rs.gcbench.INode n))
(defn- node-right [n] (.right ^r7rs.gcbench.INode n))
(defn- node-left-set! [n v] (.setLeft ^r7rs.gcbench.INode n v))
(defn- node-right-set! [n v] (.setRight ^r7rs.gcbench.INode n v))

(defn- expt [b e]
  (loop [e e acc 1]
    (if (zero? e) acc (recur (- e 1) (*' acc b)))))

(defn- run-benchmark2 [name thunk]
  (println name)
  (thunk))

(defn- PrintDiagnostics []
  (print " Total memory available= ???????? bytes")
  (print "  Free memory= ???????? bytes")
  (newline))

(defn gcbench [kStretchTreeDepth]
  (letfn [;;  Nodes used by a tree of a given size
          (TreeSize [i]
            (- (expt 2 (+ i 1)) 1))
          ;;  Number of iterations to use for a given tree depth
          (NumIters [i]
            (quot (* 2 (TreeSize kStretchTreeDepth))
                  (TreeSize i)))]
    (let [kLongLivedTreeDepth (- kStretchTreeDepth 2)
          kArraySize (* 4 (TreeSize kLongLivedTreeDepth))
          kMinTreeDepth 4
          kMaxTreeDepth kLongLivedTreeDepth]
      ;; Elements 3 and 4 of the allocated records are useless.
      ;; They're just to take up space, so this will be comparable
      ;; to the Java original.
      (let [make-empty-node (fn [] (make-node-raw 0 0 0 0))
            make-node (fn [l r] (make-node-raw l r 0 0))]
        (letfn [;;  Build tree top down, assigning to older objects.
                (Populate [iDepth thisNode]
                  (if (<= iDepth 0)
                    false
                    (let [iDepth (- iDepth 1)]
                      (node-left-set! thisNode (make-empty-node))
                      (node-right-set! thisNode (make-empty-node))
                      (Populate iDepth (node-left thisNode))
                      (Populate iDepth (node-right thisNode)))))
                ;;  Build tree bottom-up
                (MakeTree [iDepth]
                  (if (<= iDepth 0)
                    (make-empty-node)
                    (make-node (MakeTree (- iDepth 1))
                               (MakeTree (- iDepth 1)))))
                (TimeConstruction [depth]
                  (let [iNumIters (NumIters depth)]
                    (println (str "Creating " iNumIters " trees of depth " depth))
                    (run-benchmark2
                     "GCBench: Top down construction"
                     (fn []
                       (loop [i 0]
                         (when-not (>= i iNumIters)
                           (Populate depth (make-empty-node))
                           (recur (+ i 1))))))
                    (run-benchmark2
                     "GCBench: Bottom up construction"
                     (fn []
                       (loop [i 0]
                         (when-not (>= i iNumIters)
                           (MakeTree depth)
                           (recur (+ i 1))))))))
                (run-benchmark-x []
                  (println "Garbage Collector Test")
                  (println (str " Stretching memory with a binary tree of depth " kStretchTreeDepth))
                  (PrintDiagnostics)
                  (run-benchmark2
                   "GCBench: Main"
                   (fn []
                     ;;  Stretch the memory space quickly
                     (MakeTree kStretchTreeDepth)
                     ;;  Create a long lived object
                     (println (str " Creating a long-lived binary tree of depth " kLongLivedTreeDepth))
                     (let [longLivedTree (make-empty-node)]
                       (Populate kLongLivedTreeDepth longLivedTree)
                       ;;  Create long-lived array, filling half of it
                       (println (str " Creating a long-lived array of " kArraySize " inexact reals"))
                       (let [^objects array (make-vector kArraySize 0.0)]
                         (loop [i 0]
                           (when-not (>= i (quot kArraySize 2))
                             (aset array i (num (/ 1.0 (double (+ i 1)))))
                             (recur (+ i 1))))
                         (PrintDiagnostics)
                         (loop [d kMinTreeDepth]
                           (when-not (> d kMaxTreeDepth)
                             (TimeConstruction d)
                             (recur (+ d 2))))
                         (when (or (nil? longLivedTree)
                                   (let [n (min 1000 (- (quot (alength array) 2) 1))]
                                     (not (== (aget array n) (/ 1.0 (double (+ n 1)))))))
                           (println "Failed"))
                         ;;  fake reference to LongLivedTree
                         ;;  and array
                         ;;  to keep them from being optimized away
                         ))))
                  (PrintDiagnostics))]
          (run-benchmark-x))))))

(defn -main [& _]
  (let [count (read-input) input1 (read-input) output (read-input)]
    (print "The garbage collector should touch about ")
    (print (expt 2 (- input1 13)))
    (print " megabytes of heap storage.")
    (newline)
    (print "The use of more or less memory will skew the results.")
    (newline)
    (run-r7rs-benchmark (str "gcbench:" input1 ":" count) count
                        (fn [] (gcbench (hide count input1)))
                        (fn [result] true))))
