using System.Collections.Immutable;
using clojure.lang;

namespace Cleat.Compiler.Ir;

// L0 ~ L3 的节点（designs/30 §3.1）：与 Clojure 编译器 languages.clj 的产生式一一对应，S 表达式形状见各节点注释。
// 字段的含义尚未在 C# 侧使用的（clj-def / clj-call 等的数据字段），沿用产生式的位置命名，由 cleat-172 起按语义细化。
// 终结符：x 为 Symbol；d（datum）为任意 Clojure 值；nm 为 Symbol / 字符串 / nil / false / FnName；st（flag）为 bool。

/// <summary><c>x</c>：词法变量引用。</summary>
[Node(Langs.L0ToL3)]
public sealed partial class Ref : Expr
{
    public readonly Symbol Name;
}

/// <summary><c>(quote d)</c></summary>
[Node(Langs.L0ToL3)]
public sealed partial class Quote : Expr
{
    public readonly object Datum;
}

/// <summary><c>(if e0 e1 e2)</c></summary>
[Node(Langs.L0ToL3)]
public sealed partial class If : Expr
{
    public readonly Expr Test;
    public readonly Expr Then;
    public readonly Expr Else;
}

/// <summary><c>(begin e* ... e)</c>：至少一个表达式，最后一个的值是结果。</summary>
[Node(Langs.L0ToL3)]
public sealed partial class Begin : Expr
{
    public readonly ImmutableArray<Expr> Body;
}

/// <summary><c>(set! x e)</c></summary>
[Node(Langs.L0ToL3)]
public sealed partial class Set : Expr
{
    public readonly Symbol Name;
    public readonly Expr Value;
}

/// <summary>
/// <c>(lambda nm cl* ...)</c>（L0 ~ L2）/ <c>(lambda nm (x* ...) cl* ...)</c>（L3）。<see cref="Free"/> 在 L3 之前是 default，
/// L3 起是自由变量表（可以为空表）。
/// </summary>
[Node(Langs.L0ToL3)]
public sealed partial class Lambda : Expr
{
    public readonly object Name;
    public readonly ImmutableArray<Symbol> Free;
    public readonly ImmutableArray<Clause> Clauses;
}

/// <summary><c>(clause (x* ...) (maybe x) body)</c>：<see cref="Rest"/> 为 null 表示没有变长参数（S 表达式里写 false）。</summary>
[Node(Langs.L0ToL3)]
public sealed partial class Clause : Node
{
    public readonly ImmutableArray<Symbol> Params;
    public readonly Symbol Rest;
    public readonly Expr Body;
}

/// <summary><c>(letrec ([x* e*] ...) body)</c>；L1 起每个 e 都是 lambda。</summary>
[Node(Langs.L0ToL3)]
public sealed partial class Letrec : Expr
{
    public readonly ImmutableArray<Symbol> Vars;
    public readonly ImmutableArray<Expr> Inits;
    public readonly Expr Body;
}

/// <summary><c>(let ([x* e*] ...) body)</c></summary>
[Node(Langs.L0ToL3)]
public sealed partial class Let : Expr
{
    public readonly ImmutableArray<Symbol> Vars;
    public readonly ImmutableArray<Expr> Inits;
    public readonly Expr Body;
}

/// <summary><c>(call e e* ...)</c></summary>
[Node(Langs.L0ToL3)]
public sealed partial class Call : Expr
{
    public readonly Expr Fn;
    public readonly ImmutableArray<Expr> Args;
}

/// <summary><c>(throw e)</c></summary>
[Node(Langs.L0ToL3)]
public sealed partial class Throw : Expr
{
    public readonly Expr Value;
}

/// <summary><c>(try-finally e0 e1)</c></summary>
[Node(Langs.L0ToL3)]
public sealed partial class TryFinally : Expr
{
    public readonly Expr Body;
    public readonly Expr Finally;
}

/// <summary><c>(try-catch e (d* e*) ...)</c>：每个处理器是 (异常类型 处理器表达式)。</summary>
[Node(Langs.L0ToL3)]
public sealed partial class TryCatch : Expr
{
    public readonly Expr Body;
    public readonly ImmutableArray<object> Types;
    public readonly ImmutableArray<Expr> Handlers;
}

