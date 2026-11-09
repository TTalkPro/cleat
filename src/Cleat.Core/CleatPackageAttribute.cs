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

/// <summary>
/// 程序集级属性：编这个包程序集时运行的 Clojure boot 镜像 id（cleat-186）。宿主登记镜像旁的随附库 / tools 前按元数据读它（不加载程序集），
/// 与正在运行的镜像不同就不用（入口里的 <c>CheckBootImage</c> 是同一个 id，用到时才报 StaleModuleException）。
/// </summary>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = false)]
public sealed class CleatBootImageAttribute : Attribute
{
    public string ImageId { get; }

    public CleatBootImageAttribute(string imageId)
    {
        ImageId = imageId;
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
