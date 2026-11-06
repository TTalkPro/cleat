using System;
using System.Collections.Immutable;

namespace Cleat.Compiler.Ir;

/// <summary>
/// 中间语言（designs/30 §2 D2）。各层共用一个节点层次，节点用 <see cref="NodeAttribute"/> 标出属于哪些层；
/// 层的差异 = 节点集合的差异 + <see cref="IrValidator"/> 检查的附加不变量。
/// </summary>
[Flags]
public enum Langs
{
    None = 0,
    /// <summary>分析器的输出：宏已展开、符号已决议。</summary>
    L0 = 1,
    /// <summary>simplify-letrec 之后：letrec 只绑定 lambda。</summary>
    L1 = 2,
    /// <summary>convert-assignments 之后：被捕获又被赋值的局部是 Box。</summary>
    L2 = 4,
    /// <summary>uncover-free 之后：lambda 带自由变量表。</summary>
    L3 = 8,
    /// <summary>表示层（cleat-174 起）。</summary>
    L4 = 16,
    /// <summary>类层（cleat-174 起）。</summary>
    L5 = 32,

    L0ToL3 = L0 | L1 | L2 | L3,
    L2ToL3 = L2 | L3,
}

/// <summary>标出 IR 节点类与它所属的层；Source Generator（Cleat.Compiler.Generators）据此生成样板代码。</summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class NodeAttribute : Attribute
{
    public NodeAttribute(Langs langs) { Langs = langs; }
    public Langs Langs { get; }
}

/// <summary>源位置（对应 Clojure 编译器节点 meta 的 <c>:pos [line column file]</c>）。Line 为 0 表示没有；Column 为 0 表示未知。</summary>
public readonly record struct SourcePos(int Line, int Column, string File)
{
    public bool IsKnown => Line > 0;
}

/// <summary>
/// IR 节点。字段公开只读，构造器、<c>Update</c>、访问方法由 Source Generator 生成；只有源位置可以在构造后设置
/// （对应 Clojure 编译器的 set-node-meta!）。节点的相等是引用相等（按节点身份做键的表用 ReferenceEqualityComparer）。
/// </summary>
public abstract class Node
{
    public SourcePos Pos { get; set; }

    public abstract NodeKind Kind { get; }

    /// <summary>本节点种类所属的层。</summary>
    public abstract Langs Langs { get; }

    internal abstract Node AcceptRewriter(Rewriter r);
    internal abstract void AcceptWalker(Walker w);

    /// <summary>把本节点的源位置复制给 n（重写建新节点时保留位置，对应 propagate-src-meta!），返回 n。</summary>
    protected T CopyPosTo<T>(T n) where T : Node
    {
        if (!n.Pos.IsKnown) n.Pos = Pos;
        return n;
    }

    public override string ToString() => IrPrinter.ToText(this);
}

/// <summary>表达式（L0 ~ L3 的 Expr 非终结符）。</summary>
public abstract class Expr : Node
{
}

/// <summary>
/// 重写的基类：每种节点一个虚方法（生成），缺省递归子节点，子节点都没变时返回原节点（结构共享）。
/// pass 只覆盖它关心的节点。
/// </summary>
public abstract partial class Rewriter
{
    public T Rewrite<T>(T n) where T : Node
    {
        if (n is null) return null;
        var r = n.AcceptRewriter(this);
        return r as T ?? throw new InvalidOperationException($"{GetType().Name}: rewrote a {n.Kind} into {r?.Kind.ToString() ?? "null"}, expected {typeof(T).Name}");
    }

    public ImmutableArray<T> RewriteList<T>(ImmutableArray<T> xs) where T : Node
    {
        if (xs.IsDefault) return xs;
        T[] changed = null;
        for (int i = 0; i < xs.Length; i++)
        {
            var r = Rewrite(xs[i]);
            if (changed is null && !ReferenceEquals(r, xs[i]))
            {
                changed = new T[xs.Length];
                for (int j = 0; j < i; j++) changed[j] = xs[j];
            }
            if (changed != null) changed[i] = r;
        }
        return changed is null ? xs : ImmutableArray.Create(changed);
    }
}

/// <summary>只读遍历的基类：每种节点一个虚方法（生成），缺省依次遍历子节点。</summary>
public abstract partial class Walker
{
    /// <summary>遍历一个节点（可覆盖：在每个节点上做统一的检查，再调 base.Walk 继续）。</summary>
    public virtual void Walk(Node n) => n?.AcceptWalker(this);

    public void WalkList<T>(ImmutableArray<T> xs) where T : Node
    {
        if (xs.IsDefault) return;
        foreach (var x in xs) Walk(x);
    }
}
