(ns nrepl.transport-test
  (:require [nrepl.transport :as sut]
            [clojure.test :refer [deftest testing is]])
  (:import (System.IO BufferedStream MemoryStream)
           (System.Text Encoding)))

(deftest bencode-safe-write-test
  (testing "safe-write-bencode only writes if the whole message is writable"
    (let [out (MemoryStream.)]
      (is (thrown? ArgumentException
                   (#'sut/safe-write-bencode out {"obj" (Object.)})))
      (is (empty? (.ToArray out))))))

(deftest malformed-bencode-input-test
  (testing "if non-bencode input is passed, throw an informative error"
    (let [in (-> (.GetBytes Encoding/UTF8 "123456789123456789123456789")
                 MemoryStream.
                 BufferedStream.)
          out (BufferedStream. (MemoryStream.))]
      (is (thrown-with-msg? clojure.lang.ExceptionInfo
                            #"^nREPL message must be a map."
                            (sut/recv (sut/bencode in out))))))

  (testing "if top-level message is not a map, throw an informative error"
    (let [in (-> (.GetBytes Encoding/UTF8 "li1ei2ei3ee")
                 MemoryStream.
                 BufferedStream.)
          out (BufferedStream. (MemoryStream.))]
      (is (thrown-with-msg? clojure.lang.ExceptionInfo
                            #"^nREPL message must be a map."
                            (sut/recv (sut/bencode in out)))))))
