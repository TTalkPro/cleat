;; WebServer 示例的处理函数（cleat-035）：C# 侧经 Cleat.Var("web.handlers", …) 调用。
(ns web.handlers)

(defn greet
  "打招呼；name 来自查询参数（string）。"
  [name]
  (str "hello, " name "!"))

(defn sum
  "求和；xs 是 C# 传来的 long[]。返回值转成文本（C# 侧只回文本）。"
  [xs]
  (str (reduce + 0 xs)))

(def requests (atom 0))

(defn stats
  "示例里的有状态处理函数：atom 计数请求数（并发请求被串行化，计数仍然准确）。"
  []
  (str "requests: " (swap! requests inc)))
