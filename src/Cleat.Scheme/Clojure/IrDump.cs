using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using clojure.lang;

namespace Cleat.Scheme.Clojure;

/// <summary>
/// 中间语言的规范文本与逐形式转储（cleat-085，designs/19 D5 的差分门控）：Scheme 编译器与 Clojure 编译器把同一个形式各阶段的 IR
/// 打印成同一种文本，写到 <c>CLEAT_DUMP_IR</c> 目录下 <c>&lt;一侧&gt;/&lt;单元&gt;.ir</c>，由 tools/compiler-diff.clj 读回比较。
/// <para>文本是 Clojure 可读的数据：IR 的结构是列表，IR 里的标识符（变量、原语名、全局位置）是裸符号；数据位置的值用
/// <see cref="PrintDatum"/>，Clojure 与 Scheme 的值带 <c>#cleat/…</c> 标签区分（同名的 Clojure 符号与 Scheme 符号不相等）。</para>
/// 过渡期的工具：阶段 E（cleat-093）随 Scheme 一起删除。
/// </summary>
public static class IrDump
{
    /// <summary>转储目录；没有设置时不转储。</summary>
    public static readonly string Directory = Environment.GetEnvironmentVariable("CLEAT_DUMP_IR");

    public static bool Enabled => !string.IsNullOrEmpty(Directory);

    private static readonly object s_lock = new object();
    private static readonly Dictionary<string, int> s_counters = new Dictionary<string, int>();

    /// <summary>
    /// 单元里下一个形式的序号（按 (一侧, 单元) 计数，0 起）。eval 的单元（eval$N）统一叫 eval：
    /// 一个进程里依次求值的形式按顺序编号，两个编译器对同一次运行给出相同的序号。
    /// </summary>
    public static int NextIndex(string side, string unit)
    {
        lock (s_lock)
        {
            string key = side + "\n" + UnitName(unit);
            s_counters.TryGetValue(key, out int n);
            s_counters[key] = n + 1;
            return n;
        }
    }

    [ThreadStatic] private static Stack<int> t_pending;

    /// <summary>
    /// 驱动在编译一个形式之前压入它的序号（eval-form 进入时取的，两侧共用），编译器的前端取走（<see cref="TakeIndex"/>）：
    /// 宏展开里嵌套的 eval 先编译，序号仍按进入的顺序。
    /// </summary>
    public static void PushIndex(int ix) => (t_pending ??= new Stack<int>()).Push(ix);

    /// <summary>
    /// 前端取本形式的序号：驱动压入的，没有时按单元计数（AOT 单元的最终编译）。不经 eval-form 的 eval 单元（驱动内部的编译）
    /// 从 1000000 起编号，不与形式的序号相撞。
    /// </summary>
    public static int TakeIndex(string side, string unit)
    {
        if (t_pending != null && t_pending.Count > 0) return t_pending.Pop();
        int n = NextIndex(side, unit);
        return UnitName(unit).EndsWith("eval", StringComparison.Ordinal) ? 1000000 + n : n;
    }

    /// <summary>
    /// 影子模式（CLEAT_COMPILER=shadow，designs/19 D5）：驱动对每个顶层形式在 Scheme 展开之前调用，Clojure 编译器按已实现的阶段
    /// 处理同一个形式并转储（cleat.compiler.shadow/shadow-form）。异常不影响 Scheme 的编译，记成该形式的 error 阶段。
    /// </summary>
    public static readonly bool ShadowEnabled = Environment.GetEnvironmentVariable("CLEAT_COMPILER") == "shadow";

    private static IFn s_shadow;

    /// <summary>
    /// 宿主启动时（Clojure 镜像与编译器包登记之后、执行任何形式之前）加载影子模式的命名空间：放到第一个形式时才 require，
    /// 被它拉起的源码加载（构建镜像时 clojure.string 等从源码重新求值）会插进形式序列，两侧的序号就对不上了。
    /// </summary>
    public static void InitShadow()
    {
        if (!(Enabled && ShadowEnabled) || s_shadow != null) return;
        clojure.lang.RT.var("clojure.core", "require").invoke(clojure.lang.Symbol.intern("cleat.compiler.shadow"));
        s_shadow = clojure.lang.RT.var("cleat.compiler.shadow", "shadow-form");
    }

