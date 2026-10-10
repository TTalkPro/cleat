using System;
using System.Collections.Concurrent;
using System.Text;
using System.Threading;

namespace Cleat.Scheme;

/// <summary>
/// 符号：按名字驻留，带值槽。全局变量与库绑定的值存放在（位置）符号的 Value 中。
/// 设计：designs/02-runtime.md §3。
/// </summary>
public sealed class Symbol
{
    public readonly string Name;

    /// <summary>值槽。未绑定时为 <see cref="Unbound.Instance"/>。</summary>
    public object Value = Unbound.Instance;

    private readonly int _hash;

    private Symbol(string name)
    {
        Name = name;
        // 稳定的 FNV-1a 哈希：符号按名字驻留，Equals 仍是引用相等；哈希值只取决于名字，
        // eq 哈希表按符号的遍历顺序因此与对象分配顺序无关（iron-030：自举产物不随宿主变化）
        unchecked
        {
            uint h = 2166136261;
            foreach (char c in name) { h ^= c; h *= 16777619; }
            _hash = (int)h;
        }
    }

    public override int GetHashCode() => _hash;

    public override bool Equals(object obj) => ReferenceEquals(this, obj);

    public override string ToString() => Name;

    public bool IsBound => !(Value is Unbound);

    private static readonly ConcurrentDictionary<string, Symbol> s_table = new ConcurrentDictionary<string, Symbol>(StringComparer.Ordinal);

    /// <summary>按名字驻留：同名总是返回同一对象。gensym 也按名字驻留，因此从 dll 重新加载时得到同一个对象。</summary>
    public static Symbol Intern(string name)
    {
        if (name == null) throw new ArgumentNullException(nameof(name));
        return s_table.TryGetValue(name, out var s) ? s : s_table.GetOrAdd(name, n => new Symbol(n));
    }

    #region gensym

    // 名字格式沿用旧运行时：g$<名字>$<计数>$<TICKS>，匿名为 g$gen<计数>$<TICKS>。
    // TICKS 由进程启动时间派生，保证不同进程生成的 gensym 不冲突。
    //
    // 构建模式（RuntimeOptions.DeterministicGensym，iron-014）：TICKS 换成当前 gensym 作用域名，
    // 计数在作用域内从 1 开始，同一输入两次展开得到相同名字。展开器展开库时进入以库名为名的作用域
    // （如 rnrs.base），作用域外为 %top；文本种子中的 gensym 后缀为 %seed（iron-015）。

    private static int s_anonCount, s_namedCount;

    /// <summary>构建模式下的 gensym 作用域：名字与计数。</summary>
    public sealed class GensymScope
    {
        public readonly string Name;
        internal int _count;
        internal GensymScope(string name) { Name = name; }
        public override string ToString() => "#<gensym-scope " + Name + " " + _count + ">";
    }

    // 作用域外的 gensym 用的"顶层"作用域，按领域区分（见 GensymRealm）
    private static readonly System.Collections.Generic.Dictionary<string, GensymScope> s_topScopes = new System.Collections.Generic.Dictionary<string, GensymScope>();
    private static GensymScope s_scope;

    /// <summary>当前作用域；未进入任何作用域时为 null。</summary>
    public static GensymScope CurrentGensymScope => s_scope;

    /// <summary>
    /// 作用域名的前缀（"领域"）。同一进程中同一个库可能被展开两次（构建脚本在 bootstrap 库集合中再展开一遍
    /// 运行中系统已加载的库），用不同领域区分，避免确定性 gensym 同名（iron-028）。
    /// </summary>
    public static string GensymRealm = "";

    /// <summary>进入新作用域（计数从 0 开始）。名字中的 '$' 换成 '_'，以免干扰 ungensym。</summary>
    public static void EnterGensymScope(string name) => s_scope = new GensymScope((GensymRealm + name).Replace('$', '_'));

    /// <summary>恢复先前保存的作用域（可为 null）。</summary>
    public static void RestoreGensymScope(GensymScope saved) => s_scope = saved;

    private static string NextDeterministic(out int n)
    {
        var s = s_scope;
        if (s == null)
            lock (s_topScopes)
                if (!s_topScopes.TryGetValue(GensymRealm, out s)) s_topScopes[GensymRealm] = s = new GensymScope((GensymRealm + "%top").Replace('$', '_'));
        n = ++s._count;
        return s.Name;
    }

    private static readonly char[] s_packChars = "0123456789abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ@=".ToCharArray();

    private static string PackInt(int i)
    {
        var sb = new StringBuilder();
        do { sb.Insert(0, s_packChars[i % 64]); i /= 64; } while (i > 0);
        return sb.ToString();
    }

    /// <summary>gensym 名字的进程唯一后缀。</summary>
    public static readonly string TickString = PackInt((int)((DateTime.Now.Ticks >> 16) & 0x7FFFFFFF));

    public static Symbol Gensym()
    {
        if (RuntimeOptions.DeterministicGensym)
        {
            string sc = NextDeterministic(out int n);
            return Intern("g$gen" + n + "$" + sc);
        }
        return Intern("g$gen" + Interlocked.Increment(ref s_anonCount) + "$" + TickString);
    }

    public static Symbol Gensym(string name)
    {
        if (RuntimeOptions.DeterministicGensym)
        {
            string sc = NextDeterministic(out int n);
            return Intern("g$" + name + "$" + n + "$" + sc);
        }
        return Intern("g$" + name + "$" + Interlocked.Increment(ref s_namedCount) + "$" + TickString);
    }

    /// <summary>gensym 去掉装饰后的名字：第一个与第二个 '$' 之间的部分；不是 gensym 时返回原名。</summary>
    public static string UngensymName(string name)
    {
        int start = name.IndexOf('$') + 1;
        if (start > 0)
        {
            int end = name.IndexOf('$', start);
            if (end > start) return name.Substring(start, end - start);
        }
        return name;
    }

    public Symbol Ungensym()
    {
        string n = UngensymName(Name);
        return ReferenceEquals(n, Name) ? this : Intern(n);
    }

    #endregion
}
