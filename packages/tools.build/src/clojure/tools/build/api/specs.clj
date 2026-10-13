;   Copyright (c) Rich Hickey. All rights reserved.
;   The use and distribution terms for this software are covered by the
;   Eclipse Public License 1.0 (http://opensource.org/licenses/eclipse-1.0.php)
;   which can be found in the file epl-v10.html at the root of this distribution.
;   By using this software in any fashion, you are agreeing to be bound by
;   the terms of this license.
;   You must not remove this notice, or any other, from this software.

;; cleat port (designs/18): paths are strings; no clojure.spec, the param
;; checks are predicates (api.specs). java-command -> cleat-command, javac ->
;; dotnet-build, write-pom/jar -> pack (nupkg), install puts the nupkg in the
;; local NuGet feed, push is new, uber writes a deployable directory.

(ns clojure.tools.build.api.specs
  "Param predicates for the tasks (upstream uses clojure.spec).")

(defn path? [x] (string? x))
(defn paths? [x] (and (sequential? x) (every? string? x)))
(defn lib? [x] (symbol? x))
(defn basis? [x] (map? x))
