(ns nrepl.test-matchers
  "Just enough of matcher-combinators for nREPL's tests, which use its `match?`
  and a handful of its matchers.

  cleat: added in the CLR port, since matcher-combinators isn't available.
  Matching follows matcher-combinators' defaults: maps match when every key in
  the expected map matches (others are allowed), sequences match element-wise
  and in order, sets match exactly, regexes match strings they find a match in,
  and functions are predicates.")

(defprotocol Matcher
  (matches [matcher actual] "Whether actual matches."))

(declare match?)

(defrecord Absent []
  Matcher
  (matches [_ _] false))

(def absent
  "Matches a map key that's not there."
  (->Absent))

(defrecord Embeds [expected]
  Matcher
  (matches [_ actual]
    (cond
      (set? expected) (and (coll? actual)
                           (every? (fn [e] (some #(match? e %) actual)) expected))
      (map? expected) (match? expected actual)
      :else (and (sequential? actual)
                 ;; Each expected element matches a distinct actual one.
                 (loop [expected (seq expected)
                        actual (vec actual)]
                   (if-let [[e & more] expected]
                     (if-let [i (first (keep-indexed #(when (match? e %2) %1) actual))]
                       (recur more (into (subvec actual 0 i) (subvec actual (inc i))))
                       false)
                     true))))))

(defn embeds
  "Matches a collection holding at least what expected holds, in any order."
  [expected]
  (->Embeds expected))

(defrecord Equals [expected]
  Matcher
  (matches [_ actual]
    (if (map? expected)
      (and (map? actual)
           (= (set (keys expected)) (set (keys actual)))
           (every? (fn [[k v]] (match? v (get actual k))) expected))
      (match? expected actual))))

(defn equals
  "Matches exactly: a map with these keys and no others."
  [expected]
  (->Equals expected))

(defrecord Via [f expected]
  Matcher
  (matches [_ actual]
    (match? expected (f actual))))

(defn via
  "Matches when (f actual) matches expected."
  [f expected]
  (->Via f expected))

(defrecord AllOf [matchers]
  Matcher
  (matches [_ actual]
    (every? #(match? % actual) matchers)))

(defn all-of [& matchers]
  (->AllOf matchers))

(defrecord Mismatch [expected]
  Matcher
  (matches [_ actual]
    (not (match? expected actual))))

(defn mismatch [expected]
  (->Mismatch expected))

(defn- regex? [x]
  (instance? System.Text.RegularExpressions.Regex x))

(defn match?
  "Whether actual matches expected."
  [expected actual]
  (cond
    (satisfies? Matcher expected) (matches expected actual)
    (map? expected) (and (map? actual)
                         (every? (fn [[k v]]
                                   (if (instance? Absent v)
                                     (not (contains? actual k))
                                     (and (contains? actual k)
                                          (match? v (get actual k)))))
                                 expected))
    (set? expected) (and (coll? actual)
                         (= (count expected) (count (set actual)))
                         (every? (fn [e] (some #(match? e %) actual)) expected))
    (sequential? expected) (and (sequential? actual)
                                (= (count expected) (count actual))
                                (every? true? (map match? expected actual)))
    (regex? expected) (and (string? actual) (boolean (re-find expected actual)))
    (fn? expected) (boolean (expected actual))
    :else (= expected actual)))
