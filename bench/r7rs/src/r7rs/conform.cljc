;;; CONFORM -- Type checker, written by Jim Miller.
;;; Port of r7rs-benchmarks src/conform.scm.
;;; Representation: Scheme lists -> Clojure seqs with nil as the empty list (car = first,
;;; cdr = next); map / append / for-each -> small eager list helpers below (Scheme semantics,
;;; no laziness); dotted pairs and the two-dimensional tables (mutated with set-cdr!) ->
;;; util mutable pairs; nodes, blue edges and graphs (Scheme vectors with vector-set!) ->
;;; object arrays; symbols compared with eq? (operation names) -> keywords; the local
;;; set! variable in conforms? -> a mutable cell.
(ns r7rs.conform
  (:refer-clojure :exclude [test])
  (:require [r7rs.common :refer [read-input run-r7rs-benchmark]]
            [r7rs.util :refer [mcons mcar mcdr set-mcdr! fatal]]))

(definterface ICell (get []) (set [v]))
(deftype Cell [^:unsynchronized-mutable v]
  ICell
  (get [_] v)
  (set [_ x] (set! v x) x))
(defn- cget [c] (.get ^r7rs.conform.ICell c))
(defn- cset! [c v] (.set ^r7rs.conform.ICell c v))

;;; List helpers (Scheme list procedures over nil-terminated seqs)

(defn- memq [x l]
  (loop [l l]
    (cond (nil? l) false
          (identical? x (first l)) l
          :else (recur (next l)))))

(defn- assq [x alist]
  (loop [l alist]
    (cond (nil? l) false
          (identical? x (mcar (first l))) (first l)
          :else (recur (next l)))))

(defn- map1 [f l]
  (if (nil? l) nil (cons (f (first l)) (map1 f (next l)))))

(defn- for-each [f l]
  (loop [l l]
    (when-not (nil? l)
      (f (first l))
      (recur (next l)))))

(defn- append [l1 l2]
  (if (nil? l1) l2 (cons (first l1) (append (next l1) l2))))

(defn- length [l]
  (loop [l l n 0]
    (if (nil? l) n (recur (next l) (+ n 1)))))

;;; Functional and unstable

(defn- sort-list [obj pred]
  (letfn [(sort-loop [l]
            (if (and (some? l) (some? (next l)))
              (split-list l nil nil)
              l))
          (split-list [l one two]
            (if (some? l)
              (recur (next l) two (cons (first l) one))
              (sort-merge (sort-loop one) (sort-loop two))))
          (sort-merge [one two]
            (cond (nil? one) two
                  (pred (first two) (first one))
                  (cons (first two) (sort-merge (next two) one))
                  :else
                  (cons (first one) (sort-merge (next one) two))))]
    (sort-loop obj)))

;; SET OPERATIONS
;; (representation as lists with distinct elements)

(defn- adjoin [element set]
  (if (memq element set) set (cons element set)))

(defn- eliminate [element set]
  (cond (nil? set) set
        (identical? element (first set)) (next set)
        :else (cons (first set) (eliminate element (next set)))))

(defn- intersect [list1 list2]
  (letfn [(lp [l]
            (cond (nil? l) nil
                  (memq (first l) list2) (cons (first l) (lp (next l)))
                  :else (recur (next l))))]
    (lp list1)))

(defn- union [list1 list2]
  (if (nil? list1)
    list2
    (recur (next list1) (adjoin (first list1) list2))))

;; GRAPH NODES

(defn- make-internal-node [name green red blue] (object-array [name green red blue]))
(defn- internal-node-name [node] (aget ^objects node 0))
(defn- internal-node-green-edges [node] (aget ^objects node 1))
(defn- internal-node-red-edges [node] (aget ^objects node 2))
(defn- internal-node-blue-edges [node] (aget ^objects node 3))
(defn- set-internal-node-name! [node name] (aset ^objects node 0 name))
(defn- set-internal-node-green-edges! [node edges] (aset ^objects node 1 edges))
(defn- set-internal-node-red-edges! [node edges] (aset ^objects node 2 edges))
(defn- set-internal-node-blue-edges! [node edges] (aset ^objects node 3 edges))

