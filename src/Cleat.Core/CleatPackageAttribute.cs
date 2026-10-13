using System;

namespace Cleat.Core;

/// <summary>程序集级属性：包程序集的包名（designs/17 §2.1）。</summary>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = false)]
public sealed class CleatPackageAttribute : Attribute
{
    public string Name { get; }

    public CleatPackageAttribute(string name)
    {
        Name = name;
    }
}

/// <summary>程序集级属性：包程序集的命名空间索引，命名空间 → 入口 public static object 方法()（designs/17 §2.1）。</summary>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
public sealed class CleatNamespaceAttribute : Attribute
{
    public string Namespace { get; }
    public string EntryType { get; }
    public string EntryMethod { get; }

    public CleatNamespaceAttribute(string ns, string entryType, string entryMethod)
    {
        Namespace = ns;
        EntryType = entryType;
        EntryMethod = entryMethod;
    }
}
