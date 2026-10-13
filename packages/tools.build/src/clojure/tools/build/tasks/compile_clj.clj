;   Copyright (c) Rich Hickey. All rights reserved.
;   The use and distribution terms for this software are covered by the
;   Eclipse Public License 1.0 (http://opensource.org/licenses/eclipse-1.0.php)
;   which can be found in the file epl-v10.html at the root of this distribution.
;   By using this software in any fashion, you are agreeing to be bound by
;   the terms of this license.
;   You must not remove this notice, or any other, from this software.

;; cleat port (designs/18 §4): the output is one package assembly
;; (designs/17), compiled by a cleat process running a generated script that
;; calls the compile driver (Cleat.Scheme.Clojure.Driver/CompilePackage) inside
;; the requested bindings.

(ns clojure.tools.build.tasks.compile-clj
  (:require
    [clojure.pprint :as pprint]
    [clojure.string :as str]
    [clojure.tools.build.api :as api]
    [clojure.tools.build.util.file :as file]
    [clojure.tools.build.tasks.process :as process]
    [clojure.tools.namespace.find :as find]
    [clojure.tools.namespace.dependency :as dependency]
    [clojure.tools.namespace.parse :as parse])
  (:import
    [System.IO Directory File Path]))

(defn- package-id-of [lib]
  (when lib
    (if (namespace lib) (str (namespace lib) "." (name lib)) (name lib))))

(defn assembly-name
  "The package assembly name for the params: :assembly, else :package-id,
  else :lib as a NuGet id (my.org/my-lib -> my.org.my-lib)."
  [{:keys [assembly package-id lib]}]
  (or assembly package-id (package-id-of lib)
      (throw (ex-info "compile-clj needs :assembly, :package-id or :lib to name the package assembly" {}))))

(defn- write-compile-script!
  [script-file package nses roots dll bindings]
  (let [binding-nses (->> (keys bindings)
                       (map #(-> % symbol namespace symbol)) ;; Var->namespace
                       distinct
                       (remove #(= % 'clojure.core)))
        requires (map (fn [n] `(require '~n)) binding-nses)
        forms (mapv (fn [n] `(require '~n)) nses)
        do-compile `(with-bindings ~(or bindings {})
                      (Cleat.Scheme.Clojure.Driver/CompilePackage ~package '~forms ~(vec roots) ~dll)
                      (System.Environment/Exit 0))
        script (->> (conj (vec requires) do-compile)
                 (map #(with-out-str (pprint/pprint %)))
                 (str/join System.Environment/NewLine))]
    (spit script-file script)))

(defn- ns->path
  [ns-sym]
  (str/replace (munge (str ns-sym)) \. \/))

(defn- nses-in-bfs
  [dirs]
  (mapcat #(find/find-namespaces-in-dir % find/clj) dirs))

(defn- nses-in-topo
  [dirs]
  (let [ns-decls (mapcat #(find/find-ns-decls-in-dir % find/clj) dirs)
        ns-candidates (set (map parse/name-from-ns-decl ns-decls))
        graph (reduce
                (fn [graph decl]
                  (let [sym (parse/name-from-ns-decl decl)]
                    (reduce
                      (fn [graph dep] (dependency/depend graph sym dep))
                      graph
                      (parse/deps-from-ns-decl decl))))
                (dependency/graph)
                ns-decls)]
    (->> graph
      dependency/topo-sort
      (filter ns-candidates) ;; only keep stuff in these dirs
      (#(concat % (sort ns-candidates))) ;; but make sure everything is in there at least once
      distinct)))

(defn- basis-paths
  "Extract all path entries from basis, in load path order"
  [{:keys [classpath classpath-roots]}]
  (let [path-set (->> classpath
                   (filter #(contains? (val %) :path-key))
                   (map key)
                   set)]
    (filter path-set classpath-roots)))

(defn- package-roots
  "The roots whose files go into the assembly: the source dirs, or with
  :filter-nses the parts of them under those namespace prefixes."
  [clj-paths filter-nses]
  (if (seq filter-nses)
    (for [dir clj-paths
          prefix filter-nses
          :let [base (Path/Combine dir (ns->path prefix))]
          p (cons base (map #(str base %) [".clj" ".cljc" ".cljr"]))
          :when (or (Directory/Exists p) (File/Exists p))]
      p)
    clj-paths))

(defn compile-clj
  [{:keys [basis src-dirs ns-compile filter-nses class-dir sort bindings lib package] :as params
    :or {sort :topo}}]
  (let [working-dir (file/ensure-dir (Path/Combine (Path/GetTempPath) (str "compile-clj-" (.ToString (Guid/NewGuid) "N"))))
        compile-dir (Path/GetFullPath (file/ensure-dir (api/resolve-path class-dir)))
        clj-paths (map #(Path/GetFullPath (api/resolve-path %)) (or src-dirs (basis-paths basis)))
        nses (cond
               (seq ns-compile) ns-compile
               (= sort :topo) (nses-in-topo clj-paths)
               (= sort :bfs) (nses-in-bfs clj-paths)
               :else (throw (ex-info "Missing :ns-compile or :sort order in compile-clj task" {})))
        asm (assembly-name params)
        dll (Path/Combine compile-dir (str asm ".dll"))
        package (or package (some-> lib name) asm)
        compile-script (Path/Combine working-dir "compile.clj")
        _ (write-compile-script! compile-script package nses (package-roots clj-paths filter-nses) dll bindings)

        ;; cleat-command will run in context of *project-root* - basis, load paths, etc
        ;; should all be relative to that (or absolute)
        command (process/cleat-command
                  (merge
                    (select-keys params [:cleat-cmd :cleat-opts])
                    {:cp (vec clj-paths) :basis basis :main 'clojure.main}))
        ;; run the script rather than a -m main
        command-args (-> (:command-args command) (subvec 0 (- (count (:command-args command)) 2)) (conj compile-script))
        process-args (merge {:command-args command-args}
                       (select-keys params [:out :err :out-file :err-file]))
        _ (spit (Path/Combine working-dir "compile.args") (str/join " " command-args))
        {exit :exit, ps-out :out, ps-err :err} (process/process process-args)
        ret (cond-> nil
              ps-out (assoc :out ps-out)
              ps-err (assoc :err ps-err))]
    (if (and (zero? exit) (File/Exists dll))
      (do
        ;; only delete on success, otherwise leave the evidence!
        (file/delete working-dir)
        ret)
      (throw (ex-info (str "Clojure compilation failed, working dir preserved: " working-dir) (or ret {}))))))
