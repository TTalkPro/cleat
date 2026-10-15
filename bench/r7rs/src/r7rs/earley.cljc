;;; EARLEY -- Earley's parser, written by Marc Feeley.
;;; Port of r7rs-benchmarks src/earley.scm (see the original for the full description of
;;; configurations, starters, enders and predictors).
;;; Representation: Scheme vectors (all mutated with vector-set!) -> object arrays;
;;; Scheme lists -> Clojure seqs with nil as the empty list (car = first, cdr = next,
;;; pair? = seq?), with eager list helpers (map / reverse / append / length) below;
;;; grammar symbols -> keywords (compared with equal? as in the original).
;;; The parser's internal defines only use their parameters, so they are top-level private
;;; functions here; the parse result still carries parsed? / deriv-trees* / nb-deriv-trees*
;;; as procedure values and parse->... calls them through the vector, like the original.
;;; Named lets whose inner loop ends by tail-calling the outer loop (loop2 -> loop1) are
;;; written as an inner loop that returns (with its accumulator, if any) followed by recur
;;; in the outer loop.
(ns r7rs.earley
  (:refer-clojure :exclude [test reduce])
  (:require [r7rs.common :refer [hide read-input run-r7rs-benchmark]]
            [r7rs.util :refer [make-vector]]))

