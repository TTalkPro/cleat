(ns clojure.tools.analyzer.clr.passes-test
  (:refer-clojure :exclude [macroexpand-1])
  (:require [clojure.tools.analyzer.ast :refer :all]
            [clojure.tools.analyzer.clr :as ana.clr]
            [clojure.tools.analyzer.env :as env]
            [clojure.tools.analyzer.passes :refer [schedule]]
            [clojure.test :refer [deftest is]]
            [clojure.set :as set]
            [clojure.tools.analyzer.passes.add-binding-atom :refer [add-binding-atom]]
            [clojure.tools.analyzer.passes.collect-closed-overs :refer [collect-closed-overs]]
            [clojure.tools.reader :as r]
            [clojure.tools.analyzer.clr.core-test :refer [ast ast1 ana e f f1]]
            [clojure.tools.analyzer.passes.clr.emit-form
             :refer [emit-form emit-hygienic-form]]
            [clojure.tools.analyzer.passes.clr.validate :as v]
            [clojure.tools.analyzer.passes.clr.annotate-tag :refer [annotate-tag]]
            [clojure.tools.analyzer.passes.clr.infer-tag :refer [infer-tag]]
            [clojure.tools.analyzer.passes.clr.annotate-branch :refer [annotate-branch]]
            [clojure.tools.analyzer.passes.clr.annotate-host-info :refer [annotate-host-info]]
            [clojure.tools.analyzer.passes.clr.annotate-loops :refer [annotate-loops]]
            [clojure.tools.analyzer.passes.clr.fix-case-test :refer [fix-case-test]]
            [clojure.tools.analyzer.passes.clr.analyze-host-expr :refer [analyze-host-expr]]
            [clojure.tools.analyzer.passes.clr.classify-invoke :refer [classify-invoke]])
  (:import (clojure.lang Keyword Var Symbol AFunction ExceptionInfo
                         PersistentVector PersistentArrayMap PersistentHashSet ISeq)
           System.Text.RegularExpressions.Regex
		   (System.IO FileInfo)
		   #_(java.util UUID Arrays)
		   ))                                                                             ;;; clojure.tools.analyzer.jvm.test.FieldMethodOverload

(defn validate [ast]
  (env/with-env (ana.clr/global-env)
    (v/validate ast)))