    public static void Shadow(object form, int ix)
    {
        try
        {
            if (s_shadow == null) return;   // 没有 Clojure 镜像（--no-boot）时影子模式不工作
            s_shadow.invoke(form, (long)ix);
        }
        catch (Exception e)
        {
            WriteError("clojure", "eval", ix, e);
        }
    }

    /// <summary>形式在展开 / 分析时出错：记成 error 阶段（异常类型与消息），两侧的诊断文本一起比较（cleat-086）。</summary>
    public static void WriteError(string side, string unit, int ix, Exception e)
    {
        while (e is System.Reflection.TargetInvocationException && e.InnerException != null) e = e.InnerException;
        Write(side, unit, ix, "error", PrintDatum(e.GetType().Name + ": " + e.Message));
    }

    /// <summary>单元名的前缀（CLEAT_DUMP_IR_TAG）：同一个目录收集几个进程的转储时区分各进程的 eval 单元。</summary>
    private static readonly string s_tag = Environment.GetEnvironmentVariable("CLEAT_DUMP_IR_TAG");

    public static string UnitName(string unit)
    {
        string u = unit != null && unit.StartsWith("eval$", StringComparison.Ordinal) ? "eval" : unit ?? "unit";
        return string.IsNullOrEmpty(s_tag) ? u : s_tag + "." + u;
    }

    /// <summary>写一条记录：<c>;; 序号 阶段</c> 一行，后面是文本。</summary>
    public static void Write(string side, string unit, int index, string stage, string text)
    {
        if (!Enabled) return;
        lock (s_lock)
        {
            string dir = Path.Combine(Directory, side);
            System.IO.Directory.CreateDirectory(dir);
            string file = Path.Combine(dir, Sanitize(UnitName(unit)) + ".ir");
            File.AppendAllText(file, ";; " + index.ToString(CultureInfo.InvariantCulture) + " " + stage + "\n" + text + "\n");
        }
    }

    private static string Sanitize(string s)
    {
        var sb = new StringBuilder();
        foreach (char c in s) sb.Append(char.IsLetterOrDigit(c) || c is '.' or '-' or '_' ? c : '_');
        return sb.ToString();
    }

    /// <summary>Scheme 编译器的 IR（unparse 的结果：Scheme 列表，标识符是 Scheme 符号）→ 规范文本。</summary>
    public static string PrintSchemeIr(object x)
    {
        var sb = new StringBuilder();
        Ir(sb, x);
        return sb.ToString();
    }

    // L0 ~ L3 里数据位置（datum / name / flag 终结符）的实参下标（关键字之后 0 起）；其余位置是 IR 或标识符。
    // 与 Clojure 一侧按语言描述打印的规则相同：symbol 终结符为裸标识符，其余终结符为 PrintDatum（cleat.compiler.dump）
    private static readonly Dictionary<string, int[]> s_datumArgs = new Dictionary<string, int[]>
    {
        ["quote"] = new[] { 0 }, ["lambda"] = new[] { 0 }, ["library"] = new[] { 0 },
        ["host-call"] = new[] { 0, 1, 2 }, ["host-field-get"] = new[] { 0, 1, 2 }, ["host-field-set"] = new[] { 0, 1, 2 },
        ["host-new"] = new[] { 0 }, ["host-new-array"] = new[] { 0 }, ["host-cast"] = new[] { 0 }, ["host-is"] = new[] { 0 },
        ["host-typeof"] = new[] { 0 }, ["host-using"] = new[] { 0 }, ["host-reference"] = new[] { 0 }, ["unsupported"] = new[] { 0 },
        ["clj-var-ref"] = new[] { 0, 1 }, ["clj-var-set!"] = new[] { 0 }, ["clj-def"] = new[] { 0, 1, 2 },
        ["clj-keyword-invoke"] = new[] { 0 }, ["clj-call"] = new[] { 0, 1 }, ["clj-static-call"] = new[] { 0, 1 },
        ["clj-case"] = new[] { 0 }, ["clj-deftype"] = new[] { 0 },
    };

