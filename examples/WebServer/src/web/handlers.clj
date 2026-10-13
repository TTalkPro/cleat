(ns web.handlers
  "The application's handlers, in Clojure, and the .NET type the C# host uses:
  gen-class makes Web.Handlers a real CLR class implementing the C# interface
  WebCore.IHandlers, so C# code just does `new Web.Handlers()` -- no runtime
  setup: the first use of the class starts cleat (lazy start).

  Each method calls its -Name function through the Var, so redefining a
  function from a REPL connected to the running host (see web.dev) changes
  what the host serves at once."
  (:require [clojure.string :as str])
  (:import (WebCore Greeting Slug))
  (:gen-class
   :name Web.Handlers
   :implements [WebCore.IHandlers]
   :methods [^:static [StartNrepl [int] Object]
             ^:static [StopNrepl [] Object]]))

;; Plain Clojure functions: what you work on at the REPL.

(defn greeting-text
  [name]
  (str "hello, " (if (str/blank? name) "world" name) "!"))

(defn total
  [numbers]
  (reduce + 0 numbers))

(defonce requests (atom 0))

;; WebCore.IHandlers, implemented (gen-class prefix "-"). C# types in and out:
;; long[] arrives as a CLR array, WebCore.Greeting is constructed here.

(defn -Greet [_this name]
  (Greeting. name (greeting-text name)))

(defn -Sum [_this numbers]
  (total numbers))

(defn -Slugify [_this title]
  (Slug/Make title))

(defn -Requests [_this]
  (swap! requests inc))

;; Static methods: the host starts an nREPL server in its own process.

(defn -StartNrepl [port]
  ((requiring-resolve 'web.dev/start-nrepl!) port))

(defn -StopNrepl []
  ((requiring-resolve 'web.dev/stop-nrepl!)))
