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

    public PrimitiveAttribute(string name) { Name = name; }
}
