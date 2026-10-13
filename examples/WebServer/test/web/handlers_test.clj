(ns web.handlers-test
  (:require [clojure.test :refer [deftest is testing]]
            [web.handlers :as h])
  (:import (WebCore IHandlers)))

(deftest functions
  (is (= "hello, cleat!" (h/greeting-text "cleat")))
  (is (= "hello, world!" (h/greeting-text "")))
  (is (= 10 (h/total [1 2 3 4]))))

(deftest through-the-csharp-interface
  ;; the gen-class type, as the C# host sees it
  (let [^IHandlers handlers (Web.Handlers.)]
    (testing "a C# record built in Clojure"
      (let [g (.Greet handlers "dotnet")]
        (is (= "dotnet" (.Name g)))
        (is (= "hello, dotnet!" (.Message g)))))
    (testing "a CLR array in"
      (is (= 6 (.Sum handlers (long-array [1 2 3])))))
    (testing "a C# helper called from Clojure"
      (is (= "hello-cleat-world" (.Slugify handlers "Hello, Cleat World!"))))
    (testing "state"
      (let [n (.Requests handlers)]
        (is (= (inc n) (.Requests handlers)))))))
