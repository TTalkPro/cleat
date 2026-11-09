(ns clojure.tools.analyzer.clr.core-test
  (:refer-clojure :exclude [macroexpand-1])
  (:require [clojure.tools.analyzer :as ana]
            [clojure.tools.analyzer.clr :as ana.clr]
            [clojure.tools.analyzer.env :as env]
            [clojure.tools.analyzer.passes.elide-meta :refer [elides elide-meta]]
            [clojure.tools.analyzer.ast :refer [postwalk]]
            [clojure.tools.reader :as r]
            [clojure.test :refer [deftest is]])
  (:import (System.IO FileInfo)))                                                                            ;;; java.io File
			
(assembly-load-from (str clojure.lang.RT/SystemRuntimeDirectory "System.ComponentModel.dll"))

(defprotocol p (f [_]))
(defn f1 [^long x])
(def e (ana.clr/empty-env))

(defmacro ast [form]
  `(binding [ana/macroexpand-1 ana.clr/macroexpand-1
             ana/create-var    ana.clr/create-var
             ana/parse         ana.clr/parse
             ana/var?          var?
             elides            {:all #{:line :column :file}}]
     (env/with-env (ana.clr/global-env)
       (postwalk (ana/analyze '~form e) elide-meta))))
	   
(defn ana [form]
  (binding [ana/macroexpand-1 ana.clr/macroexpand-1
            ana/create-var    ana.clr/create-var
            ana/parse         ana.clr/parse
            ana/var?          var?
            elides            {:all #{:line :column :file}}]
    (ana.clr/analyze form e)))	   

(defmacro ast1 [form]
  `(ana '~form))

(defmacro mexpand [form]
  `(ana.clr/macroexpand-1 '~form e))

(deftest macroexpander-test
  (is (= (list '. (list 'do System.Object) 'ToString)                                     ;;; java.lang.Object toString
         (mexpand (.ToString Object))))                                                   ;;; .toString
  (is (= (list '. System.Int32 '(Parse "2")) (mexpand (Int32/Parse "2")))))               ;;; java.lang.Integer parseInt Integer/parseInt 

(deftest analyzer-test

  (let [v-ast (ast #'+)]
    (is (= :the-var (:op v-ast)))
    (is (= #'+ (:var v-ast))))

  (let [mn-ast (ast (monitor-enter 1))]
    (is (= :monitor-enter (:op mn-ast)))
    (is (= 1 (-> mn-ast :target :form))))

  (let [mx-ast (ast (monitor-exit 1))]
    (is (= :monitor-exit (:op mx-ast)))
    (is (= 1 (-> mx-ast :target :form))))

  (let [i-ast (ast (clojure.core/import* "System.String"))]                                       ;;; "java.lang.String"
    (is (= :import (:op i-ast))) 
    (is (= "System.String" (:class i-ast))))                                                      ;;; "java.lang.String"

  (let [r-ast (ast ^:foo (reify
                           Object (ToString [this] "")                                            ;;; toString
                           System.IServiceProvider (GetService [this ^Type serviceType] this)))]  ;;; Appendable (^Appendable append [this ^char x] this)
    (is (= :with-meta (-> r-ast :op))) ;; line/column info
    (is (= :reify (-> r-ast :expr :op)))
    (is (= #{System.IServiceProvider clojure.lang.IObj} (-> r-ast :expr :interfaces)))            ;;; #{Appendable clojure.lang.IObj}
    (is (= '#{ToString GetService} (->> r-ast :expr :methods (mapv :name) set))))                 ;;; #{toString append}

  (let [dt-ast (ast (deftype* x user.x [a b]
                      :implements [System.IServiceProvider]                                              ;;; Appendable
                      (GetService [this ^Type serviceType] this)))]                               ;;; (^Appendable append [this ^char x] this)
    (is (= :deftype (-> dt-ast :op)))
    (is (= '[a b] (->> dt-ast :fields (mapv :name))))
    (is (= '[GetService] (->> dt-ast :methods (mapv :name))))                                     ;;; append
    (is (= 'user.x (-> dt-ast :class-name))))

  (let [c-ast (ast (case* 1 0 0 :number {2 [2 :two] 3 [3 :three]} :compact :int))]
    (is (= :number (-> c-ast :default :form)))
    (is (= #{2 3} (->> c-ast :tests (mapv (comp :form :test)) set)))
    (is (= #{:three :two} (->> c-ast :thens (mapv (comp :form :then)) set)))
    (is (= 3 (-> c-ast :high)))
    (is (= :int (-> c-ast :test-type)))
    (is (= :compact (-> c-ast :switch-type)))
    (is (= 2 (-> c-ast :low)))
    (is (= 0 (-> c-ast :shift)))
    (is (= 0 (-> c-ast :mask))))

  (is (= Exception (-> (ast1 (try (catch :default e))) :catches first :class :val)))             ;;; Throwable
  (is (= Exception (-> (ast1 (try (catch Exception e e))) :catches first :body :tag))))

(deftest doseq-chunk-hint
  (let [tree (ast1 (doseq [item (range 10)]
                     (println item)))
        {[_ chunk] :bindings} tree]
    (is (= :loop (:op tree)))
    (is (.StartsWith (name (:name chunk)) "chunk"))                                       ;;; .StartsWith
    (is (= clojure.lang.IChunk (:tag chunk)))))

(def ^:dynamic *test-dynamic*)
(deftest set!-dynamic-var
  (is (ast1 (set! *test-dynamic* 1))))

(deftest analyze-proxy
  (is (ast1 (proxy [Object] []))))

(deftest analyze-record
  (is (ast1 (defrecord TestRecord [x y]))))

(deftest eq-no-reflection
  (is (:validated? (-> (ast1 (fn [s] (= s \f))) :expr :methods first :body))))         ;;; I had to add the :expr to get this to work.

(deftest analyze+eval-context-test
  (let [do-ast (ana.clr/analyze+eval '(do 1 2 3))]
    (is (= :ctx/statement (-> do-ast :statements first :env :context)))))
	
(deftest array_class
  (is (ana (r/read-string "(fn [^{:tag int/2} x] (instance? int/2 x))"))))	
  
(deftest macroexpander-qualified-methods-test
  (is (= (list '. Int32 (symbol "-MaxValue"))                   ;;; Integer  "-MAX_VALUE"
         (mexpand Int32/MaxValue)))                             ;;; Integer/MAX_VALUE

  (is (= 'String/.get_Length (mexpand String/.get_Length)))             ;;; .length   .length 
  (is (= 'Int32/.Abs (mexpand Int32/.Abs)))                     ;;; Integer/.intValue   Integer/.intValue 

  (is (= 'String/new (mexpand String/new)))

  (is (= 'String/IsNullOrWhiteSpace (mexpand String/IsNullOrWhiteSpace)))     ;;; String/valueOf
  (is (= 'Int32/Parse (mexpand Int32/Parse)))                                 ;;; Integer/parseInt  Integer/parseInt

  (is (= '(String/new "hello") (mexpand (String/new "hello"))))
  (is (= '(String/.substring "hello" 1 3) (mexpand (String/.substring "hello" 1 3))))
  (is (= '(String/.length "hello") (mexpand (String/.length "hello"))))
  (is (= '(Integer/parseInt "2") (mexpand (^{:param-tags [String]} Integer/parseInt "2"))))

  (let [expanded (mexpand (Int32/Parse "2"))]                    ;;; Integer/parseInt
    (is (= '. (first expanded)))
    (is (= System.Int32 (second expanded)))))                    ;;; java.lang.Integer

(deftest analyzer-qualified-methods-test
  (let [a (ast1 FileInfo/.get_Name)]                             ;;; File/.getName   TODO:  When we can handle instance properties with QME, add an example for FileInfo/.Name
    (is (= :method-value (:op a)))
    (is (= :instance (:kind a)))
    (is (= 'get_Name (:method a)))                               ;;; getName 
    (is (= System.IO.FileInfo (:class a))))                      ;;; java.io.File

  (let [a (ast1 String/IsNullOrWhiteSpace)]                      ;;; String/valueOf
    (is (= :method-value (:op a)))
    (is (= :static (:kind a)))
    (is (= 'IsNullOrWhiteSpace (:method a)))                     ;;; valueOf
    (is (= String (:class a))))

  (let [a (ast1 FileInfo/new)]                                   ;;; File
    (is (= :method-value (:op a)))
    (is (= :ctor (:kind a)))
    (is (= System.IO.FileInfo (:class a))))                      ;;; java.io.File 

  (let [a (ast1 Int32/MaxValue)]                                 ;;; Integer/MAX_VALUE
    (is (= :static-field (:op a)))
    (is (= Int32 (:class a))))                                   ;;; Integer

  (let [a (ana (r/read-string "String/1"))]
    (is (= :const (:op a)))
    (is (= :class (:type a)))
    (is (.IsArray ^Type (:val a))))                              ;;; .isArray ^Class

  (let [a (ast1 (FileInfo/new "."))]                             ;;; File
    (is (= :new (:op a))))

  (let [a (ast1 (String/.get_Length "hello"))]                       ;;; .length
    (is (= :instance-call (:op a)))
    (is (= 'get_Length (:method a))))                                ;;; length

  (let [a (ast1 (String/.Substring "hello"1 3))]                 ;;; .substring
    (is (= :instance-call (:op a)))
    (is (= 'Substring (:method a))))                             ;;; substring

  (let [a (ast1 (Int32/Parse "7"))]                              ;;; Integer/parseInt
    (is (= :static-call (:op a)))
    (is (= 'Parse (:method a)))))                                ;;; parseInt