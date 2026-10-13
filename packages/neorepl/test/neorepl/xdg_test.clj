(ns neorepl.xdg-test
  (:require [clojure.test :refer [are deftest is testing]]
            [neorepl.xdg :as xdg]))

;; cleat: directories are path strings.

(deftest base-dir-test
  (let [home (System.IO.Path/GetFullPath "home")
        xdg (System.IO.Path/GetFullPath "xdg")]
    (is (= xdg (xdg/base-dir xdg home ".cache")))
    (testing "the default under home when the variable is unset, empty or relative"
      (are [value] (= (System.IO.Path/Combine home ".cache") (xdg/base-dir value home ".cache"))
        nil
        ""
        "cache"))))
