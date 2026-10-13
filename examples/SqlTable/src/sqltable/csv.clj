(ns sqltable.csv
  "A small CSV reader (RFC 4180): fields separated by commas, quoted with
  double quotes (\"\" inside quotes is a quote, quotes may span lines).")

(defn parse
  "Rows (vectors of strings) of CSV text. Blank lines are skipped."
  [^String text]
  (let [n (count text)
        sb (StringBuilder.)]
    (loop [i 0, quoted false, row [], rows []]
      (if (>= i n)
        (let [row (conj row (str sb))]
          (if (= row [""]) rows (conj rows row)))
        (let [c (.get_Chars text i)]
          (cond
            quoted
            (cond
              (and (= c \") (< (inc i) n) (= \" (.get_Chars text (inc i))))
              (do (.Append sb \") (recur (+ i 2) true row rows))
              (= c \") (recur (inc i) false row rows)
              :else (do (.Append sb c) (recur (inc i) true row rows)))

            (= c \") (recur (inc i) true row rows)
            (= c \,) (let [f (str sb)] (.Clear sb) (recur (inc i) false (conj row f) rows))
            (= c \return) (recur (inc i) false row rows)
            (= c \newline) (let [row (conj row (str sb))]
                             (.Clear sb)
                             (recur (inc i) false [] (if (= row [""]) rows (conj rows row))))
            :else (do (.Append sb c) (recur (inc i) false row rows))))))))