(deftest emit-form-test
  (is (= '(monitor-enter 1) (emit-form (ast (monitor-enter 1)))))
  (is (= '(monitor-exit 1) (emit-form (ast (monitor-exit 1)))))
  (is (= '(clojure.core/import* "System.String")                                                ;;; "java.lang.String"
         (emit-form (validate (ast (clojure.core/import* "System.String"))))))                  ;;; "java.lang.String"
  (is (= '(var clojure.core/+) (emit-form (ast #'+))))
  (is (= '(:foo {}) (emit-form (ast (:foo {})))))
  (is (= '(try 1 (catch Exception e nil))
         (emit-form (ana.clr/analyze '(try 1 (catch Exception e))))))
  (is (= '(try 1 (catch Exception e nil))
         (emit-form (ana.clr/analyze '(try 1 (catch Exception e)))
                    {:qualifed-symbols true})))
  (is (= '(f [] 1) (emit-form (ast (f [] 1))))))

(deftest annotate-branch-test
  (let [i-ast (annotate-branch (ast (if 1 2 3)))]
    (is (:branch? i-ast))
    (is (= true (-> i-ast :test :test?)))
    (is (= true (-> i-ast :then :path?)))
    (is (= true (-> i-ast :else :path?))))

  (let [fn-ast (prewalk (ast (fn ([]) ([x]))) annotate-branch)]
    (is (every? :path? (-> fn-ast :methods))))

  (let [r-ast (prewalk (ast (reify Object (toString [this] x))) annotate-branch)]
    (is (every? :path? (-> r-ast :methods))))

  (let [c-ast (-> (ast (case 1 0 0 2 2 1)) :body :ret (prewalk annotate-branch))]
    (is (:branch? c-ast))
    (is (= true (-> c-ast :test :test?)))
    (is (= true (-> c-ast :default :path?)))
    (is (every? :path? (-> c-ast :thens)))))

(deftest fix-case-test-test
  (let [c-ast (-> (ast (case 1 1 1)) add-binding-atom (prewalk fix-case-test))]
    (is (= true (-> c-ast :body :ret :test :atom deref :case-test)))))

(deftest annotate-tag-test
  (is (= PersistentVector (-> {:op :const :form [] :val []} annotate-tag :tag)))
  (is (= PersistentVector (-> (ast []) annotate-tag :tag)))
  (is (= PersistentArrayMap(-> (ast {}) annotate-tag :tag)))
  (is (= PersistentHashSet (-> (ast #{}) annotate-tag :tag)))
  (is (= System.RuntimeType (-> {:op :const :type :class :form Object :val Object}         ;;; Class
                 annotate-tag :tag)))
  (is (= String (-> (ast "foo") annotate-tag :tag)))
  (is (= Keyword (-> (ast :foo) annotate-tag :tag)))
  (is (= Char (-> (ast \f) annotate-tag :tag)))                                            ;;; Character/TYPE
  (is (= Int64 (-> (ast 1) annotate-tag :tag)))                                            ;;; Long/TYPE
  (is (= Regex (-> (ast #"foo") annotate-tag :tag)))                                       ;;; Pattern
  (is (= Var (-> (ast #'+)  annotate-tag :tag)))
  (is (= Boolean (-> (ast true) annotate-tag :tag)))
  (let [b-ast (-> (ast (let [a 1] a)) add-binding-atom
                 (postwalk annotate-tag))]
    (is (= Int64 (-> b-ast :body :ret :tag)))))                                            ;;; Long/TYPE

(deftest classify-invoke-test
  (is (= :keyword-invoke (-> (ast (:foo {})) classify-invoke :op)))
  (is (= :invoke (-> (ast (:foo {} 1)) classify-invoke :op)))
  (is (= :protocol-invoke (-> (ast (f nil)) classify-invoke :op)))
  (is (= :instance? (-> (ast (instance? String ""))
                      (prewalk analyze-host-expr) classify-invoke :op)))
  (is (= :prim-invoke (-> (ast (f1 1)) (prewalk infer-tag) classify-invoke :op))))          ;;; FAIL -- Why do we get :invoke instead of :prim-invoke?

(deftest annotate-host-info-test
  (let [r-ast (-> (ast ^:foo (reify Object (ToString [_] ""))) (prewalk annotate-host-info))]          ;;; toString
    (is (= 'ToString (-> r-ast :expr :methods first :name)))                                           ;;; toString
    (is (= [] (-> r-ast :expr :methods first :params)))
    (is (= '_ (-> r-ast :expr :methods first :this :name)))))

;; TODO: test primitives, tag matching, throwing validation, method validation
(deftest validate-test
  (is (= Exception (-> (ast (try (catch Exception e)))
                     (prewalk (comp validate analyze-host-expr)) :catches first :class :val)))
  (is (-> (ast (set! *warn-on-reflection* true)) validate))
  (is (= true (-> (ast (String. \a (int 5))) (postwalk (comp validate annotate-tag analyze-host-expr))          ;;; 
              :validated?)))

  (let [s-ast (-> (ast (Int32/Parse "7")) (prewalk annotate-tag) analyze-host-expr validate)]           ;;;Integer/parseInt
    (is (:validated? s-ast))
    (is (= Int32 (:tag s-ast)))                                                                         ;;; Integer/TYPE
    (is (= [String] (mapv :tag (:args s-ast)))))

  (let [i-ast (-> (ast (.GetHashCode "7")) (prewalk annotate-tag) analyze-host-expr validate)]          ;;; .hashCode 
    (is (:validated? i-ast))
    (is (= Int32 (:tag i-ast)))                                                                         ;;; Integer/TYPE 
    (is (= [] (mapv :tag (:args i-ast))))
    (is (= String (:class i-ast))))

  (is (= true (-> (ast (import System.String)) (prewalk validate) :ret :validated?))))                    ;;; java.lang.String

;; we need all or most those passes to perform those tests
(deftest all-passes-test
  (let [t-ast (ast1 (let [a 1
                          b 2
                          c (str a)
                          d (Int32/Parse c b)]                                                            ;;; Integer/parseInt
                      (Int32/Parse c b)))]                                                                                 ;;; (Integer/getInteger c d) - no direct equivalent.  Need to adjust.
    (is (= Int32 (-> t-ast :body :tag)))                                                                  ;;; Integer
    (is (= Int32 (-> t-ast :tag)))                                                                        ;;; Integer
    (is (= Int64 (->> t-ast :bindings (filter #(= 'a (:form %))) first :tag)))                            ;;; Long/TYPE
    (is (= String (->> t-ast :bindings (filter #(= 'c (:form %))) first :tag)))
    (is (= Int32 (->> t-ast :bindings (filter #(= 'd (:form %))) first :tag))))                           ;;; Integer/TYPE
  (is (= Void (:tag (ast1 (.Write System.Console/Out "foo")))))                                                 ;;; Void/TYPE  .println System/out

  (is (= String (-> (ast1 String) :val)))
  (is (= 'String (-> (ast1 String) :form)))
(is (= PersistentVector (-> (ast1 '[]) :tag)))
(is (= ISeq (-> (ast1 '()) :tag)))

  (let [d-ast (ast1 (Double/IsInfinity 2))]                                                                ;;; Double/isInfinite
    (is (= Boolean (-> d-ast :tag)))                                                                       ;;; Boolean/TYPE
    (is (= Double (->> d-ast :args first :tag)))))                                                         ;;; Double/TYPE 

;; checks for specific bugs that have surfaced
(deftest annotate-case-loop
  (is (ast1 (loop [] (case 1 :a (recur) :b 42)))))

(deftest var-tag-inference
  (let [ast (ana.clr/analyze '(def a "foo")
                             (ana.clr/empty-env)
                             {:passes-opts (merge ana.clr/default-passes-opts
                                                  {:infer-tag/level :global})})]
    (is (= String (-> ast :var meta :tag)))))

(deftest validate-handlers
  ;; test for tanal-24, without the handler analysis would throw
  ;; with an handler that ignores the tag, we can simulate the current behaviour
  ;; of the clojure compiler
  (is (ana.clr/analyze '(defn ^long a [] 1)
                       (ana.clr/empty-env)
                       {:passes-opts (merge ana.clr/default-passes-opts
                                            {:validate/wrong-tag-handler (fn [t ast]
                                                                           {t nil})})})))
																		   
(deftest method-value-emit-form-test
  (is (= 'System.IO.FileInfo/.get_Name (emit-form (ast1 FileInfo/.get_Name))))                       ;;; java.io.File/.getName   File/.getName  TODO: When we get QMEs working on instance properties, add a test for FileInfo/.Name

  (is (= 'System.String/IsNullOrWhiteSpace (emit-form (ast1 String/IsNullOrWhiteSpace))))         ;;; java.lang.String/valueOf  String/valueOf

  (is (= 'System.IO.FileInfo/new (emit-form (ast1 FileInfo/new))))                                   ;;; java.io.File

  (let [emitted (emit-form (ana (r/read-string "^[long] Math/Abs")))]                      ;;;   String/valueOf  -- needed to find something overloaded that had an Int64 parameter
    (is (= 'System.Math/Abs emitted))                                                       	;;; java.lang.String/valueOf
    (is (= '[long] (:param-tags (meta emitted)))))

  (let [emitted (emit-form (ana (r/read-string "^[int int] String/.Substring")))]          ;;; .substring
    (is (= 'System.String/.Substring emitted))                                             ;;; java.lang.String/.substring
    (is (= '[int int] (:param-tags (meta emitted))))))

(deftest method-value-validate-test
  (let [a (ast1 FileInfo/.get_Name)]                               ;;; File/.getName
    (is (= :method-value (:op a)))
    (is (:validated? a))
    (is (= System.IO.FileInfo (:class a))))                        ;;; java.io.File

  (let [a (ast1 Math/Abs)]                                         ;;; String/valueOf
    (is (= :method-value (:op a)))
    (is (:validated? a))
    (is (pos? (count (:methods a)))))

  (let [a (ast1 FileInfo/new)]                                     ;;; File
    (is (= :method-value (:op a)))
    (is (:validated? a))
    (is (= :ctor (:kind a))))

  (let [a (ana (r/read-string "^[long] Math/Abs"))]                ;;; String/valueOf
    (is (= :method-value (:op a)))
    (is (:validated? a))
    (is (= 1 (count (:methods a))))
    (is (= '[System.Int64] (-> a :methods first :parameter-types))))       ;;; '[long]   Java primitive value

  (let [a (ana (r/read-string "^[int int] String/.Substring"))]    ;;; .substring
    (is (= :method-value (:op a)))
    (is (:validated? a))
    (is (= 1 (count (:methods a))))
    (is (= '[System.Int32 System.Int32] (-> a :methods first :parameter-types)))))        ;;; '[int int]

(deftest method-value-kinds-test
  (let [a (ast1 FileInfo/.get_Exists)]                                 ;;; File/.isDirectory
    (is (= :instance (:kind a)))
    (is (= 'get_Exists (:method a))))                                  ;;; isDirectory

  (let [a (ast1 Char/IsDigit)]                                     ;;; Character/isDigit
    (is (= :method-value (:op a)))
    (is (= :static (:kind a)))
    (is (= 'IsDigit (:method a)))                                  ;;; isDigit
    (is (< 1 (count (:methods a)))))

  (let [a (ast1 String/new)]
    (is (= :ctor (:kind a)))
    (is (= String (:class a))))

  (let [a (ast1 FileInfo/.get_Name)]                               ;;; File/.getName
    (is (= AFunction (:o-tag a)))))

(deftest method-value-field-overload-test
  (let [a (ast1 Int32/MaxValue)]                                   ;;; Integer/MAX_VALUE
    (is (= :static-field (:op a))))

  (let [a (ast1 Boolean/FalseString)]                              ;;; TRUE
    (is (= :static-field (:op a)))))

(deftest qualified-method-invocation-test
  (let [a (ast1 (FileInfo/new "."))]                               ;;; File
    (is (= :new (:op a))))

  (let [a (ast1 (String/.get_Length "hello"))]                                        ;;; .length
    (is (= :instance-call (:op a)))
    (is (= 'get_Length (:method a)))                                                  ;;; length
    (is (:validated? a)))

  (let [a (ast1 (String/.Substring "hello" 1 3))]                                 ;;; .substring
    (is (= :instance-call (:op a)))
    (is (= 'Substring (:method a)))                                               ;;; substring
    (is (= 2 (count (:args a)))))

  (let [a (ast1 (Int32/Parse "7"))]                                               ;;; Integer/parseInt
    (is (= :static-call (:op a)))
    (is (= 'Parse (:method a)))                                                   ;;; parseInt
    (is (:validated? a)))

  (let [a (ast1 (FileInfo/.get_Exists (FileInfo. ".")))]                          ;;; File/.isDirectory    File. 
    (is (= :instance-call (:op a)))
    (is (= 'get_Exists (:method a)))))                                            ;;; isDirectory

(deftest param-tags-invocation-test
  (let [a (ana (r/read-string "(^[long] Math/Abs 42)"))]                          ;;; String/valueOf
    (is (= :static-call (:op a)))
    (is (:validated? a))
    (is (= '[long] (:param-tags a))))

  (let [a (ana (r/read-string "(^[int int] String/.Substring \"hello\" 1 3)"))]   ;;; .substring
    (is (= :instance-call (:op a))) 
    (is (:validated? a))
    (is (= '[int int] (:param-tags a))))

  (let [a (ana (r/read-string "(^[int _] String/.Substring \"hello\" 1 3)"))]     ;;; .substring
    (is (= :instance-call (:op a)))
    (is (:validated? a))
    (is (= '[int _] (:param-tags a))))

  #_(let [a (ana (r/read-string "^[int/1] java.util.Arrays/sort"))]
    (is (= :method-value (:op a)))
    (is (= :static (:kind a)))
    (is (= 1 (count (:methods a))))))

(deftest existing-interop-unchanged-test
  (let [a (ast1 (.get_Length "hello"))]                                               ;;; .length
    (is (= :instance-call (:op a)))
    (is (:validated? a)))

  (let [a (ast1 (String. (.ToCharArray "foo")))]
    (is (= :new (:op a)))
    (is (:validated? a)))

  (is (= System.Object (:tag (ast1 (.WriteLine System.Console "foo")))))            ;;;  Void/TYPE .println System/out

  (let [a (ast1 (Int32/Parse "7"))]                                               ;;; Integer/parseInt
    (is (= :static-call (:op a)))
    (is (:validated? a)))

  (let [a (ast1 Int32/MaxValue)]                                    ;;; Integer/MAX_VALUE
    (is (= :static-field (:op a))))

  #_(let [a (ast1 Boolean/TYPE)]
    (is (= :static-field (:op a)))))

(deftest bad-method-names-test
  (is (thrown? ExceptionInfo (ast1 String/foo)))
  (is (thrown? ExceptionInfo (ast1 String/.foo)))
  (is (thrown? ExceptionInfo (ast1 Math/new))))

(deftest param-tags-method-signature-selection-test
  (let [a (ana (r/read-string "^[double] Math/Abs"))]               ;;; abs
    (is (= :method-value (:op a)))
    (is (= 1 (count (:methods a))))
    (is (= '[System.Double] (-> a :methods first :parameter-types)))       ;;; '[double]  Java primitive type
    (is (:validated? a)))

  (let [a (ana (r/read-string "^[float] Math/Abs"))]               ;;; abs
    (is (= :method-value (:op a)))
    (is (= 1 (count (:methods a))))
    (is (= '[System.Single] (-> a :methods first :parameter-types)))         ;;; '[float]  Java primitive type
    (is (:validated? a)))

  (let [a (ana (r/read-string "^[long] Math/Abs"))]               ;;; abs
    (is (= :method-value (:op a)))
    (is (= 1 (count (:methods a))))
    (is (= '[System.Int64] (-> a :methods first :parameter-types)))       ;;;; '[long]  Java primitive type
    (is (:validated? a)))

  (let [a (ana (r/read-string "^[int] Math/Abs"))]               ;;; abs
    (is (= :method-value (:op a)))
    (is (= 1 (count (:methods a))))
    (is (= '[System.Int32] (-> a :methods first :parameter-types)))      ;;;  '[int]   Java primitive type
    (is (:validated? a))))
	
(deftest param-tags-constructor-invocation-test
  (let [a (ana (r/read-string "(^[String] System.Guid/new \"1\")"))]              ;;; "(^[long long] java.util.UUID/new 1 2)"
    (is (= :new (:op a)))
    (is (:validated? a))
    (is (= '[String] (:param-tags a))))                                ;;; '[long long]

  (let [a (ana (r/read-string "(^[Character int] String/new \\a (int 12))"))]               ;;; "(^[String] String/new \"a\")"
    (is (= :new (:op a)))
    (is (:validated? a))
    (is (= '[Character int] (:param-tags a)))))

(deftest param-tags-no-arg-invocation-test
  (let [a (ana (r/read-string "(^[] String/.ToUpper \"hello\")"))]            ;;; .toUpperCase
    (is (= :instance-call (:op a)))
    (is (:validated? a))
    (is (= '[] (:param-tags a))))

  (let [a (ana (r/read-string "(^[] Int64/.ToString 42)"))]                   ;;; Long/.toString          XXX
    (is (= :instance-call (:op a)))
    (is (:validated? a))
    (is (= '[] (:param-tags a)))))

(deftest param-tags-wildcard-test
  (let [a (ana (r/read-string "(^[_ _] String/.Substring \"hello\" 1 3)"))]   ;;; .substring
    (is (= :instance-call (:op a)))
    (is (:validated? a))
    (is (= '[_ _] (:param-tags a)))))

#_(deftest param-tags-array-types-test                                                ;;; no good equivalent at this time
  (let [a (ana (r/read-string "^[long/1 long] java.util.Arrays/binarySearch"))]
    (is (= :method-value (:op a)))
    (is (= 1 (count (:methods a))))
    (is (= '[long<> long] (-> a :methods first :parameter-types)))
    (is (:validated? a)))

  (let [a (ana (r/read-string "^[Object/1 _] java.util.Arrays/binarySearch"))]
    (is (= :method-value (:op a)))
    (is (= 1 (count (:methods a))))
    (is (= '[java.lang.Object<> java.lang.Object] (-> a :methods first :parameter-types)))
    (is (:validated? a))))
	
(deftest bad-param-tags-test
  (is (thrown? ExceptionInfo (ana (r/read-string "^[String String] Math/Abs"))))          ;; abs          
  (is (thrown? ExceptionInfo (ana (r/read-string "(^[] String/foo \"a\")"))))
  (is (thrown? ExceptionInfo (ana (r/read-string "(^[] String/.foo \"a\")"))))
  (is (thrown? ExceptionInfo (ana (r/read-string "(^[String String String] java.util.UUID/new 1 2 3)")))))	
  
#_(deftest field-method-overload-test                                                                         ;;; We cannot define a field and a method with the same name, so this test does not apply
  (let [a (ast1 clojure.tools.analyzer.jvm.test.FieldMethodOverload/doppelganger)]
    (is (= :static-field (:op a))))

  (let [a (ana (r/read-string "^[] clojure.tools.analyzer.jvm.test.FieldMethodOverload/doppelganger"))]
    (is (= :method-value (:op a)))
    (is (= 1 (count (:methods a))))
    (is (= '[] (-> a :methods first :parameter-types))))

  (let [a (ast1 (clojure.tools.analyzer.jvm.test.FieldMethodOverload/doppelganger))]
    (is (= :static-call (:op a)))
    (is (:validated? a)))

  (let [a (ast1 (clojure.tools.analyzer.jvm.test.FieldMethodOverload/doppelganger (int 1) (int 2)))]
    (is (= :static-call (:op a)))
    (is (:validated? a)))) 