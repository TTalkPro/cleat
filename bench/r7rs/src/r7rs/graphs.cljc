;;; GRAPHS -- Obtained from Andrew Wright.
;;; Port of r7rs-benchmarks src/graphs.scm.
;;; Representation: Scheme vectors (mutated with vector-set!) -> object arrays; lists ->
;;; Clojure seqs with nil as the empty list; 'less / 'equal / 'more -> keywords; booleans
;;; compared with eq? -> identical? on true / false.
;;; fold-over-perm-tree is continuation-passing: deeper / accross re-enter its named lets
;;; by proper tail calls in Scheme, and the stack never unwinds until the whole permutation
;;; tree is folded (a plain-call translation overflows the JVM stack). It uses a trampoline:
;;; deeper and accross return thunks that re-enter _-*- / _-**-, b-folder / t-folder return
;;; them in tail position (the folder protocol is unchanged), and `trampoline` runs them;
;;; the results are booleans, never functions. Self tail calls of named lets that are not
;;; re-entered from closures are loop / recur.
(ns r7rs.graphs
  (:require [r7rs.common :refer [hide read-input run-r7rs-benchmark]]
            [r7rs.util :refer [make-vector fatal]]))

(defn- hinted [] (with-meta (gensym "v") {:tag 'objects}))
(defmacro ^:private vref [v i] (let [a (hinted)] `(let [~a ~v] (aget ~a ~i))))
(defmacro ^:private vset! [v i x] (let [a (hinted)] `(let [~a ~v] (aset ~a ~i ~x))))
(defmacro ^:private vlen [v] (let [a (hinted)] `(let [~a ~v] (alength ~a))))

;;;; ==== util.ss ====

;; Fold over list elements, associating to the left.
(defn- fold [lst folder state]
  (loop [lst lst state state]
    (if (nil? lst)
      state
      (recur (next lst) (folder (first lst) state)))))

;; Given the size of a vector and a procedure which
;; sends indicies to desired vector elements, create
;; and return the vector.
(defn- proc->vector [size f]
  (if (zero? size)
    (object-array 0)
    (let [x (make-vector size (f 0))]
      (loop [i 1]
        (when (< i size)
          (vset! x i (f i))
          (recur (+ i 1))))
      x)))

(defn- vector-fold [vec folder state]
  (let [len (vlen vec)]
    (loop [i 0 state state]
      (if (= i len)
        state
        (recur (+ i 1) (folder (vref vec i) state))))))

;; Given limit, return the list 0, 1, ..., limit-1.
(defn- giota [limit]
  (loop [limit limit res nil]
    (if (zero? limit)
      res
      (let [limit (- limit 1)]
        (recur limit (cons limit res))))))

;; Fold over the integers [0, limit).
(defn- gnatural-fold [limit folder state]
  (loop [i 0 state state]
    (if (= i limit)
      state
      (recur (+ i 1) (folder i state)))))

;; Iterate over the integers [0, limit).
(defn- gnatural-for-each [limit proc!]
  (loop [i 0]
    (when-not (= i limit)
      (proc! i)
      (recur (+ i 1)))))

(defn- natural-for-all? [limit ok?]
  (loop [i 0]
    (or (= i limit)
        (and (ok? i)
             (recur (+ i 1))))))

(defn- natural-there-exists? [limit ok?]
  (loop [i 0]
    (and (not (= i limit))
         (or (ok? i)
             (recur (+ i 1))))))

(defn- there-exists? [lst ok?]
  (loop [lst lst]
    (and (not (nil? lst))
         (or (ok? (first lst))
             (recur (next lst))))))

;;;; ==== ptfold.ss ====

;; Fold over the tree of permutations of a universe (see the original for the protocol
;; of b-folder / t-folder / deeper / accross).
(defn- fold-over-perm-tree [universe b-folder b-state t-folder t-state]
  (trampoline
   (fn _-*- [universe b-state t-state accross]
     (if (nil? universe)
       (t-folder b-state t-state accross)
       ((fn _-**- [in out t-state]
          (let [first (clojure.core/first in)
                rest (next in)
                accross (if (nil? rest)
                          accross
                          (fn [new-t-state]
                            #(_-**- rest
                                    (cons first out)
                                    new-t-state)))]
            (b-folder first
                      b-state
                      t-state
                      (fn [new-b-state new-t-state]
                        #(_-*- (fold out cons rest)
                               new-b-state
                               new-t-state
                               accross))
                      accross)))
        universe nil t-state)))
   universe b-state t-state (fn [final-t-state] final-t-state)))

;;;; ==== minimal.ss ====

(declare cmp-next-vertex)

;; A directed graph is stored as a connection matrix (vector-of-vectors) where the first
;; index is the `from' vertex and the second is the `to' vertex.  Make-minimal? returns a
;; procedure which tests if a labelling of the verticies is such that the matrix is minimal.
(defn- make-minimal? [max-size]
  (let [iotas (proc->vector (+ max-size 1) giota)
        perm (make-vector max-size 0)]
    (fn [size graph folder state]
      (fold-over-perm-tree (vref iotas size)
                           (fn [perm-x x state deeper accross]
                             (case (cmp-next-vertex graph perm x perm-x)
                               :less false
                               :equal (do (vset! perm x perm-x)
                                          (deeper (+ x 1) state))
                               :more (accross state)
                               (fatal "???")))
                           0
                           (fn [leaf-depth state accross]
                             (folder perm state accross))
                           state))))

;; Given a graph, a partial permutation vector, the next input and the next
;; output, return :less, :equal or :more depending on the lexicographic
;; comparison between the permuted and un-permuted graph.
(defn- cmp-next-vertex [graph perm x perm-x]
  (let [from-x (vref graph x)
        from-perm-x (vref graph perm-x)]
    (loop [y 0]
      (if (= x y)
        :equal
        (let [x->y? (vref from-x y)
              perm-y (vref perm y)]
          (cond (identical? x->y? (vref from-perm-x perm-y))
                (let [y->x? (vref (vref graph y) x)]
                  (cond (identical? y->x? (vref (vref graph perm-y) perm-x))
                        (recur (+ y 1))
                        y->x? :less
                        :else :more))
                x->y? :less
                :else :more))))))

;;;; ==== rdg.ss ====

(declare make-reach?)

;; Fold over rooted directed graphs with bounded out-degree.
;; Size is the number of verticies (including the root).  Max-out is the
;; maximum out-degree for any vertex.  Folder is called via (folder edges state)
;; where edges is a vector of length size.  The last vertex is the root.
(defn- fold-over-rdg [size max-out folder state]
  (let [root (- size 1)
        edge? (proc->vector size (fn [from] (make-vector size false)))
        edges (make-vector size nil)
        out-degrees (make-vector size 0)
        minimal-folder (make-minimal? root)
        non-root-minimal? (let [cont (fn [perm state accross]
                                       (accross true))]
                            (fn [size]
                              (minimal-folder size edge? cont true)))
        root-minimal? (let [cont (fn [perm state accross]
                                   (case (cmp-next-vertex edge? perm root root)
                                     :less false
                                     (:equal :more) (accross true)
                                     (fatal "???")))]
                        (fn []
                          (minimal-folder root edge? cont true)))]
    ((fn _-*- [vertex state]
       (cond (not (non-root-minimal? vertex))
             state

             (= vertex root)
             (let [reach? (make-reach? root edges)
                   from-root (vref edge? root)]
               ((fn _-*- [v outs efr efrr state]
                  (cond (not (or (= v root)
                                 (= outs max-out)))
                        (do (vset! from-root v true)
                            (let [state (_-*- (+ v 1)
                                              (+ outs 1)
                                              (cons v efr)
                                              (cons (vref reach? v) efrr)
                                              state)]
                              (vset! from-root v false)
                              (recur (+ v 1) outs efr efrr state)))

                        (and (natural-for-all? root
                                               (fn [v]
                                                 (there-exists? efrr
                                                                (fn [r] (vref r v)))))
                             (root-minimal?))
                        (do (vset! edges root efr)
                            (folder (proc->vector size (fn [i] (vref edges i)))
                                    state))

                        :else state))
                0 0 nil nil state))

             :else
             (let [from-vertex (vref edge? vertex)]
               ((fn _-**- [sv outs state]
                  (if (= sv vertex)
                    (do (vset! out-degrees vertex outs)
                        (_-*- (+ vertex 1) state))
                    (let [state ;; no sv->vertex, no vertex->sv
                          (_-**- (+ sv 1) outs state)
                          from-sv (vref edge? sv)
                          sv-out (vref out-degrees sv)
                          state (if (= sv-out max-out)
                                  state
                                  (do (vset! edges sv (cons vertex (vref edges sv)))
                                      (vset! from-sv vertex true)
                                      (vset! out-degrees sv (+ sv-out 1))
                                      (let [state ;; sv->vertex, no vertex->sv
                                            (_-**- (+ sv 1) outs state)
                                            state (if (= outs max-out)
                                                    state
                                                    (do (vset! from-vertex sv true)
                                                        (vset! edges vertex (cons sv (vref edges vertex)))
                                                        (let [state ;; sv->vertex, vertex->sv
                                                              (_-**- (+ sv 1) (+ outs 1) state)]
                                                          (vset! edges vertex (next (vref edges vertex)))
                                                          (vset! from-vertex sv false)
                                                          state)))]
                                        (vset! out-degrees sv sv-out)
                                        (vset! from-sv vertex false)
                                        (vset! edges sv (next (vref edges sv)))
                                        state)))]
                      (if (= outs max-out)
                        state
                        (do (vset! edges vertex (cons sv (vref edges vertex)))
                            (vset! from-vertex sv true)
                            (let [state ;; no sv->vertex, vertex->sv
                                  (_-**- (+ sv 1) (+ outs 1) state)]
                              (vset! from-vertex sv false)
                              (vset! edges vertex (next (vref edges vertex)))
                              state))))))
                0 0 state))))
     0 state)))

;; Given a vector which maps vertex to out-going-edge list,
;; return a vector which gives reachability.
(defn- make-reach? [size vertex->out]
  (let [res (proc->vector size
                          (fn [v]
                            (let [from-v (make-vector size false)]
                              (vset! from-v v true)
                              (loop [l (vref vertex->out v)]
                                (when-not (nil? l)
                                  (vset! from-v (first l) true)
                                  (recur (next l))))
                              from-v)))]
    (gnatural-for-each size
                       (fn [m]
                         (let [from-m (vref res m)]
                           (gnatural-for-each size
                                              (fn [f]
                                                (let [from-f (vref res f)]
                                                  (when (vref from-f m)
                                                    (gnatural-for-each size
                                                                       (fn [t]
                                                                         (when (vref from-m t)
                                                                           (vset! from-f t true)))))))))))
    res))

;;;; ==== test input ====

;; Produces all directed graphs with N verticies, distinguished root,
;; and out-degree bounded by 2, upto isomorphism.
(defn- run [n]
  (fold-over-rdg n 2 cons nil))

(defn -main [& _]
  (let [count (read-input)
        input1 (read-input)
        output (read-input)
        s2 (str count)
        s1 (str input1)
        name "graphs"]
    (run-r7rs-benchmark
     (str name ":" s1 ":" s2)
     count
     (fn [] (clojure.core/count (run (hide count input1))))
     (fn [result] (= result output)))))