(defn- make-node [nm & blue-edges] ; User's constructor
  (let [nm (if (symbol? nm) (name nm) nm)
        blue-edges (if (nil? blue-edges) :NOT-A-NODE-YET (first blue-edges))]
    (make-internal-node nm nil nil blue-edges)))

(declare none-node? any-node? blue-edges)

(defn- copy-node [node]
  (make-internal-node (internal-node-name node) nil nil (blue-edges node)))

;; Selectors

(defn- make-edge-getter [selector]
  (fn [node]
    (if (or (none-node? node) (any-node? node))
      (fatal "Can't get edges from the ANY or NONE nodes")
      (selector node))))
(def ^:private red-edges (make-edge-getter internal-node-red-edges))
(def ^:private green-edges (make-edge-getter internal-node-green-edges))
(def ^:private blue-edges (make-edge-getter internal-node-blue-edges))

;; Mutators

(defn- make-edge-setter [mutator!]
  (fn [node value]
    (cond (any-node? node) (fatal "Can't set edges from the ANY node")
          (none-node? node) :OK
          :else (mutator! node value))))
(def ^:private set-red-edges! (make-edge-setter set-internal-node-red-edges!))
(def ^:private set-green-edges! (make-edge-setter set-internal-node-green-edges!))
(def ^:private set-blue-edges! (make-edge-setter set-internal-node-blue-edges!))

;; BLUE EDGES

(defn- make-blue-edge [op arg res] (object-array [op arg res]))
(defn- blue-edge-operation [edge] (aget ^objects edge 0))
(defn- blue-edge-arg-node [edge] (aget ^objects edge 1))
(defn- blue-edge-res-node [edge] (aget ^objects edge 2))
(defn- set-blue-edge-operation! [edge value] (aset ^objects edge 0 value))
(defn- set-blue-edge-arg-node! [edge value] (aset ^objects edge 1 value))
(defn- set-blue-edge-res-node! [edge value] (aset ^objects edge 2 value))

;; Selectors
(def ^:private operation blue-edge-operation)
(def ^:private arg-node blue-edge-arg-node)
(def ^:private res-node blue-edge-res-node)

;; Mutators
(def ^:private set-arg-node! set-blue-edge-arg-node!)
(def ^:private set-res-node! set-blue-edge-res-node!)

;; Higher level operations on blue edges

(defn- lookup-op [op node]
  (loop [edges (blue-edges node)]
    (cond (nil? edges) nil
          (identical? op (operation (first edges))) (first edges)
          :else (recur (next edges)))))

(defn- has-op? [op node]
  (not (nil? (lookup-op op node))))

;; TWO DIMENSIONAL TABLES

(defn- make-empty-table [] (mcons :TABLE nil))
(defn- lookup [table x y]
  (let [one (assq x (mcdr table))]
    (if one
      (let [two (assq y (mcdr one))]
        (if two (mcdr two) false))
      false)))
(defn- insert! [table x y value]
  (let [make-singleton-table (fn [x y] (list (mcons x y)))
        one (assq x (mcdr table))]
    (if one
      (set-mcdr! one (cons (mcons y value) (mcdr one)))
      (set-mcdr! table (cons (mcons x (make-singleton-table y value))
                             (mcdr table))))))

;; GRAPHS

(defn- make-internal-graph [nodes met joined] (object-array [nodes met joined]))
(defn- internal-graph-nodes [graph] (aget ^objects graph 0))
(defn- internal-graph-already-met [graph] (aget ^objects graph 1))
(defn- internal-graph-already-joined [graph] (aget ^objects graph 2))
(defn- set-internal-graph-nodes! [graph nodes] (aset ^objects graph 0 nodes))

