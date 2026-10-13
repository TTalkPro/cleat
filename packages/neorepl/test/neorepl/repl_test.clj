(ns neorepl.repl-test
  "The REPL with its line editor, on a fake terminal: tests type keys and look
  at what shows up."
  (:require [clojure.string :as str]
            [clojure.test :refer [deftest is testing use-fixtures]]
            [neorepl.main :as main]
            [neorepl.nrepl :as nrepl]
            [neorepl.test-server :as test-server :refer [*port* call-with-temp-dir lf with-client]]
            [nrepl.util.streams :as streams])
  (:import (System.IO StringReader StringWriter TextWriter)))

;; cleat: ported to the CLR. The fake terminal reads the keys typed from
;; nrepl.util.streams' queue polling reader and writes to a StringWriter; the
;; editor is neorepl's own (JLine on the JVM), so the screen is what it draws,
;; which these regexes, looking at the end of the screen, see the same way.

(use-fixtures :once (test-server/fixture))

(defn- strip-ansi [s]
  (str/replace s #"\u001b\[[0-9;?]*[A-Za-z]|\u001b[=>]|\r" ""))

(defn- call-with-repl
  "Starts a REPL on a fake terminal, with the editor options in opts, and
  calls (f type screen): type types a string, screen waits for a regex to
  show up in the output, ANSI codes stripped, and returns it. Then ends the
  REPL with Ctrl-D and returns its raw output and the history file's
  contents.

  Eldoc is off unless opts say :eldoc? true, since what it shows after the
  cursor gets in the way of reading the screen."
  [opts f]
  (call-with-temp-dir
   (fn [dir]
     (let [{in :reader add-input :add-input add-eof :add-eof} (streams/queue-polling-reader (fn []))]
       (let [out (StringWriter.)
             ;; the REPL writes through the synchronized wrapper (its lock is the wrapper
             ;; itself); reading the screen takes the same lock, or StringBuilder.ToString
             ;; races the appends
             sync-out (TextWriter/Synchronized out)
             text #(locking sync-out (str out))
             repl (future (main/run ["repl" "-p" (str *port*)]
                                    {:dir dir
                                     :sessions-file (test-server/file dir "sessions.edn")
                                     :history-dir (test-server/file dir "history")
                                     :terminal (merge {:input in :output sync-out :width 80 :eldoc? false}
                                                      opts)}))
             type (fn [s] (add-input s))
             screen (fn [re]
                      (loop [tries 1000]
                        (let [s (strip-ansi (text))]
                          (cond
                            (re-find re s) s
                            (zero? tries) (throw (ex-info (str "Never saw " re) {:screen s}))
                            :else (do (System.Threading.Thread/Sleep 5) (recur (dec tries)))))))]
         (try
           (screen #"user=> $")
           (f type screen)
           (type "\u0004")
           (is (= 0 (deref repl 10000 :timeout)))
           {:out (text)
            :history (some-> (first (try (System.IO.Directory/GetFiles (test-server/file dir "history"))
                                         (catch Exception _ nil)))
                             slurp)}
           (finally
             (add-eof)
             (future-cancel repl))))))))

(deftest repl-test
  (testing "evaluating"
    (let [{:keys [out history]}
          (call-with-repl {}
            (fn [type screen]
              (type "(+ 40 2)\r")
              (is (screen #"(?m)^42\nuser=> $"))))]
      (testing "with a banner"
        (is (str/includes? (strip-ansi out) (str "neorepl connected to 127.0.0.1:" *port*)))
        ;; cleat: a cleat server's versions (cleat-068)
        (is (str/includes? (strip-ansi out) (str "(Cleat " (cleat-version) ", Clojure " (clojure-version) ")"))))
      (testing "with values highlighted"
        (is (str/includes? out "\u001b[35m42")))
      (testing "and kept in the history"
        (is (str/includes? history "(+ 40 2)")))))
  (testing "forms span lines until they're complete"
    (call-with-repl {}
      (fn [type screen]
        (type "(+ 1\r")
        (is (screen #"#_=> $"))
        (type "2)\r")
        (is (screen #"(?m)^3\nuser=> $")))))
  (testing "the prompt follows the namespace"
    (call-with-repl {}
      (fn [type screen]
        (type "(do (in-ns 'term.scratch) nil)\r")
        (is (screen #"term\.scratch=> $")))))
  (testing "no colors"
    (let [{:keys [out]} (call-with-repl {:color? false}
                          (fn [type screen]
                            (type "42\r")
                            (screen #"(?m)^42\nuser=> $")))]
      (is (not (str/includes? out "\u001b[35m"))))))

(deftest output-test
  (testing "stdout and stderr get lines of their own"
    (call-with-repl {}
      (fn [type screen]
        (type "(do (print :out) (flush) (binding [*out* *err*] (println :err)) :value)\r")
        (is (screen #":out\n:err\n:value\nuser=> $")))))
  (testing "output after the evaluation is done goes above the prompt, line by line"
    (call-with-repl {}
      (fn [type screen]
        (type "(do (future (System.Threading.Thread/Sleep 200) (print \"a\") (flush) (print \"b\") (flush) (println \"c\")) nil)\r")
        ;; The prompt gets redrawn below, which screen doesn't see.
        (is (screen #"abc\nuser=> $")))))
  (testing "messages from the server"
    (with-client [client {:port *port*}]
      (call-with-repl {}
        (fn [type screen]
          ;; Closing every session but this client's takes the REPL's.
          (let [mine (nrepl/clone-session client)
                others (remove #{mine} (some :sessions (nrepl/request client {:op "ls-sessions"})))]
            (doseq [session others]
              (nrepl/close-session client session)))
          (type "1\r")
          (is (screen #"The server reported unknown-session\nuser=> $")))))))

(deftest stdin-test
  (testing "reads from *in* get a line of their own"
    (call-with-repl {}
      (fn [type screen]
        (type "(do (print \"Name: \") (flush) (read-line))\r")
        (type "bob\r")
        (is (screen #"Name: bob\n\"bob\"\nuser=> $")))))
  (testing "Ctrl-C while code is reading interrupts it"
    (call-with-repl {}
      (fn [type screen]
        (type "(do (read-line) :read)\r")
        (screen #":read\)\n$")
        ;; Time for it to go out and ask for the line.
        (System.Threading.Thread/Sleep 200)
        (type "\u0003")
        (is (screen #"The evaluation was interrupted\nuser=> $"))
        ;; Time for the value to show up, if it was going to.
        (System.Threading.Thread/Sleep 200)
        (is (not (re-find #"(?m)(^|=> ):read$" (screen #"user=> $")))))))
  (testing "even when it comes before the code asks for a line"
    (call-with-repl {}
      (fn [type screen]
        (type "(late-read)\r")
        (screen #"\(late-read\)\n$")
        ;; Time for it to go out, but not for the server to ask.
        (System.Threading.Thread/Sleep 100)
        (type "\u0003")
        (is (screen #"The evaluation was interrupted\nuser=> $")))))
  (testing "and still gets its line when the interrupt fails after the server asked"
    (call-with-repl {}
      (fn [type screen]
        (type "(stubborn-read)\r")
        (screen #"\(stubborn-read\)\n$")
        (System.Threading.Thread/Sleep 100)
        (type "\u0003")
        (screen #"The server can't interrupt the evaluation, Ctrl-C again stops waiting for it\n")
        (type "bob\r")
        (is (screen #"\"bob\"\nuser=> $"))))))

(deftest interrupt-test
  (testing "Ctrl-C at the prompt clears the line"
    (call-with-repl {}
      (fn [type screen]
        (type "(+ 1")
        (screen #"\(\+ 1$")
        (type "\u0003")
        (type "(+ 2 2)\r")
        (is (screen #"(?m)^4\nuser=> $")))))
  (testing "a Ctrl-C that arrives twice clears the line once"
    ;; As happens on babashka, where it comes as a signal and a character.
    (call-with-repl {}
      (fn [type screen]
        (type "(+ 1")
        (screen #"\(\+ 1$")
        (type "\u0003\u0003")
        (System.Threading.Thread/Sleep 100)
        (type "(+ 2 2)\r")
        (is (= 3 (count (re-seq #"(?m)^user=> " (screen #"(?m)^4\nuser=> $"))))))))
  (testing "Ctrl-C interrupts an evaluation"
    (call-with-repl {}
      (fn [type screen]
        (type "(do (println :sleeping) (System.Threading.Thread/Sleep 20000))\r")
        (screen #":sleeping\n$")
        (type "\u0003")
        (is (screen #"The evaluation was interrupted\nuser=> $"))
        (type "(+ 3 3)\r")
        (is (screen #"(?m)^6\nuser=> $")))))
  (testing "Ctrl-C again stops waiting for an evaluation the server can't interrupt"
    (call-with-repl {}
      (fn [type screen]
        (type "(stuck)\r")
        (screen #"\(stuck\)\n$")
        (System.Threading.Thread/Sleep 100)
        (type "\u0003")
        (is (screen #"The server can't interrupt the evaluation, Ctrl-C again stops waiting for it\n$"))
        (type "\u0003")
        (is (screen #"Stopped waiting for the evaluation, which may still be running\nuser=> $"))
        (type "(+ 3 3)\r")
        (is (screen #"(?m)^6\nuser=> $")))))
  (testing "and shows what it says after all, when it's only slow"
    (call-with-repl {}
      (fn [type screen]
        (type "(slow)\r")
        (screen #"\(slow\)\n$")
        (System.Threading.Thread/Sleep 100)
        (type "\u0003")
        (is (screen #"Ctrl-C again stops waiting for it\n:slow\nuser=> $")))))
  (testing ":repl/quit"
    (call-with-repl {}
      (fn [type _]
        (type ":repl/quit\r")))))

(deftest dumb-terminal-test
  (testing "a dumb terminal gets the plain REPL"
    (call-with-temp-dir
     (fn [dir]
       (is (= "user=> 3\nuser=> \n"
              (lf (with-out-str
                (with-in-str "(+ 1 2)\n"
                  (main/run ["repl" "-p" (str *port*)]
                            {:dir dir
                             :sessions-file (test-server/file dir "sessions.edn")
                             :history-dir (test-server/file dir "history")
                             :terminal {:input (StringReader. "")
                                        :output (StringWriter.)
                                        :width 80
                                        :type "dumb"}}))))))))))

(deftest lookup-test
  (testing "TAB completes"
    (call-with-repl {}
      (fn [type screen]
        (type "(mapc")
        (type "\t")
        (type " list [[1]])\r")
        (is (screen #"(?m)^\(\[1\]\)\nuser=> $")))))
  (testing "eldoc shows the arglists of the function being called"
    (call-with-repl {:eldoc? true}
      (fn [type screen]
        (type "(mapcat ")
        (is (screen #"\[f\] \[f & colls\]"))
        (type "\u0003"))))
  (testing "eldoc for something that isn't a function"
    (call-with-repl {:eldoc? true}
      (fn [type screen]
        (type "(def cfg {:a 1})\r")
        (screen #"#'user/cfg\nuser=> $")
        (type "(cfg :a)\r")
        (is (screen #"(?m)^1\nuser=> $")))))
  (testing "C-x C-d on a keyword shows the call's documentation"
    (call-with-repl {}
      (fn [type screen]
        (type "(assoc {} :k")
        (type "\u0018\u0004")
        (is (screen #"clojure\.core/assoc\n"))
        (type "\u0003"))))
  (testing "C-x C-d shows the documentation"
    (call-with-repl {}
      (fn [type screen]
        (type "(when")
        (type "\u0018\u0004")
        (is (screen #"clojure\.core/when\n\(\[test & body\]\)\nMacro\n  Evaluates test"))
        (type "\u0003")))))

(deftest commands-test
  (call-with-repl {}
    (fn [type screen]
      (type ",doc when\r")
      (is (screen #"clojure\.core/when\n\(\[test & body\]\)\nMacro\n"))
      (type ",apropos ^mapcat$\r")
      (is (screen #"clojure\.core/mapcat\nuser=> $"))
      (type ",macroexpand (when x\r")
      (is (screen #"#_=> $"))
      (type "y)\r")
      (is (screen #"\(if x \(do y\)\)\nuser=> $"))
      (testing "and code pasted after a command runs after it"
        (type "\u001b[200~,doc inc\n(+ 40 2)\u001b[201~\r")
        (is (screen #"clojure\.core/inc\n(?s).*\n42\nuser=> $"))))))