    private static void Ir(StringBuilder sb, object x)
    {
        switch (x)
        {
            case Cons c when c.car is Symbol head && (s_datumArgs.ContainsKey(head.Name) || head.Name == "try-catch" || head.Name == "clause"):
                {
                    s_datumArgs.TryGetValue(head.Name, out int[] datums);
                    sb.Append('(').Append(head.Name);
                    int i = 0;
                    for (object p = c.cdr; p is Cons pc; p = pc.cdr, i++)
                    {
                        sb.Append(' ');
                        if (datums != null && Array.IndexOf(datums, i) >= 0) Datum(sb, pc.car);
                        else if (head.Name == "try-catch" && i > 0 && pc.car is Cons pair && pair.cdr is Cons rest)
                        {
                            // (try-catch e (d e) …)：每个处理器的 d 是数据
                            sb.Append('(');
                            Datum(sb, pair.car);
                            sb.Append(' ');
                            Ir(sb, rest.car);
                            sb.Append(')');
                        }
                        else if (head.Name == "clause" && i == 1) { if (pc.car is Symbol rs) Identifier(sb, rs.Name); else Datum(sb, pc.car); }   // (maybe x)
                        else Ir(sb, pc.car);
                    }
                    sb.Append(')');
                    break;
                }
            case Cons c:
                {
                    sb.Append('(');
                    bool first = true;
                    object p = c;
                    for (; p is Cons pc; p = pc.cdr)
                    {
                        if (!first) sb.Append(' ');
                        Ir(sb, pc.car);
                        first = false;
                    }
                    if (p is not EmptyList) { sb.Append(" . "); Ir(sb, p); }
                    sb.Append(')');
                    break;
                }
            case EmptyList:
                sb.Append("()");
                break;
            case Symbol s:
                Identifier(sb, s.Name);
                break;
            default:
                Datum(sb, x);
                break;
        }
    }

    /// <summary>IR 里的标识符：能读回成 Clojure 符号的原样，否则 #cleat/id "名字"。</summary>
    public static void Identifier(StringBuilder sb, string name)
    {
        if (IsPlainSymbol(name)) sb.Append(name);
        else { sb.Append("#cleat/id "); Str(sb, name); }
    }

    public static string IdentifierText(string name)
    {
        var sb = new StringBuilder();
        Identifier(sb, name);
        return sb.ToString();
    }

    private static bool IsPlainSymbol(string s)
    {
        if (string.IsNullOrEmpty(s) || s is "nil" or "true" or "false") return false;
        if (char.IsDigit(s[0]) || ((s[0] is '+' or '-' or '.') && s.Length > 1 && char.IsDigit(s[1]))) return false;
        if (s[0] is ':' or '#' or '\'') return false;
        foreach (char c in s)
            if (!(char.IsLetterOrDigit(c) || "*+!-_?<>=$&%.#'".IndexOf(c) >= 0)) return false;
        return true;
    }

    /// <summary>数据位置的值 → 规范文本。</summary>
    public static string PrintDatum(object x)
    {
        var sb = new StringBuilder();
        Datum(sb, x);
        return sb.ToString();
    }