;; Constructor

(defn- make-graph [& nodes]
  (make-internal-graph nodes (make-empty-table) (make-empty-table)))

;; Selectors

(def ^:private graph-nodes internal-graph-nodes)
(def ^:private already-met internal-graph-already-met)
(def ^:private already-joined internal-graph-already-joined)

;; Higher level functions on graphs

(defn- add-graph-nodes! [graph nodes]
  (set-internal-graph-nodes! graph (cons nodes (graph-nodes graph))))

(defn- copy-graph [g]
  (let [copy-list (fn [l] (seq (apply list (vec l))))] ; vector->list (list->vector l)
    (make-internal-graph
     (copy-list (graph-nodes g))
     (already-met g)
     (already-joined g))))

(defn- clean-graph [g]
  (let [clean-node (fn [node]
                     (when-not (or (any-node? node) (none-node? node))
                       (set-green-edges! node nil)
                       (set-red-edges! node nil)))]
    (for-each clean-node (graph-nodes g))
    g))

(declare find-canonical-representative)

(defn- canonicalize-graph [graph classes]
  (let [fix (fn [node]
              (let [fix-set (fn [object selector mutator]
                              (mutator object
                                       (map1 (fn [node]
                                               (find-canonical-representative node classes))
                                             (selector object))))]
                (when-not (or (none-node? node) (any-node? node))
                  (fix-set node green-edges set-green-edges!)
                  (fix-set node red-edges set-red-edges!)
                  (for-each
                   (fn [blue-edge]
                     (set-arg-node! blue-edge
                                    (find-canonical-representative (arg-node blue-edge) classes))
                     (set-res-node! blue-edge
                                    (find-canonical-representative (res-node blue-edge) classes)))
                   (blue-edges node)))
                node))
        fix-table (fn [table]
                    (let [canonical? (fn [node] (identical? node (find-canonical-representative node classes)))
                          filter-and-fix (fn [predicate-fn update-fn lst]
                                           (letfn [(lp [lst]
                                                     (cond (nil? lst) nil
                                                           (predicate-fn (first lst))
                                                           (cons (update-fn (first lst)) (lp (next lst)))
                                                           :else (recur (next lst))))]
                                             (lp lst)))
                          fix-line (fn [line]
                                     (filter-and-fix
                                      (fn [entry] (canonical? (mcar entry)))
                                      (fn [entry] (mcons (mcar entry)
                                                         (find-canonical-representative (mcdr entry) classes)))
                                      line))]
                      (if (nil? table)
                        nil
                        (mcons (mcar table)
                               (filter-and-fix
                                (fn [entry] (canonical? (mcar entry)))
                                (fn [entry] (mcons (mcar entry) (fix-line (mcdr entry))))
                                (mcdr table))))))]
    (make-internal-graph
     (map1 (fn [class] (fix (first class))) classes)
     (fix-table (already-met graph))
     (fix-table (already-joined graph)))))

;; USEFUL NODES

