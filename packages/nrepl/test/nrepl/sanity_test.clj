(ns nrepl.sanity-test
  (:require
   [clojure.test :refer :all]
   [nrepl.core :as nrepl]
   [nrepl.middleware.print :as print]
   [nrepl.test-helpers :as th]
   [nrepl.transport :as transport :refer [piped-transports]]))

;; cleat: the replying writer is a System.IO.TextWriter, so the writes are the
;; CLR's overloads (there's no write(String, int, int) there).

(deftest repl-out-writer
  (let [[local remote] (piped-transports)
        ^System.IO.TextWriter w (print/replying-PrintWriter :out {:transport remote} {})]
    (doto w
      .Flush
      (.WriteLine "println")
      (.Write "abcd")
      (.Write (char-array "ef") 0 2)
      (.Write "gh")
      (.Write (char-array "ij"))
      (.Write (char-array "   klm") 5 1)
      (.Write \space)
      .Flush)
    (binding [*out* w]
      (newline)
      (prn #{})
      (flush))

    (is (= [(str "println" System.Environment/NewLine)
            "abcdefghijm "
            (th/newline->sys "\n#{}\n")]
           (->> (nrepl/response-seq local 0)
                (map :out))))))