    private static void Datum(StringBuilder sb, object x)
    {
        switch (x)
        {
            case null: sb.Append("nil"); break;
            case bool b: sb.Append(b ? "true" : "false"); break;
            case string s: Str(sb, s); break;
            case SchemeString ss: Str(sb, ss.ToString()); break;
            case char c: sb.Append("#cleat/char ").Append(((int)c).ToString(CultureInfo.InvariantCulture)); break;
            case int or long or short or byte or sbyte or uint or ushort or ulong:
                sb.Append(Convert.ToString(x, CultureInfo.InvariantCulture)); break;
            case System.Numerics.BigInteger bi: sb.Append(bi.ToString(CultureInfo.InvariantCulture)).Append('N'); break;
            case BigInt cbi: sb.Append(cbi.ToString()).Append('N'); break;
            case double d: sb.Append("#cleat/double \"").Append(d.ToString("R", CultureInfo.InvariantCulture)).Append('"'); break;
            case float f: sb.Append("#cleat/float \"").Append(f.ToString("R", CultureInfo.InvariantCulture)).Append('"'); break;
            case decimal m: sb.Append(m.ToString(CultureInfo.InvariantCulture)).Append('M'); break;
            // —— Scheme 的值 ——
            case Symbol s: sb.Append("#cleat/ssym "); Str(sb, s.Name); break;
            case EmptyList: sb.Append("#cleat/slist ()"); break;
            case Cons c:
                {
                    sb.Append("#cleat/slist (");
                    bool first = true;
                    object p = c;
                    for (; p is Cons pc; p = pc.cdr)
                    {
                        if (!first) sb.Append(' ');
                        Datum(sb, pc.car);
                        first = false;
                    }
                    if (p is not EmptyList) { sb.Append(" #cleat/dot "); Datum(sb, p); }
                    sb.Append(')');
                    break;
                }
            case object[] v:
                sb.Append("#cleat/svec [");
                for (int i = 0; i < v.Length; i++) { if (i > 0) sb.Append(' '); Datum(sb, v[i]); }
                sb.Append(']');
                break;
            case Record r when r.Rtd != null:
                sb.Append("#cleat/record [");
                Str(sb, r.Rtd.Name);
                foreach (var f in r.Fields) { sb.Append(' '); Datum(sb, f); }
                sb.Append(']');
                break;
            // —— Clojure 的值 ——
            case clojure.lang.Symbol cs: sb.Append("#cleat/sym "); Str(sb, cs.ToString()); break;
            case Keyword k: sb.Append("#cleat/kw "); Str(sb, k.ToString().Substring(1)); break;
            case Var v: sb.Append("#cleat/var "); Str(sb, v.Namespace.Name + "/" + v.Symbol.Name); break;
            case Type t: sb.Append("#cleat/type "); Str(sb, t.FullName ?? t.Name); break;
            case Namespace ns: sb.Append("#cleat/ns "); Str(sb, ns.Name.ToString()); break;
            case IPersistentMap m:
                WithMeta(sb, m, () =>
                {
                    sb.Append("#cleat/map [");
                    bool first = true;
                    for (ISeq q = clojure.lang.RT.seq(m); q != null; q = q.next())
                    {
                        var e = (IMapEntry)q.first();
                        if (!first) sb.Append(' ');
                        Datum(sb, e.key()); sb.Append(' '); Datum(sb, e.val());
                        first = false;
                    }
                    sb.Append(']');
                });
                break;
            case IPersistentVector pv: WithMeta(sb, pv, () => Coll(sb, "#cleat/vec [", pv)); break;
            case IPersistentSet ps: WithMeta(sb, ps, () => Coll(sb, "#cleat/set [", ps)); break;
            case IPersistentList or ISeq: WithMeta(sb, x, () => Coll(sb, "#cleat/list [", x)); break;
            case System.Text.RegularExpressions.Regex re: sb.Append("#cleat/regex "); Str(sb, re.ToString()); break;
            default:
                sb.Append("#cleat/object [");
                Str(sb, x.GetType().FullName);
                sb.Append(' ');
                Str(sb, x.ToString());
                sb.Append(']');
                break;
        }
    }

    /// <summary>带非空元数据的集合：#cleat/meta [元数据 值]。</summary>
    private static void WithMeta(StringBuilder sb, object x, Action body)
    {
        if (x is IMeta im && im.meta() is IPersistentMap m && m.count() > 0)
        {
            sb.Append("#cleat/meta [");
            Datum(sb, m);
            sb.Append(' ');
            body();
            sb.Append(']');
        }
        else body();
    }

    private static void Coll(StringBuilder sb, string open, object coll)
    {
        sb.Append(open);
        bool first = true;
        for (ISeq q = clojure.lang.RT.seq(coll); q != null; q = q.next())
        {
            if (!first) sb.Append(' ');
            Datum(sb, q.first());
            first = false;
        }
        sb.Append(']');
    }

    private static void Str(StringBuilder sb, string s)
    {
        sb.Append('"');
        foreach (char c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < ' ' || c > '~') sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    else sb.Append(c);
                    break;
            }
        }
        sb.Append('"');
    }
}
