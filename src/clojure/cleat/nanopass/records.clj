;; cleat.nanopass 的运行时部分（cleat-083，designs/19 D2、07 D6）：节点接口、语言的运行时对象与注册表、构造检查的辅助函数。
;; 每个产生式是一个 deftype（引用相等，字段按元变量名），实现 INode；每个语言另有一个标记接口（L0$Node），
;; 非终结符谓词靠它区分同标签、不同语言的节点。
(ns cleat.nanopass.records)

(definterface INode
  (^long nanoTag [])          ; 产生式在本语言里的序号（按定义顺序，0 起）
  (nanoFields [])             ; 字段值的向量，顺序同产生式里元变量出现的顺序
  (nanoLang []))              ; 语言的限定名（符号）

(defn node? [x] (instance? INode x))

;; —— 语言的运行时对象 ——
;; desc：语言描述（cleat.nanopass.language 生成的纯数据）；preds：终结符名 → 谓词函数；ctors：标签 → 带检查的构造函数；
;; by-tag：标签 → 产生式描述；nts：非终结符名 → 描述；pretties：终结符名 → unparse 的美化函数（=>，cleat-095）
(defrecord Language [name desc preds ctors by-tag nts pretties])

;; 限定名 → Language（加载期登记；宏展开期另有描述的注册表，见 cleat.nanopass.language）
(defonce ^:private languages (atom {}))

(defn register-runtime! [^Language lang]
  (swap! languages assoc (:name lang) lang)
  lang)

(defn runtime-language
  "语言的运行时对象：参数为限定名或 Language；没有登记时报错。"
  [x]
  (cond
    (instance? Language x) x
    :else (or (get @languages x)
              (throw (ex-info (str "nanopass: unknown language " x) {:language x})))))

(defn node-language [^INode n] (runtime-language (.nanoLang n)))

;; —— 构造函数的检查 ——

(defn bad-field [lang prod field v]
  (throw (ex-info (str "nanopass: " lang " " prod ": expected " field ", got " (pr-str v))
                  {:language lang :production prod :field field :value v})))

(defn absent? [v] (or (nil? v) (false? v)))

(defn ->vec
  "省略号字段的值：已是向量原样，其余（惰性序列等）实现成向量。"
  [x]
  (if (vector? x) x (vec x)))

(defn check-all
  "一层序列的每个元素都满足 ok?（构造检查的快路径，不为每次调用建闭包）。"
  [ok? v]
  (or (nil? v)
      (and (sequential? v)
           (loop [s (seq v)]
             (cond (nil? s) true
                   (ok? (first s)) (recur (next s))
                   :else false)))))

(defn check-seq
  "depth 层嵌套序列的每个元素都满足 ok?（depth ≥ 1）。"
  [ok? v depth]
  (and (or (nil? v) (sequential? v))
       (if (== depth 1)
         (every? ok? v)
         (every? #(check-seq ok? % (dec depth)) v))))

;; 子句不匹配（模式测试或 guard 失败）时子句代码的值；处理器据此试下一个子句（cleat-084）
(defonce no-match (Object.))

(defn brief
  "错误信息里的节点：限制打印的深度与长度（整棵树可能有几百万个节点）。"
  [x]
  (binding [*print-level* 6 *print-length* 8] (pr-str x)))
