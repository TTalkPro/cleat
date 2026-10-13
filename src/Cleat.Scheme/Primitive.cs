using System;

namespace Cleat.Scheme;

[Flags]
public enum PrimitiveFlags
{
    None = 0,
    /// <summary>无副作用，参数为常量时编译器可以折叠。</summary>
    Pure = 1,
    /// <summary>不做类型检查（$ 开头的原语）。</summary>
    Unsafe = 2,
    /// <summary>可以在尾位置以 tail. 调用。</summary>
    AllowTailCall = 4,
    // 以下四个照 Chez cmacros.ss 的 prim-mask 注释（iron-069）：
    /// <summary>参数全为常量时可在编译期按机器无关的方式折叠。不蕴含 Pure（car 对常量表可折叠但不 pure），
    /// Pure 也不蕴含它（结果依赖机器特征的不折叠）。折叠的实现在编译器里（compiler/primdata.sls），不是调用原语本身。</summary>
    Mifoldable = 8,
    /// <summary>结果可以丢弃：没有副作用，在 effect 上下文（begin 的中间项）可以整个删掉。
    /// 只有同时 Unrestricted（任何参数都不报错）才真的删，否则参数错误的报错也是可观察的。</summary>
    Discard = 16,
    /// <summary>对任何参数都不报错（谓词、分配器）。</summary>
    Unrestricted = 32,
    /// <summary>参数类型已经确定正确时不需要再检查（内联模板可以省掉 isinst）。</summary>
    SafeOnGoodArgs = 64,
}

/// <summary>
/// 标注 C# 实现的 Scheme 原语。启动时由原语登记表写入名字符号的值槽（iron-008），
/// 并由 PrimGen 生成编译器使用的原语表（iron-012）。设计：designs/02-runtime.md §5。
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
public sealed class PrimitiveAttribute : Attribute
{
    public string Name { get; }
    public PrimitiveFlags Flags { get; set; }
    /// <summary>编译器内联 IL 模板名（若有）。</summary>
    public string Inline { get; set; }
    /// <summary>
    /// 类型签名（iron-069，照 Chez primdata.ss 的 sig）："(参数类型 …) -> (结果类型 …)"，参数类型表末尾可以是 "..."。
    /// 类型名：ptr（任意）、fixnum、flonum、number、integer、boolean、char、string、symbol、pair、list、vector、bytevector、
    /// procedure、void。结果类型是正常返回时保证成立的类型（参数不对时报错不算）。
    /// </summary>
    public string Sig { get; set; }

    public PrimitiveAttribute(string name) { Name = name; }
}
