;   Copyright (c) Rich Hickey. All rights reserved.
;   The use and distribution terms for this software are covered by the
;   Eclipse Public License 1.0 (http://opensource.org/licenses/eclipse-1.0.php)
;   which can be found in the file epl-v10.html at the root of this distribution.
;   By using this software in any fashion, you are agreeing to be bound by
;   the terms of this license.
;   You must not remove this notice, or any other, from this software.

;; cleat port (designs/18): paths are strings, resolve-path returns a string;
;; no clojure.spec (assert-specs takes predicates). java-command ->
;; cleat-command, javac -> dotnet-build, write-pom/jar -> pack (a nupkg),
;; install puts the nupkg in the local NuGet feed, push is new, uber writes a
;; deployable directory.

(ns clojure.tools.build.api
  (:require
    [clojure.set :as set]
    [clojure.string :as str]
    [clojure.tools.build.util.file :as file]
    [clojure.tools.build.api.specs :as specs])
  (:import
    [System.IO Path]))

;; Project root dir, defaults to current directory

(def ^:dynamic *project-root*
  "Project root path, defaults to current directory.
  Use `resolve-path` to resolve relative paths in terms of the *project-root*.
  Use `set-project-root!` to override the default for all tasks."
  ".")

(defn set-project-root!
  "Set *project-root* dir (default is \".\")"
  [root]
  (alter-var-root #'*project-root* (constantly root)))

(defmacro with-project-root
  "Execute forms in a bound project path (string) other than the default (\".\")"
  [path & forms]
  `(binding [*project-root* ~path] ~@forms))

(defn resolve-path
  "If path is absolute or root-path is nil then return path,
  otherwise resolve relative to *project-root*. Returns a string."
  ^String [path]
  (let [path (file/path path)]
    (if (Path/IsPathRooted path)
      ;; absolute, ignore root
      path
      ;; relative to *project-root*
      (Path/Combine (file/path *project-root*) path))))

(defn- assert-required
  "Check that each key in required coll is a key in params and throw if
  required are missing in params, otherwise return nil."
  [task params required]
  (let [missing (set/difference (set required) (set (keys params)))]
    (when (seq missing)
      (throw (ex-info (format "Missing required params for %s: %s" task (vec (sort missing))) (or params {}))))))

(defn- assert-specs
  "Check that key in params satisfies the predicate. Throw if it exists and
  does not, otherwise return nil."
  [task params & key-preds]
  (doseq [[key pred] (partition-all 2 key-preds)]
    (let [val (get params key)]
      (when (and val (not (pred val)))
        (throw (ex-info (format "Invalid param %s in call to %s: got %s" key task (pr-str val)) {}))))))

;; File tasks

(defn delete
  "Delete file or directory recursively, if it exists. Returns nil.

  Options:
    :path - required, path to file or directory"
  [{:keys [path] :as params}]
  (assert-required "delete" params [:path])
  (assert-specs "delete" params :path specs/path?)
  (let [root-file (resolve-path path)]
    (when (file/exists? root-file)
      (file/delete root-file))))

(defn copy-file
  "Copy one file from source to target, creating target dirs if needed.
  Returns nil.

  Options:
    :src - required, source path
    :target - required, target path"
  [{:keys [src target] :as params}]
  (assert-required "copy-file" params [:src :target])
  (assert-specs "copy-file" params
    :src specs/path?
    :target specs/path?)
  (file/copy-file (resolve-path src) (resolve-path target)))

(defn write-file
  "Writes a file at path, will create parent dirs if needed. Returns nil.
  File contents may be specified either with :content (for data, that
  will be pr-str'ed) or with :string for the string to write. If
  neither is specified, an empty file is created (like touch).

  Options:
    :path - required, file path
    :content - val to write, will pr-str
    :string - string to write
    :opts - coll of writer opts like :append and :encoding (per spit)"
  [{:keys [path content string opts] :as params}]
  (assert-required "write-file" params [:path])
  (assert-specs "write-file" params :path specs/path?)
  (let [f (resolve-path path)]
    (cond
      content (apply file/ensure-file f (pr-str content) opts)
      string (apply file/ensure-file f string opts)
      :else (file/ensure-file f))))

(defn copy-dir
  "Copy the contents of the src-dirs to the target-dir, optionally do text replacement.
  Returns nil.

  Globs are wildcard patterns for specifying sets of files in a directory
  tree, in the glob syntax of java.nio.file.FileSystem (* within a path
  segment, ** across segments, ?, {a,b}, [...]).

  Options:
    :target-dir - required, dir to write files, will be created if it doesn't exist
    :src-dirs   - required, coll of dirs to copy from
    :include    - glob of files to include, default = \"**\"
    :ignores    - collection of ignore regex patterns (applied only to file names),
                  see clojure.tools.build.tasks.copy/default-ignores for defaults
    :replace    - map of source to replacement string in files
    :non-replaced-exts - coll of extensions to skip when replacing (still copied)
                  default = [\"jpg\" \"jpeg\" \"png\" \"gif\" \"bmp\"]"
  [params]
  (assert-required "copy" params [:target-dir :src-dirs])
  (assert-specs "copy" params
    :target-dir specs/path?
    :src-dirs specs/paths?)
  ((requiring-resolve 'clojure.tools.build.tasks.copy/copy) params))

;; Basis tasks

(defn create-basis
  "Create a basis from the project's cleat.edn and a set of aliases
  (designs/18 §3). NuGet deps ({:nuget/version \"1.2.3\"}) are resolved by
  `dotnet restore` (sources and credentials per nuget.config, so private feeds
  such as BaGetter work; the local feed is an extra source), :local/root deps
  are other cleat projects.

  Options (note, paths resolved via *project-root*):
    :project - cleat.edn path, or a cleat.edn map, default = \"cleat.edn\"
               (a missing file is an empty project)
    :extra   - map merged over the project (e.g. {:deps {...}})
    :aliases - coll of aliases to apply (:extra-paths, :extra-deps, :replace-deps,
               :replace-paths; other keys merge into :argmap)
    :local-feed - local NuGet feed dir, default $CLEAT_LOCAL_FEED or ~/.cleat/local-feed

  Returns a runtime basis, which is the merged cleat.edn map plus these keys:
   :argmap - the merged alias args
   :libs - lib map: lib -> {:nuget/version v, :paths [...], :dependents [...]}
           or {:local/root dir, :paths [...]}
   :classpath - map of load path entry -> {:path-key :paths} or {:lib-name lib}
   :classpath-roots - vector of load path entries in order (cleat -I)"
  ([]
   ((requiring-resolve 'clojure.tools.build.tasks.create-basis/create-basis)))
  ([params]
   ((requiring-resolve 'clojure.tools.build.tasks.create-basis/create-basis) params)))

;; Process tasks

(defn cleat-command
  "Create cleat command line args (java-command upstream). The load path will
  be the combination of :cp followed by the load path from the basis, both are
  optional; each entry becomes a -I.

  Note that 'cleat-command' will NOT resolve any relative paths from basis
  or cp in terms of *project-root*, you will get a load path with the same
  relative paths. 'process' (if run with this output), will run in the
  context of the *project-root* directory.

  Options:
    :cleat-cmd - coll of strings, the cleat command, default = $CLEAT_CMD (split on
                 whitespace) or the cleat this process runs on
    :cp - coll of string load path entries, used first (if provided)
    :basis - runtime basis used for the load path and :cleat-opts from aliases, used last (if provided)
    :cleat-opts - coll of string cleat options (before -m)
    :main - required, main namespace symbol (cleat -m)
    :main-args - coll of main args

  Returns map suitable for passing to 'process' with keys:
    :command-args - coll of command arg strings"
  [params]
  (assert-required "cleat-command" params [:main])
  ((requiring-resolve 'clojure.tools.build.tasks.process/cleat-command) params))

(defn process
  "Exec the command made from command-args, redirect out and err as directed,
  and return {:exit exit-code, :out captured-out, :err captured-err}.

  Options:
    :command-args - required, coll of string args
    :dir - directory to run the command from, default *project-root*
    :out - one of :inherit :capture :write :append :ignore
    :err - one of :inherit :capture :write :append :ignore
    :out-file - file path to write if :out is :write or :append
    :err-file - file path to write if :err is :write or :append
    :env - map of environment variables to set

  The :out and :err input flags take one of the following options:
    :inherit - inherit the stream and write the subprocess io to this process's stream (default)
    :capture - capture the stream to a string and return it
    :write - write to :out-file or :err-file
    :append - append to :out-file or :err-file
    :ignore - ignore the stream"
  [params]
  (assert-required "process" params [:command-args])
  (assert-specs "process" params
    :dir specs/path?
    :out-file specs/path?
    :err-file specs/path?)
  ((requiring-resolve 'clojure.tools.build.tasks.process/process) params))

;; Git tasks

(defn git-process
  "Run git process in the specified dir using git-command with git-args (which should not
  start with \"git\"). git-args may either be a string (split on whitespace) or a vector
  of strings. By default, stdout is captured, trimmed, and returned.

  Options:
    :dir - dir to invoke this command from, default = current directory
    :git-command - git command to use, default = \"git\"
    :git-args - required, coll of git-arg strings forming a command line OR
                a string (do not use if args may have embedded spaces)
    :capture - :out (default) or :err, else nothing

  Examples:
    (api/git-process {:git-args \"rev-list HEAD --count\"})
    (api/git-process {:git-args \"branch --show-current\"})
    (api/git-process {:git-args \"rev-parse --short HEAD\"})
    (api/git-process {:git-args \"push\", :capture nil})"
  [params]
  (assert-required "git-command" params [:git-args])
  (assert-specs "git-process" params
    :dir specs/path?
    :git-command #(or (nil? %) (string? %))
    :git-args #(or (string? %) (and (sequential? %) (every? string? %)))
    :capture #(or (nil? %) (keyword? %)))
  (let [{:keys [dir git-command git-args capture] :or {git-command "git", capture :out}} params
        git-args (vec
                   (concat [git-command]
                     (if (string? git-args)
                       (str/split git-args #"\s")
                       git-args)))
        proc-params (cond-> {:command-args git-args}
                      capture (assoc capture :capture)
                      dir (assoc :dir (resolve-path dir)))
        output (process proc-params)]
    (when capture
      (some-> output capture str/trim))))

(defn git-count-revs
  "Shells out to git and returns count of commits on this branch:
    git rev-list HEAD --count

  Options:
    :dir - dir to invoke this command from, default = current directory
    :git-command - git command to use, default = \"git\"
    :path - path to count commits for relative to dir"
  [{:keys [dir git-command path] :or {git-command "git"} :as params}]
  (assert-specs "git-count-revs" params
    :dir specs/path?
    :git-command #(or (nil? %) (string? %))
    :path specs/path?)
  (git-process
    (cond-> {:git-args (cond-> ["rev-list" "HEAD" "--count"]
                         path (conj "--" path))}
      dir (assoc :dir dir)
      git-command (assoc :git-command git-command))))

;; Compile tasks

(defn compile-clj
  "Compile Clojure source into a package assembly (designs/17) in :class-dir:
  <class-dir>/<assembly>.dll, one entry per namespace file, loadable by cleat
  (-I class-dir) and referenceable from C# / F#.

  Clojure source files are found in :basis :paths by default, or override with :src-dirs.

  Namespaces and order of compilation are one of:
    * :ns-compile - compile these namespaces, in this order
    * :sort - find all namespaces in source files and use either :topo (default)
              or :bfs to order them for compilation

  Options:
    :basis - required, basis to use when compiling
    :class-dir - required, dir to write the assembly, will be created if needed
    :assembly - assembly name, default = :package-id, else :lib (as a NuGet id), else \"Main\"
    :package - package name (the CleatPackage attribute), default = :lib's name
    :lib - project lib symbol, used for the defaults above
    :package-id - NuGet package id, used for the default assembly name
    :src-dirs - coll of Clojure source dirs, used to find all Clojure nses to compile
                (only files under them go into the assembly)
    :ns-compile - coll of specific namespace symbols to compile
    :sort - :topo (default) or :bfs for breadth-first search
    :bindings - map of Var to value to be set during compilation, for example:
      {#'clojure.core/*assert* false
       #'clojure.core/*warn-on-reflection* true}
    :filter-nses - coll of symbols representing a namespace prefix to include

  Additional options flow to the cleat process doing the compile:
    :cleat-cmd - coll of strings, the cleat command (see cleat-command)
    :cleat-opts - coll of string cleat options
    :out - one of :inherit :capture :write :append :ignore
    :err - one of :inherit :capture :write :append :ignore
    :out-file - file path to write if :out is :write or :append
    :err-file - file path to write if :err is :write or :append

  Returns nil, or if needed a map with keys:
    :out captured-out
    :err captured-err"
  [params]
  (assert-required "compile-clj" params [:basis :class-dir])
  (assert-specs "compile-clj" params
    :class-dir specs/path?
    :src-dirs specs/paths?
    :bindings map?
    :basis specs/basis?)
  ((requiring-resolve 'clojure.tools.build.tasks.compile-clj/compile-clj) params))

(defn dotnet-build
  "Build a C# / F# project (or solution) with `dotnet build` (javac upstream).
  The output goes to :output-dir; put it on the load path (:cp / -I) to call
  it from Clojure, or into :class-dir to ship it with the package.
  Returns {:output-dir path}.

  Options:
    :project - required, path to a .csproj, .fsproj or .sln (or its dir)
    :output-dir - dir to write the build output, default = <project dir>/bin/<configuration>
    :configuration - default = \"Release\"
    :properties - map of MSBuild property name to value (-p:name=value)
    :dotnet-opts - coll of extra string args for dotnet build
    :out, :err, :out-file, :err-file - as for process"
  [params]
  (assert-required "dotnet-build" params [:project])
  (assert-specs "dotnet-build" params
    :project specs/path?
    :output-dir specs/path?
    :properties map?)
  ((requiring-resolve 'clojure.tools.build.tasks.dotnet-build/dotnet-build) params))

;; Package / zip tasks

(defn pack
  "Create a NuGet package (nupkg) (write-pom + jar upstream): :class-dir's
  assemblies (and other files) under lib/net10.0/, a nuspec with the
  dependencies: Cleat.Runtime and the basis's NuGet deps. Returns nil.

  Options:
    :basis - required, used for the NuGet dependencies (:local/root deps are an error)
    :lib - required, project lib symbol (the default package id: my.org/my-lib -> my.org.my-lib)
    :version - required, package version (NuGet / SemVer)
    :class-dir - required, dir whose files go into lib/net10.0/
    :nupkg-file - required, nupkg to write
    :package-id - NuGet id, default from :lib
    :description - default = the package id
    :authors - string or coll of strings, default = the package id
    :license - SPDX license expression, e.g. \"EPL-1.0\"
    :project-url, :repository-url, :tags (coll), :readme (path, packed as README.md)
    :src-dirs - coll of source dirs packed under src/ (for tooling; not loaded)
    :runtime-version - Cleat.Runtime version range, default = [<running version>, )
    :target-framework - default = \"net10.0\""
  [params]
  (assert-required "pack" params [:basis :lib :version :class-dir :nupkg-file])
  (assert-specs "pack" params
    :lib specs/lib?
    :version string?
    :class-dir specs/path?
    :nupkg-file specs/path?
    :src-dirs specs/paths?)
  ((requiring-resolve 'clojure.tools.build.tasks.pack/pack) params))

(defn uber
  "Create a self-contained deployable directory (an uberjar upstream): the
  cleat host and runtime (assemblies, both images, driver libraries, bundled
  libs) at its root, and the project contents (:class-dir) and all dependency
  package assemblies from the basis under lib/. With :main, writes launchers
  run / run.cmd (dotnet cleat.dll -I lib -m main args...).

  Use :exclude to exclude specific paths from the deps. Use conflict-handlers
  to handle conflicts that may occur if two sources include a file at the same
  path (relative to lib/). See below for more detail.

  Returns nil.

  Options:
    :uber-dir - required, directory to create (emptied first)
    :class-dir - required, local class dir to include
    :basis - used to pull dep package dirs
    :main - main namespace symbol
    :exclude - coll of string patterns (regex) to exclude from deps
    :conflict-handlers - map of string pattern (regex) to built-in handlers,
                         symbols to eval, or function instances

  When combining dirs, multiple sources may contain a file at the same path.
  The conflict handlers are a map of string regex pattern to:
    a keyword (to use a built-in handler) or
    a symbol (to resolve and invoke) or
    a function instance
  The special key `:default` specifies the default behavior if not matched.

  Conflict handler signature (fn [params]) => effect-map:
    params:
      :path     - String, path in lib/, matched by regex
      :in       - Stream of the incoming file (see stream->string if needed)
      :existing - String, path of the existing file
      :lib      - symbol, lib source for incoming conflict
      :state    - map, available for retaining state during the uber process

  Handler should return effect-map with optional keys:
    :state      - updated state map
    :write      - map of string path to map of :string (string) or
                  :stream (Stream) to write and optional :append
                  flag. Omit if no files to write.

  Available built-in conflict handlers:
    :ignore - don't do anything (default)
    :overwrite - overwrite (replaces prior file)
    :append - append the file with a blank line separator
    :append-dedupe - append the file but dedupe appended sections
    :data-readers - merge data_readers.clj
    :warn - print a warning
    :error - throw an error

  Default conflict handlers map:
    {\"^data_readers.clj[cr]?$\" :data-readers
     \"(?i)^(META-INF/)?(COPYRIGHT|NOTICE|LICENSE)(\\\\.(txt|md))?$\" :append-dedupe
     :default :ignore}"
  [params]
  (assert-required "uber" params [:class-dir :uber-dir])
  (assert-specs "uber" params
    :class-dir specs/path?
    :uber-dir specs/path?)
  ((requiring-resolve 'clojure.tools.build.tasks.uber/uber) params))

(defn zip
  "Create zip file containing contents of src dirs. Returns nil.

  Options:
    :src-dirs - required, coll of source directories to include in zip
    :zip-file - required, zip file to create"
  [params]
  (assert-required "zip" params [:src-dirs :zip-file])
  (assert-specs "zip" params
    :src-dirs specs/paths?
    :zip-file specs/path?)
  ((requiring-resolve 'clojure.tools.build.tasks.zip/zip) params))

(defn unzip
  "Unzip zip file to target-dir. Returns nil.

  Options:
    :zip-file - required, zip file to unzip
    :target-dir - required, directory to unzip in"
  [params]
  (assert-required "unzip" params [:zip-file :target-dir])
  (assert-specs "unzip" params
    :zip-file specs/path?
    :target-dir specs/path?)
  ((requiring-resolve 'clojure.tools.build.tasks.zip/unzip) params))

;; NuGet tasks

(defn install
  "Install the nupkg into the local NuGet feed (the local Maven repo upstream),
  where create-basis finds it, and drop that id/version from the NuGet global
  packages folder so the next restore takes the new package. Returns nil.

  Options:
    :nupkg-file - required, path to the nupkg
    :local-feed - feed dir, default $CLEAT_LOCAL_FEED or ~/.cleat/local-feed"
  [params]
  (assert-required "install" params [:nupkg-file])
  (assert-specs "install" params
    :nupkg-file specs/path?
    :local-feed specs/path?)
  ((requiring-resolve 'clojure.tools.build.tasks.install/install) params))

(defn push
  "Push the nupkg to a NuGet server (nuget.org, BaGetter, ...) with
  `dotnet nuget push`. Returns nil.

  Options:
    :nupkg-file - required, path to the nupkg
    :source - source name from nuget.config or URL (BaGetter: http://host/v3/index.json),
              default = nuget.org
    :api-key - default = $NUGET_API_KEY (none when unset)
    :skip-duplicate - true to ignore a version that is already there
    :out, :err, :out-file, :err-file - as for process"
  [params]
  (assert-required "push" params [:nupkg-file])
  (assert-specs "push" params
    :nupkg-file specs/path?
    :source string?)
  ((requiring-resolve 'clojure.tools.build.tasks.push/push) params))
