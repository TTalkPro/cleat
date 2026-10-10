using System;

namespace Cleat.Scheme;

/// <summary>
/// Scheme 条件以 .NET 异常形式传播。由 Scheme 的默认异常处理器抛出
/// （boot/exceptions.sls：<c>($throw (clr-new SchemeException condition))</c>）。
/// </summary>
public sealed class SchemeException : Exception
{
    public object Condition { get; }

    public SchemeException(object condition) : base("Scheme condition raised")
    {
        Condition = condition;
    }

    /// <summary>条件的可读形式由宿主用 Scheme 打印器生成（iron-029）；此处只给出简单形式。</summary>
    public override string ToString() => "SchemeException: " + (Condition?.ToString() ?? "()");
}

/// <summary>顶层程序以未处理的条件结束（boot/expander/main.sls 的 load/program），宿主据此返回非零退出码。</summary>
public sealed class ProgramFailedException : Exception
{
}
