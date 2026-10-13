;; cleat port: glob patterns as in java.nio.file.FileSystem/getPathMatcher
;; ("glob:" syntax), which upstream's copy-dir uses; matched against relative
;; paths with / separators.

(ns clojure.tools.build.util.glob
  (:require [clojure.string :as str]))

(defn glob->regex
  "Translate a glob to a regex string: * matches within a path segment, **
  across segments, ? one character in a segment, {a,b} alternatives, [...]
  a character class ([!...] negated), \\ escapes."
  [^String glob]
  (let [sb (StringBuilder. "^")
        n (count glob)]
    (loop [i 0, in-group false]
      (if (< i n)
        (let [c (.get_Chars glob i)]
          (case c
            \* (if (and (< (inc i) n) (= \* (.get_Chars glob (inc i))))
                 (do (.Append sb ".*") (recur (+ i 2) in-group))
                 (do (.Append sb "[^/]*") (recur (inc i) in-group)))
            \? (do (.Append sb "[^/]") (recur (inc i) in-group))
            \{ (do (.Append sb "(?:") (recur (inc i) true))
            \} (do (.Append sb ")") (recur (inc i) false))
            \, (do (.Append sb (if in-group "|" ",")) (recur (inc i) in-group))
            \[ (let [close (.IndexOf glob "]" (inc i))]
                 (if (neg? close)
                   (do (.Append sb "\\[") (recur (inc i) in-group))
                   (let [body (subs glob (inc i) close)
                         body (if (str/starts-with? body "!") (str "^" (subs body 1)) body)]
                     (.Append sb (str "[" (str/replace body "\\" "\\\\") "]"))
                     (recur (inc close) in-group))))
            \\ (if (< (inc i) n)
                 (do (.Append sb (System.Text.RegularExpressions.Regex/Escape (str (.get_Chars glob (inc i)))))
                     (recur (+ i 2) in-group))
                 (do (.Append sb "\\\\") (recur (inc i) in-group)))
            (do (.Append sb (System.Text.RegularExpressions.Regex/Escape (str c)))
                (recur (inc i) in-group))))
        (str (.Append sb "$"))))))

(defn matcher
  "A predicate on relative paths (either separator) matching glob."
  [glob]
  (let [re (re-pattern (glob->regex glob))]
    (fn [^String rel-path]
      (boolean (re-matches re (str/replace rel-path "\\" "/"))))))
