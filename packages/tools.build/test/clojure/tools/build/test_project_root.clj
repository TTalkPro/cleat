;   Copyright (c) Rich Hickey. All rights reserved.
;   The use and distribution terms for this software are covered by the
;   Eclipse Public License 1.0 (http://opensource.org/licenses/eclipse-1.0.php)
;   which can be found in the file epl-v10.html at the root of this distribution.
;   By using this software in any fashion, you are agreeing to be bound by
;   the terms of this license.
;   You must not remove this notice, or any other, from this software.
(ns clojure.tools.build.test-project-root
  (:require
   [clojure.test :refer :all]
   [clojure.tools.build.api :as api]
   [clojure.tools.build.test-util :as tu])
  (:import
   [System.IO Directory Path]))

;; cleat: a :local/root dep instead of a Maven one (no network in the tests)
(deftest test-with-project-root
  (let [root (Path/Combine tu/test-out "project-root")
        p-dir (Path/Combine root "p")
        lib-dir (Path/Combine root "lib")]
    (Directory/CreateDirectory (Path/Combine lib-dir "src"))
    (Directory/CreateDirectory p-dir)
    (spit (Path/Combine p-dir "cleat.edn") "{:deps {my/lib {:local/root \"../lib\"}}}")
    (api/set-project-root! root)
    (try
      (api/with-project-root (Path/Combine root "p")
        (is (contains? (-> (api/create-basis) :libs keys set) 'my/lib)))
      (finally
        (api/set-project-root! ".")))))
