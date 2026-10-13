(ns nrepl.misc-test
  (:require [clojure.test :refer [deftest is]]
            [nrepl.misc :as misc]
            [nrepl.test-helpers :as th]))

;; cleat: :file metadata as a FileInfo or a Uri (java.io.File and java.net.URL
;; on the JVM) comes back as its string form.

(deftest sanitize-meta-test
  (is (not-empty (:file (misc/sanitize-meta {:file "clojure/core.clj"}))))

  (is (System.IO.Path/IsPathRooted ^String (:file (misc/sanitize-meta {:file "clojure/core.clj"}))))

  (is (= "/foo/bar/baz.clj"
         (:file (misc/sanitize-meta {:file "/foo/bar/baz.clj"}))))

  (is (= (th/dir-sep->sys "/foo/bar/baz.clj")
         (:file (misc/sanitize-meta {:file (System.IO.FileInfo. (th/dir-sep->sys "/foo/bar/baz.clj"))}))))

  (is (= "https://foo.bar/"
         (:file (misc/sanitize-meta {:file (System.Uri. "https://foo.bar")})))))
