using System;

namespace Cleat.Core;

/// <summary>程序集级属性：发射器按 Lcil 的 (entry …) 写入，加载器据此找到入口 public static object 方法()（designs/05 §6）。</summary>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = false)]
public sealed class CleatModuleAttribute : Attribute
{
    public string EntryType { get; }
    public string EntryMethod { get; }

    public CleatModuleAttribute(string entryType, string entryMethod)
    {
        EntryType = entryType;
        EntryMethod = entryMethod;
    }
}
