(ns sqltable.table
  "A result as a text table: a box of + - |, numbers right-aligned, NULL for
  nil, and CJK / full-width characters counted as two columns so that tables
  with Chinese text line up."
  (:require [clojure.string :as str]))

(defn- wide? [c]
  (let [c (int c)]
    (or (<= 0x1100 c 0x115F) (<= 0x2E80 c 0xA4CF) (<= 0xAC00 c 0xD7A3)
        (<= 0xF900 c 0xFAFF) (<= 0xFE30 c 0xFE4F) (<= 0xFF00 c 0xFF60) (<= 0xFFE0 c 0xFFE6))))

(defn width
  "Display width of a string."
  [^String s]
  (reduce + (map #(if (wide? %) 2 1) s)))

(defn- cell-text [v]
  (cond
    (nil? v) "NULL"
    (double? v) (let [^double d v] (.ToString d "0.############" System.Globalization.CultureInfo/InvariantCulture))
    :else (str v)))

(defn- pad [s w right?]
  (let [fill (apply str (repeat (- w (width s)) \space))]
    (if right? (str fill s) (str s fill))))

(defn render
  "The table for {:columns [...] :rows [[...]]}, followed by a row count line."
  [{:keys [columns rows]}]
  (let [texts (mapv #(mapv cell-text %) rows)
        numeric (mapv (fn [i] (and (seq rows) (every? #(let [v (nth % i)] (or (nil? v) (number? v))) rows)))
                      (range (count columns)))
        widths (mapv (fn [i] (apply max (width (nth columns i)) (map #(width (nth % i)) texts)))
                     (range (count columns)))
        line (str "+" (str/join "+" (map #(apply str (repeat (+ 2 %) \-)) widths)) "+")
        row (fn [cells right]
              (str "| " (str/join " | " (map-indexed (fn [i c] (pad c (nth widths i) (right i))) cells)) " |"))]
    (str/join "\n"
              (concat [line (row columns (constantly false)) line]
                      (map #(row % numeric) texts)
                      (when (seq texts) [line])
                      [(str (count rows) (if (= 1 (count rows)) " row" " rows"))]))))
