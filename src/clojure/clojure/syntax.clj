;   Copyright (c) cleat contributors. All rights reserved.
;   The use and distribution terms for this software are covered by the
;   Eclipse Public License 1.0 (https://opensource.org/license/epl-1-0/)
;   which can be found in the file epl-v10.html at the root of this distribution.
;   By using this software in any fashion, you are agreeing to be bound by
;   the terms of this license.
;   You must not remove this notice, or any other, from this software.

(ns ^{:doc "Hygienic macros for Clojure (cleat-057): defsyntax, syntax-rules, syntax-case and syntax templates
  on top of the Scheme meta layer's expander.

  A hygienic macro receives the whole call form as a *syntax object* (an opaque value carrying lexical
  context) and returns a syntax object. Identifiers introduced by a template neither capture the user's
  locals of the same name nor are captured by them; free names in a template resolve in the namespace
  that defined the macro. Ordinary defmacro macros are unchanged; the two kinds call each other freely
  (a hygienic template may expand to let, when, fn ..., and a defmacro may use a hygienic macro).

  Patterns: a symbol is a pattern variable (_ matches anything; symbols listed as literals match by
  free-identifier=?), lists and vectors match structurally, ... is the ellipsis, other atoms match by
  equality. Templates mirror patterns; (... template) escapes the ellipsis.

  Limitations: macroexpand / macroexpand-1 return plain data (introduced identifiers show by name), so
  code walkers that expand at the data level (clojure.walk/macroexpand-all, core.async's go) lose hygiene
  for hygienic macros inside them."
      :author "cleat"}
  clojure.syntax)

(defmacro syntax-case
  "(syntax-case expr (literal ...) [pattern body] [pattern fender body] ...)
  Matches the syntax object expr against each pattern in turn and evaluates the body of the first
  match (a fender, when present, must also be true). Pattern variables are bound as locals holding
  syntax objects (lists of them under ellipses); use (syntax template) to build the result and
  syntax->datum to inspect them."
  {:added "cleat-057"}
  [expr literals & clauses]
  (list* 'syntax-case* expr literals clauses))

(defmacro syntax
  "(syntax template): instantiates template, substituting the pattern variables in scope and giving
  every other identifier the context of the defining namespace (hygienic)."
  {:added "cleat-057"}
  [template]
  (list 'syntax* template))

(defmacro syntax-rules
  "(syntax-rules (literal ...) [pattern template] ...): a transformer (fn of one syntax object) that
  rewrites the first matching pattern into its template."
  {:added "cleat-057"}
  [literals & clauses]
  (doseq [c clauses]
    (when-not (and (vector? c) (= 2 (count c)))
      (throw (ArgumentException. (str "syntax-rules clause must be a vector [pattern template]: " (pr-str c))))))
  (let [stx (gensym "stx__")]
    (list 'fn* [stx]
          (list* 'syntax-case* stx literals
                 (map (fn [[pat tmpl]] [pat (list 'syntax* tmpl)]) clauses)))))

(defmacro with-syntax
  "(with-syntax [[pattern expr] ...] body ...): binds the pattern variables of each pattern to the
  (syntax object) value of expr, like let for templates."
  {:added "cleat-057"}
  [bindings & body]
  (when-not (and (vector? bindings) (every? #(and (vector? %) (= 2 (count %))) bindings))
    (throw (ArgumentException. "with-syntax expects a vector of [pattern expr] pairs")))
  (list 'syntax-case* (list* 'clojure.core/list (map second bindings)) ()
        [(apply list (map first bindings)) (list* 'do body)]))

(defmacro defsyntax
  "(defsyntax name docstring? transformer): defines a hygienic macro. transformer is a fn of one syntax
  object returning a syntax object, usually (syntax-rules ...)."
  {:added "cleat-057"}
  [name & body]
  (let [[doc body] (if (and (string? (first body)) (next body)) [(first body) (next body)] [nil body])]
    (when-not (= 1 (count body))
      (throw (ArgumentException. "defsyntax expects (defsyntax name docstring? transformer)")))
    (let [m (merge (meta name) (when doc {:doc doc}) {:hygienic true :arglists (list 'quote '([stx]))})]
      (list 'do
            (list 'def (with-meta name m) (first body))
            (list '. (list 'var name) '(setMacro))
            (list 'var name)))))

(defn syntax->datum
  "The Clojure data of a syntax object (other values are returned as is)."
  {:added "cleat-057"}
  [x]
  (Cleat.Scheme.Clojure.SyntaxOps/SyntaxToDatum x))

(defn datum->syntax
  "Wraps the Clojure data datum as a syntax object with the lexical context of the identifier context
  (or the top-level context of a Namespace). Deliberately unhygienic: the result resolves as if it had
  been written where context was."
  {:added "cleat-057"}
  [context datum]
  (Cleat.Scheme.Clojure.SyntaxOps/DatumToSyntax context datum))

(defn identifier?
  "True if x is a syntax object wrapping a symbol."
  {:added "cleat-057"}
  [x]
  (Cleat.Scheme.Clojure.SyntaxOps/Identifier x))

(defn free-identifier=?
  "True if the two identifiers would resolve to the same binding (both unbound: same name)."
  {:added "cleat-057"}
  [a b]
  (Cleat.Scheme.Clojure.SyntaxOps/FreeIdentifierEquals a b))

(defn bound-identifier=?
  "True if a binding of one identifier would capture the other (same name and marks)."
  {:added "cleat-057"}
  [a b]
  (Cleat.Scheme.Clojure.SyntaxOps/BoundIdentifierEquals a b))

(defn generate-temporaries
  "A list of fresh identifiers, one per element of coll."
  {:added "cleat-057"}
  [coll]
  (Cleat.Scheme.Clojure.SyntaxOps/GenerateTemporaries coll))