(defn- hinted [] (with-meta (gensym "v") {:tag 'objects}))
(defmacro ^:private vref [v i] (let [a (hinted)] `(let [~a ~v] (aget ~a ~i))))
;; a function (like Scheme's vector-set!), so primitive long values are boxed on the way in
(defn- vset! [v i x] (aset ^objects v i x))
(defmacro ^:private vlen [v] (let [a (hinted)] `(let [~a ~v] (alength ~a))))

;;; List helpers (Scheme list procedures over nil-terminated seqs)

(defn- pair? [x] (seq? x))

(defn- member [x l]
  (loop [l l]
    (cond (nil? l) false
          (= x (first l)) l
          :else (recur (next l)))))

(defn- reverse* [l]
  (loop [l l acc nil]
    (if (pair? l) (recur (next l) (cons (first l) acc)) acc)))

(defn- map1 [f l]
  (if (pair? l) (cons (f (first l)) (map1 f (next l))) nil))

(declare list->vector)

;; Iterative (copies l1 through a temporary array): deriv-trees* appends a list of
;; millions of trees, which overflows the JVM stack with the naive recursive append.
(defn- append [l1 l2]
  (if (pair? l1)
    (let [v (list->vector l1)]
      (loop [i (- (vlen v) 1) acc l2]
        (if (>= i 0) (recur (- i 1) (cons (vref v i) acc)) acc)))
    l2))

(defn- length [l]
  (loop [l l n 0]
    (if (pair? l) (recur (next l) (+ n 1)) n)))

(defn- list->vector [l]
  (let [v (make-vector (length l))]
    (loop [l l i 0]
      (when (pair? l)
        (vset! v i (first l))
        (recur (next l) (+ i 1))))
    v))

(defn- vector->list [v]
  (loop [i (- (vlen v) 1) acc nil]
    (if (>= i 0) (recur (- i 1) (cons (vref v i) acc)) acc)))

;;; make-parser

(defn- non-terminals [grammar] ; return vector of non-terminals in grammar
  (let [add-nt (fn [nt nts] (if (member nt nts) nts (cons nt nts)))] ; use equal? for equality tests
    (loop [defs grammar nts nil]
      (if (pair? defs)
        (let [def (first defs)
              head (first def)
              nts (loop [rules (next def)
                         nts (add-nt head nts)]
                    (if (pair? rules)
                      (let [rule (first rules)]
                        (recur (next rules)
                               (loop [l rule nts nts]
                                 (if (pair? l)
                                   (recur (next l) (add-nt (first l) nts))
                                   nts))))
                      nts))]
          (recur (next defs) nts))
        (list->vector (reverse* nts)))))) ; goal non-terminal must be at index 0

(defn- ind [nt nts] ; return index of non-terminal `nt' in `nts'
  (loop [i (- (vlen nts) 1)]
    (if (>= i 0)
      (if (= (vref nts i) nt) i (recur (- i 1)))
      false)))

(defn- nb-configurations [grammar] ; return nb of configurations in grammar
  (loop [defs grammar nb-confs 0]
    (if (pair? defs)
      (let [def (first defs)
            nb-confs (loop [rules (next def) nb-confs nb-confs]
                       (if (pair? rules)
                         (let [rule (first rules)]
                           (recur (next rules)
                                  (loop [l rule nb-confs nb-confs]
                                    (if (pair? l)
                                      (recur (next l) (+ nb-confs 1))
                                      (+ nb-confs 1)))))
                         nb-confs))]
        (recur (next defs) nb-confs))
      nb-confs)))

(defn- setup-tables [grammar nts starters enders predictors steps names]
  (let [add-conf (fn [conf nt nts class]
                   (let [i (ind nt nts)]
                     (vset! class i (cons conf (vref class i)))))
        nb-nts (vlen nts)]
    (loop [i (- nb-nts 1)]
      (when (>= i 0)
        (vset! steps i (- i nb-nts))
        (vset! names i (list (vref nts i) 0))
        (vset! enders i (list i))
        (recur (- i 1))))
    (loop [defs grammar conf (vlen nts)]
      (when (pair? defs)
        (let [def (first defs)
              head (first def)
              conf (loop [rules (next def) conf conf rule-num 1]
                     (if (pair? rules)
                       (let [rule (first rules)]
                         (vset! names conf (list head rule-num))
                         (add-conf conf head nts starters)
                         (let [conf (loop [l rule conf conf]
                                      (if (pair? l)
                                        (let [nt (first l)]
                                          (vset! steps conf (ind nt nts))
                                          (add-conf conf nt nts predictors)
                                          (recur (next l) (+ conf 1)))
                                        (do (vset! steps conf (- (ind head nts) nb-nts))
                                            (add-conf conf head nts enders)
                                            conf)))]
                           (recur (next rules) (+ conf 1) (+ rule-num 1))))
                       conf))]
          (recur (next defs) conf))))))

;;; The parser proper

(defn- comp-tok [tok nts] ; transform token to parsing format
  (loop [l1 (next tok) l2 nil]
    (if (pair? l1)
      (let [i (ind (first l1) nts)]
        (if i
          (recur (next l1) (cons i l2))
          (recur (next l1) l2)))
      (cons (first tok) (reverse* l2)))))

(defn- input->tokens [input lexer nts]
  (list->vector (map1 (fn [tok] (comp-tok tok nts)) (lexer input))))

(defn- make-states [nb-toks nb-confs]
  (let [states (make-vector (+ nb-toks 1) false)]
    (loop [i nb-toks]
      (if (>= i 0)
        (let [v (make-vector (+ nb-confs 1) false)]
          (vset! v 0 -1)
          (vset! states i v)
          (recur (- i 1)))
        states))))

(defn- conf-set-get [state conf]
  (vref state (+ conf 1)))

(defn- conf-set-get* [state state-num conf]
  (let [conf-set (conf-set-get state conf)]
    (if conf-set
      conf-set
      (let [conf-set (make-vector (+ state-num 6) false)]
        (vset! conf-set 1 -3) ; old elems tail (points to head)
        (vset! conf-set 2 -1) ; old elems head
        (vset! conf-set 3 -1) ; new elems tail (points to head)
        (vset! conf-set 4 -1) ; new elems head
        (vset! state (+ conf 1) conf-set)
        conf-set))))

(defn- conf-set-merge-new! [conf-set]
  (vset! conf-set
         (+ (vref conf-set 1) 5)
         (vref conf-set 4))
  (vset! conf-set 1 (vref conf-set 3))
  (vset! conf-set 3 -1)
  (vset! conf-set 4 -1))

(defn- conf-set-head [conf-set]
  (vref conf-set 2))

(defn- conf-set-next [conf-set i]
  (vref conf-set (+ i 5)))

(defn- conf-set-member? [state conf i]
  (let [conf-set (vref state (+ conf 1))]
    (if conf-set
      (conf-set-next conf-set i)
      false)))

(defn- conf-set-adjoin [state conf-set conf i]
  (let [tail (vref conf-set 3)] ; put new element at tail
    (vset! conf-set (+ i 5) -1)
    (vset! conf-set (+ tail 5) i)
    (vset! conf-set 3 i)
    (when (< tail 0)
      (vset! conf-set 0 (vref state 0))
      (vset! state 0 conf))))

(defn- conf-set-adjoin* [states state-num l i]
  (let [state (vref states state-num)]
    (loop [l1 l]
      (when (pair? l1)
        (let [conf (first l1)
              conf-set (conf-set-get* state state-num conf)]
          (if (not (conf-set-next conf-set i))
            (do (conf-set-adjoin state conf-set conf i)
                (recur (next l1)))
            (recur (next l1))))))))

(defn- conf-set-adjoin** [states states* state-num conf i]
  (let [state (vref states state-num)]
    (if (conf-set-member? state conf i)
      (let [state* (vref states* state-num)
            conf-set* (conf-set-get* state* state-num conf)]
        (when-not (conf-set-next conf-set* i)
          (conf-set-adjoin state* conf-set* conf i))
        true)
      false)))

(defn- conf-set-union [state conf-set conf other-set]
  (loop [i (conf-set-head other-set)]
    (when (>= i 0)
      (if (not (conf-set-next conf-set i))
        (do (conf-set-adjoin state conf-set conf i)
            (recur (conf-set-next other-set i)))
        (recur (conf-set-next other-set i))))))

(defn- predict [state state-num conf-set conf nt starters enders]
  ;; add configurations which start the non-terminal `nt' to the right of the dot
  (loop [l (vref starters nt)]
    (when (pair? l)
      (let [starter (first l)
            starter-set (conf-set-get* state state-num starter)]
        (if (not (conf-set-next starter-set state-num))
          (do (conf-set-adjoin state starter-set starter state-num)
              (recur (next l)))
          (recur (next l))))))
  ;; check for possible completion of the non-terminal `nt' to the right of the dot
  (loop [l (vref enders nt)]
    (when (pair? l)
      (let [ender (first l)]
        (if (conf-set-member? state ender state-num)
          (let [next (+ conf 1)
                next-set (conf-set-get* state state-num next)]
            (conf-set-union state next-set next conf-set)
            (recur (clojure.core/next l)))
          (recur (clojure.core/next l)))))))

(defn- reduce [states state state-num conf-set head preds]
  ;; a non-terminal is now completed so check for reductions that
  ;; are now possible at the configurations `preds'
  (loop [l preds]
    (when (pair? l)
      (let [pred (first l)]
        (loop [i head]
          (when (>= i 0)
            (let [pred-set (conf-set-get (vref states i) pred)]
              (when pred-set
                (let [next (+ pred 1)
                      next-set (conf-set-get* state state-num next)]
                  (conf-set-union state next-set next pred-set)))
              (recur (conf-set-next conf-set i)))))
        (recur (next l))))))

(defn- forw [states state-num starters enders predictors steps nts]
  (let [state (vref states state-num)
        nb-nts (vlen nts)]
    (loop []
      (let [conf (vref state 0)]
        (when (>= conf 0)
          (let [step (vref steps conf)
                conf-set (vref state (+ conf 1))
                head (vref conf-set 4)]
            (vset! state 0 (vref conf-set 0))
            (conf-set-merge-new! conf-set)
            (if (>= step 0)
              (predict state state-num conf-set conf step starters enders)
              (let [preds (vref predictors (+ step nb-nts))]
                (reduce states state state-num conf-set head preds)))
            (recur)))))))

(defn- forward [starters enders predictors steps nts toks]
  (let [nb-toks (vlen toks)
        nb-confs (vlen steps)
        states (make-states nb-toks nb-confs)
        goal-starters (vref starters 0)]
    (conf-set-adjoin* states 0 goal-starters 0) ; predict goal
    (forw states 0 starters enders predictors steps nts)
    (loop [i 0]
      (when (< i nb-toks)
        (let [tok-nts (next (vref toks i))]
          (conf-set-adjoin* states (+ i 1) tok-nts i) ; scan token
          (forw states (+ i 1) starters enders predictors steps nts)
          (recur (+ i 1)))))
    states))

(defn- produce [conf i j enders steps toks states states* nb-nts]
  (let [prev (- conf 1)]
    (when (and (>= conf nb-nts) (>= (vref steps prev) 0))
      (loop [l (vref enders (vref steps prev))]
        (when (pair? l)
          (let [ender (first l)
                ender-set (conf-set-get (vref states j) ender)]
            (when ender-set
              (loop [k (conf-set-head ender-set)]
                (when (>= k 0)
                  (and (>= k i)
                       (conf-set-adjoin** states states* k prev i)
                       (conf-set-adjoin** states states* j ender k))
                  (recur (conf-set-next ender-set k)))))
            (recur (next l))))))))

(defn- back [states states* state-num enders steps nb-nts toks]
  (let [state* (vref states* state-num)]
    (loop []
      (let [conf (vref state* 0)]
        (when (>= conf 0)
          (let [conf-set (vref state* (+ conf 1))
                head (vref conf-set 4)]
            (vset! state* 0 (vref conf-set 0))
            (conf-set-merge-new! conf-set)
            (loop [i head]
              (when (>= i 0)
                (produce conf i state-num enders steps toks states states* nb-nts)
                (recur (conf-set-next conf-set i))))
            (recur)))))))

(defn- backward [states enders steps nts toks]
  (let [nb-toks (vlen toks)
        nb-confs (vlen steps)
        nb-nts (vlen nts)
        states* (make-states nb-toks nb-confs)
        goal-enders (vref enders 0)]
    (loop [l goal-enders]
      (when (pair? l)
        (let [conf (first l)]
          (conf-set-adjoin** states states* nb-toks conf 0)
          (recur (next l)))))
    (loop [i nb-toks]
      (when (>= i 0)
        (back states states* i enders steps nb-nts toks)
        (recur (- i 1))))
    states*))

(defn- parsed? [nt i j nts enders states]
  (let [nt* (ind nt nts)]
    (if nt*
      (let [nb-nts (vlen nts)]
        (loop [l (vref enders nt*)]
          (if (pair? l)
            (let [conf (first l)]
              (if (conf-set-member? (vref states j) conf i)
                true
                (recur (next l))))
            false)))
      false)))

(defn- deriv-trees [conf i j enders steps names toks states nb-nts]
  (let [name (vref names conf)]
    (if name ; `conf' is at the start of a rule (either special or not)
      (if (< conf nb-nts)
        (list (list name (first (vref toks i))))
        (list (list name)))
      (let [prev (- conf 1)]
        (loop [l1 (vref enders (vref steps prev))
               l2 nil]
          (if (pair? l1)
            (let [ender (first l1)
                  ender-set (conf-set-get (vref states j) ender)]
              (if ender-set
                (recur (next l1)
                       (loop [k (conf-set-head ender-set) l2 l2]
                         (if (>= k 0)
                           (if (and (>= k i)
                                    (conf-set-member? (vref states k) prev i))
                             (let [prev-trees (deriv-trees prev i k enders steps names toks states nb-nts)
                                   ender-trees (deriv-trees ender k j enders steps names toks states nb-nts)]
                               (recur (conf-set-next ender-set k)
                                      (loop [l3 ender-trees l2 l2]
                                        (if (pair? l3)
                                          (let [ender-tree (list (first l3))]
                                            (recur (next l3)
                                                   (loop [l4 prev-trees l2 l2]
                                                     (if (pair? l4)
                                                       (recur (next l4)
                                                              (cons (append (first l4) ender-tree) l2))
                                                       l2))))
                                          l2))))
                             (recur (conf-set-next ender-set k) l2))
                           l2)))
                (recur (next l1) l2)))
            l2))))))

(defn- deriv-trees* [nt i j nts enders steps names toks states]
  (let [nt* (ind nt nts)]
    (if nt*
      (let [nb-nts (vlen nts)]
        (loop [l (vref enders nt*) trees nil]
          (if (pair? l)
            (let [conf (first l)]
              (if (conf-set-member? (vref states j) conf i)
                (recur (next l)
                       (append (deriv-trees conf i j enders steps names toks states nb-nts)
                               trees))
                (recur (next l) trees)))
            trees)))
      false)))

(defn- nb-deriv-trees [conf i j enders steps toks states nb-nts]
  (let [prev (- conf 1)]
    (if (or (< conf nb-nts) (< (vref steps prev) 0))
      1
      (loop [l (vref enders (vref steps prev))
             n 0]
        (if (pair? l)
          (let [ender (first l)
                ender-set (conf-set-get (vref states j) ender)]
            (if ender-set
              (recur (next l)
                     (loop [k (conf-set-head ender-set) n n]
                       (if (>= k 0)
                         (if (and (>= k i)
                                  (conf-set-member? (vref states k) prev i))
                           (let [nb-prev-trees (nb-deriv-trees prev i k enders steps toks states nb-nts)
                                 nb-ender-trees (nb-deriv-trees ender k j enders steps toks states nb-nts)]
                             (recur (conf-set-next ender-set k)
                                    (+ n (* nb-prev-trees nb-ender-trees))))
                           (recur (conf-set-next ender-set k) n))
                         n)))
              (recur (next l) n)))
          n)))))

(defn- nb-deriv-trees* [nt i j nts enders steps toks states]
  (let [nt* (ind nt nts)]
    (if nt*
      (let [nb-nts (vlen nts)]
        (loop [l (vref enders nt*) nb-trees 0]
          (if (pair? l)
            (let [conf (first l)]
              (if (conf-set-member? (vref states j) conf i)
                (recur (next l)
                       (+ (nb-deriv-trees conf i j enders steps toks states nb-nts)
                          nb-trees))
                (recur (next l) nb-trees)))
            nb-trees)))
      false)))

(defn- make-parser [grammar lexer]
  (let [nts (non-terminals grammar)              ; id map = list of non-terms
        nb-nts (vlen nts)                        ; the number of non-terms
        nb-confs (+ (nb-configurations grammar) nb-nts) ; the nb of confs
        starters (make-vector nb-nts nil)        ; starters for every non-term
        enders (make-vector nb-nts nil)          ; enders for every non-term
        predictors (make-vector nb-nts nil)      ; predictors for every non-term
        steps (make-vector nb-confs false)       ; what to do in a given conf
        names (make-vector nb-confs false)]      ; name of rules
    ;; Now, for each non-terminal, compute the starters, enders and predictors and
    ;; the names and steps tables.
    (setup-tables grammar nts starters enders predictors steps names)
    ;; Build the parser description
    (let [parser-descr (object-array [lexer nts starters enders predictors steps names])]
      (fn [input]
        (let [lexer      (vref parser-descr 0)
              nts        (vref parser-descr 1)
              starters   (vref parser-descr 2)
              enders     (vref parser-descr 3)
              predictors (vref parser-descr 4)
              steps      (vref parser-descr 5)
              names      (vref parser-descr 6)
              toks       (input->tokens input lexer nts)]
          (object-array [nts
                         starters
                         enders
                         predictors
                         steps
                         names
                         toks
                         (backward (forward starters enders predictors steps nts toks)
                                   enders steps nts toks)
                         parsed?
                         deriv-trees*
                         nb-deriv-trees*]))))))

(defn- parse->parsed? [parse nt i j]
  (let [nts     (vref parse 0)
        enders  (vref parse 2)
        states  (vref parse 7)
        parsed? (vref parse 8)]
    (parsed? nt i j nts enders states)))

(defn- parse->trees [parse nt i j]
  (let [nts          (vref parse 0)
        enders       (vref parse 2)
        steps        (vref parse 4)
        names        (vref parse 5)
        toks         (vref parse 6)
        states       (vref parse 7)
        deriv-trees* (vref parse 9)]
    (deriv-trees* nt i j nts enders steps names toks states)))

(defn- parse->nb-trees [parse nt i j]
  (let [nts             (vref parse 0)
        enders          (vref parse 2)
        steps           (vref parse 4)
        toks            (vref parse 6)
        states          (vref parse 7)
        nb-deriv-trees* (vref parse 10)]
    (nb-deriv-trees* nt i j nts enders steps toks states)))

(defn- test [input]
  (let [p (make-parser '((:s (:a) (:s :s)))
                       (fn [l] (map1 (fn [x] (list x x)) l)))]
    (let [x (p input)
          n (length input)]
      (length (parse->trees x :s 0 n)))))

(defn -main [& _]
  (let [count (read-input)
        input1 (read-input)
        output (read-input)
        s2 (str count)
        s1 (str input1)
        name "earley"]
    (run-r7rs-benchmark
     (str name ":" s2)
     count
     (fn [] (test (hide count (vector->list (make-vector input1 :a)))))
     (fn [result] (= result output)))))
