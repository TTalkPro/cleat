namespace Cleat.Core.Runtime;

/// <summary>运行时的版本与标识。</summary>
public static class CoreInfo
{
    /// <summary>cleat 自己的版本（*cleat-version*、Cleat.Runtime 包的缺省版本）。</summary>
    public const string Version = "1.1.0";

    /// <summary>cleat 实现的 Clojure 语言版本（*clojure-version*）：core.clj 跟的是 ClojureCLR master，已含 1.13 的新增。</summary>
    public const string ClojureVersion = "1.13.0";

    /// <summary>REPL 启动与 --version 打印的一行。</summary>
    public static string Banner => "Cleat " + Version + " (Clojure " + ClojureVersion + ")";
}
