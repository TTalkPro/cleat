;; cleat-193：分诊里修的几处。ClojureCompilerTests.Triage193 核对输出。
(prn :bigdec-nil (try (bigdec nil) (catch NullReferenceException e :npe)))
(defn make-reify [] (reify Object))
(prn :reify-name (.Name (class (make-reify))) (.Name (class (promise))))
(defprotocol Elusive (old-method [x]))
(defprotocol Elusive (new-method [x]))
(prn :removed-protocol-method
     (try (eval '(old-method (reify Elusive (new-method [x] :n))))
          (catch Exception e [(.Name (class (.InnerException e))) (boolean (re-find #"^No method of interface: user\.Elusive found for function: old-method of protocol: Elusive" (.Message (.InnerException e))))])))
(prn :malformed-method (try (eval '(fn [] (System.String/.Length))) (catch Exception e (.Name (class (.InnerException e))))))
(prn :boxed-math
     (for [mode [:warn-on-boxed true]]
       (for [f ['(#(inc %) 2) '(#(inc ^long %) 2) '(long-array 5) '(> (first (range 3)) 0)]]
         (let [sw (System.IO.StringWriter.)]
           (binding [*unchecked-math* mode *err* sw] (eval f))
           (boolean (re-find #"^Boxed math warning, .* - call: .*clojure\.lang\.Numbers\." (str sw)))))))
