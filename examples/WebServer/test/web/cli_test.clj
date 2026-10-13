(ns web.cli-test
  (:require [clojure.test :refer [deftest is testing]]
            [web.cli :as cli]))

(deftest main
  (testing "-main"
    (is (= "hello, bob!\n" (with-out-str (cli/-main "greet" "bob"))))
    (is (= "6\n" (with-out-str (cli/-main "sum" "1" "2" "3"))))
    (is (= "hello-cleat-world\n" (with-out-str (cli/-main "slug" "Hello," "Cleat" "World")))))
  (testing "the gen-class type's static Main, as C# calls it"
    (is (= "10\n" (with-out-str (Web.Cli/Main (into-array String ["sum" "4" "6"])))))))

(deftest exec-fn
  (is (= "hello, x!\n" (with-out-str (cli/greet {:name "x"})))))