/// <summary><c>(label x (x* ...) body0 body1)</c>：lower-loops 产生的循环标签。</summary>
[Node(Langs.L0ToL3)]
public sealed partial class Label : Expr
{
    public readonly Symbol Name;
    public readonly ImmutableArray<Symbol> Params;
    public readonly Expr Body0;
    public readonly Expr Body1;
}

/// <summary><c>(jump x e* ...)</c></summary>
[Node(Langs.L0ToL3)]
public sealed partial class Jump : Expr
{
    public readonly Symbol Target;
    public readonly ImmutableArray<Expr> Args;
}

/// <summary><c>(uninitialized)</c></summary>
[Node(Langs.L0ToL3)]
public sealed partial class Uninitialized : Expr
{
}

/// <summary><c>(check-initialized e)</c></summary>
[Node(Langs.L0ToL3)]
public sealed partial class CheckInitialized : Expr
{
    public readonly Expr Value;
}

/// <summary><c>(clj-var-ref d st)</c>：d 是 Var。</summary>
[Node(Langs.L0ToL3)]
public sealed partial class CljVarRef : Expr
{
    public readonly object Var;
    public readonly bool Flag;
}

/// <summary><c>(clj-var-set! d e)</c></summary>
[Node(Langs.L0ToL3)]
public sealed partial class CljVarSet : Expr
{
    public readonly object Var;
    public readonly Expr Value;
}

/// <summary><c>(clj-def d st0 st1 e0 e1)</c></summary>
[Node(Langs.L0ToL3)]
public sealed partial class CljDef : Expr
{
    public readonly object Datum;
    public readonly bool Flag0;
    public readonly bool Flag1;
    public readonly Expr Expr0;
    public readonly Expr Expr1;
}

/// <summary><c>(clj-invoke e e* ...)</c>：按 IFn 调用。</summary>
[Node(Langs.L0ToL3)]
public sealed partial class CljInvoke : Expr
{
    public readonly Expr Fn;
    public readonly ImmutableArray<Expr> Args;
}

/// <summary><c>(clj-keyword-invoke d e e* ...)</c>：d 是关键字。</summary>
[Node(Langs.L0ToL3)]
public sealed partial class CljKeywordInvoke : Expr
{
    public readonly object Keyword;
    public readonly Expr Target;
    public readonly ImmutableArray<Expr> Args;
}

/// <summary>
/// <c>(clj-call d0 d1 e* ...)</c>：d0 是操作名（字符串）。designs/30 §3.2 的具名节点拆分在 cleat-172 随分析器的语义一起做；
/// 拆开后的节点仍打印成这个形状（L0 的 S 表达式与 .inl 兼容）。
/// </summary>
[Node(Langs.L0ToL3)]
public sealed partial class CljCall : Expr
{
    public readonly object Op;
    public readonly object Datum;
    public readonly ImmutableArray<Expr> Args;
}

/// <summary><c>(clj-static-call d0 d1 e* ...)</c>：守卫后直接链接到 invokeStatic。</summary>
[Node(Langs.L0ToL3)]
public sealed partial class CljStaticCall : Expr
{
    public readonly object Datum0;
    public readonly object Datum1;
    public readonly ImmutableArray<Expr> Args;
}

/// <summary><c>(clj-case d e0 e1 e* ...)</c></summary>
[Node(Langs.L0ToL3)]
public sealed partial class CljCase : Expr
{
    public readonly object Datum;
    public readonly Expr Expr0;
    public readonly Expr Expr1;
    public readonly ImmutableArray<Expr> Rest;
}

/// <summary><c>(clj-deftype d (x* ...) le* ...)</c></summary>
[Node(Langs.L0ToL3)]
public sealed partial class CljDeftype : Expr
{
    public readonly object Datum;
    public readonly ImmutableArray<Symbol> Fields;
    public readonly ImmutableArray<Lambda> Methods;
}

/// <summary><c>(box e)</c>（L2 起）</summary>
[Node(Langs.L2ToL3)]
public sealed partial class Box : Expr
{
    public readonly Expr Value;
}

/// <summary><c>(unbox x)</c>（L2 起）</summary>
[Node(Langs.L2ToL3)]
public sealed partial class Unbox : Expr
{
    public readonly Symbol Name;
}

/// <summary><c>(set-box! x e)</c>（L2 起）</summary>
[Node(Langs.L2ToL3)]
public sealed partial class SetBox : Expr
{
    public readonly Symbol Name;
    public readonly Expr Value;
}
