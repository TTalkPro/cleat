(ns web.cli
  "A command line for the handlers, as a gen-class with a main: Web.Cli is a
  CLR class with a static Main(string[]) that calls -main below, so

    dotnet cleat -m web.cli sum 1 2 3          ; from source (or -M:cli sum 1 2 3)
    dotnet run --project host -- cli sum 1 2 3  ; the C# host calls Web.Cli.Main

  both run the same code; C# needs no runtime setup (lazy start).
  greet is also a function for -X: dotnet cleat -X:greet :name '\"you\"'."
  (:require [clojure.string :as str]
            [web.handlers :as h])
  (:import (WebCore Slug))
  (:gen-class
   :name Web.Cli
   :main true))

(defn- usage []
  (binding [*out* *err*]
    (println "usage: cli greet NAME | sum N... | slug TEXT...")))

(defn -main
  [& args]
  (let [[command & more] args]
    (case command
      "greet" (println (h/greeting-text (first more)))
      "sum" (println (h/total (map #(Int64/Parse ^String %) more)))
      "slug" (println (Slug/Make (str/join " " more)))
      (usage))))

(defn greet
  "For -X (clj -X style): takes a map, prints a greeting."
  [{:keys [name]}]
  (println (h/greeting-text (str name))))