(def ^:private none-node (make-node 'none true))
(defn- none-node? [node] (identical? node none-node))

(def ^:private any-node (make-node 'any nil))
(defn- any-node? [node] (identical? node any-node))

;; COLORED EDGE TESTS

(defn- green-edge? [from-node to-node]
  (cond (any-node? from-node) false
        (none-node? from-node) true
        (memq to-node (green-edges from-node)) true
        :else false))

(defn- red-edge? [from-node to-node]
  (cond (any-node? from-node) false
        (none-node? from-node) true
        (memq to-node (red-edges from-node)) true
        :else false))

;; SIGNATURE

;; Return signature (i.e. <arg, res>) given an operation and a node

(def ^:private sig
  (let [none-comma-any (mcons none-node any-node)]
    (fn [op node] ; Returns (arg, res)
      (let [the-edge (lookup-op op node)]
        (if (not (nil? the-edge))
          (mcons (arg-node the-edge) (res-node the-edge))
          none-comma-any)))))

;; Selectors from signature

(defn- arg [pair] (mcar pair))
(defn- res [pair] (mcdr pair))

;; CONFORMITY

(defn- conforms? [t1 t2]
  (let [nodes-with-red-edges-out (Cell. nil)
        add-red-edge! (fn [from-node to-node]
                        (set-red-edges! from-node (adjoin to-node (red-edges from-node)))
                        (cset! nodes-with-red-edges-out
                               (adjoin from-node (cget nodes-with-red-edges-out))))
        greenify-red-edges! (fn [from-node]
                              (set-green-edges! from-node
                                                (append (red-edges from-node) (green-edges from-node)))
                              (set-red-edges! from-node nil))
        delete-red-edges! (fn [from-node]
                            (set-red-edges! from-node nil))
        does-conform (fn does-conform [t1 t2]
                       (cond (or (none-node? t1) (any-node? t2)) true
                             (or (any-node? t1) (none-node? t2)) false
                             (green-edge? t1 t2) true
                             (red-edge? t1 t2) true
                             :else
                             (do (add-red-edge! t1 t2)
                                 (loop [blues (blue-edges t2)]
                                   (if (nil? blues)
                                     true
                                     (let [current-edge (first blues)
                                           phi (operation current-edge)]
                                       (and (has-op? phi t1)
                                            (does-conform
                                             (res (sig phi t1))
                                             (res (sig phi t2)))
                                            (does-conform
                                             (arg (sig phi t2))
                                             (arg (sig phi t1)))
                                            (recur (next blues)))))))))]
    (let [result (does-conform t1 t2)]
      (for-each (if result greenify-red-edges! delete-red-edges!)
                (cget nodes-with-red-edges-out))
      result)))

(defn- equivalent? [a b]
  (and (conforms? a b) (conforms? b a)))

;; EQUIVALENCE CLASSIFICATION
;; Given a list of nodes, return a list of equivalence classes

(defn- classify [nodes]
  (loop [classes nil
         nodes nodes]
    (if (nil? nodes)
      (map1 (fn [class]
              (sort-list class
                         (fn [node1 node2]
                           (< (count (internal-node-name node1))
                              (count (internal-node-name node2))))))
            classes)
      (let [this-node (first nodes)
            add-node (fn add-node [classes]
                       (cond (nil? classes) (list (list this-node))
                             (equivalent? this-node (first (first classes)))
                             (cons (cons this-node (first classes))
                                   (next classes))
                             :else (cons (first classes)
                                         (add-node (next classes)))))]
        (recur (add-node classes)
               (next nodes))))))

;; Given a node N and a classified set of nodes,
;; find the canonical member corresponding to N

(defn- find-canonical-representative [element classification]
  (loop [classes classification]
    (cond (nil? classes) (fatal "Can't classify" element)
          (memq element (first classes)) (first (first classes))
          :else (recur (next classes)))))

;; Reduce a graph by taking only one member of each equivalence
;; class and canonicalizing all outbound pointers

(defn- xreduce [graph]
  (let [classes (classify (graph-nodes graph))]
    (canonicalize-graph graph classes)))

;; MEET/JOIN
;; These update the graph when computing the node for node1*node2

(defn- blue-edge-operate [arg-fn res-fn graph op sig1 sig2]
  (make-blue-edge op
                  (arg-fn graph (arg sig1) (arg sig2))
                  (res-fn graph (res sig1) (res sig2))))

(declare join)

(defn- meet [graph node1 node2]
  (cond (identical? node1 node2) node1
        (or (any-node? node1) (any-node? node2)) any-node ; canonicalize
        (none-node? node1) node2
        (none-node? node2) node1
        :else
        (or (lookup (already-met graph) node1 node2) ; return it if found
            (cond (conforms? node1 node2) node2
                  (conforms? node2 node1) node1
                  :else
                  (let [result (make-node (str "(" (internal-node-name node1) " ^ " (internal-node-name node2) ")"))]
                    (add-graph-nodes! graph result)
                    (insert! (already-met graph) node1 node2 result)
                    (set-blue-edges! result
                                     (map1
                                      (fn [op]
                                        (blue-edge-operate join meet graph op (sig op node1) (sig op node2)))
                                      (intersect (map1 operation (blue-edges node1))
                                                 (map1 operation (blue-edges node2)))))
                    result)))))

(defn- join [graph node1 node2]
  (cond (identical? node1 node2) node1
        (any-node? node1) node2
        (any-node? node2) node1
        (or (none-node? node1) (none-node? node2)) none-node ; canonicalize
        :else
        (or (lookup (already-joined graph) node1 node2) ; return it if found
            (cond (conforms? node1 node2) node1
                  (conforms? node2 node1) node2
                  :else
                  (let [result (make-node (str "(" (internal-node-name node1) " v " (internal-node-name node2) ")"))]
                    (add-graph-nodes! graph result)
                    (insert! (already-joined graph) node1 node2 result)
                    (set-blue-edges! result
                                     (map1
                                      (fn [op]
                                        (blue-edge-operate meet join graph op (sig op node1) (sig op node2)))
                                      (union (map1 operation (blue-edges node1))
                                             (map1 operation (blue-edges node2)))))
                    result)))))

;; MAKE A LATTICE FROM A GRAPH

(defn- make-lattice [g print?]
  (let [step (fn [g]
               (let [copy (copy-graph g)
                     nodes (graph-nodes copy)]
                 (for-each (fn [first]
                             (for-each (fn [second]
                                         (meet copy first second) (join copy first second))
                                       nodes))
                           nodes)
                 copy))
        lp (fn [g count]
             (when print? (print count))
             (let [lattice (step g)]
               (when print?
                 (print " -> ")
                 (print (length (graph-nodes lattice))))
               (let [new-g (xreduce lattice)
                     new-count (length (graph-nodes new-g))]
                 (if (= new-count count)
                   (do (when print? (newline))
                       new-g)
                   (do (when print?
                         (print " -> ")
                         (print new-count)
                         (newline))
                       (recur new-g new-count))))))]
    (let [graph (apply make-graph
                       (adjoin any-node (adjoin none-node (graph-nodes (clean-graph g)))))]
      (lp graph (length (graph-nodes graph))))))

;; DEBUG and TEST

(def ^:private a (Cell. nil))
(def ^:private b (Cell. nil))
(def ^:private c (Cell. nil))
(def ^:private d (Cell. nil))

(defn- setup [a0 b0 c0 d0]
  (cset! a (make-node a0))
  (cset! b (make-node b0))
  (set-blue-edges! (cget a) (list (make-blue-edge :phi any-node (cget b))))
  (set-blue-edges! (cget b) (list (make-blue-edge :phi any-node (cget a))
                                  (make-blue-edge :theta any-node (cget b))))
  (cset! c (make-node c0))
  (cset! d (make-node d0))
  (set-blue-edges! (cget c) (list (make-blue-edge :theta any-node (cget b))))
  (set-blue-edges! (cget d) (list (make-blue-edge :phi any-node (cget c))
                                  (make-blue-edge :theta any-node (cget d))))
  '(made a b c d))

(defn- test [a0 b0 c0 d0]
  (setup a0 b0 c0 d0)
  (map1 internal-node-name
        (graph-nodes (make-lattice (make-graph (cget a) (cget b) (cget c) (cget d) any-node none-node) false))))

(defn -main [& _]
  (let [count (read-input)
        input1 (read-input)
        output (read-input)
        s (str count)
        name "conform"]
    (run-r7rs-benchmark
     (str name ":" s)
     count
     (fn [] (apply test input1))
     (fn [result] (= result output)))))
