#region License
/* Copyright (c) 2007-2016 Llewellyn Pritchard
 * All rights reserved.
 * This source code is subject to terms and conditions of the BSD License.
 * See docs/license.txt. */
#endregion

using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace Cleat.Scheme;

public class Exceptions : Builtins
{
    /// <summary>
    /// 捕获任意 .NET 异常交给 handler；续延控制流（<see cref="ContinuationException"/>）必须放行。
    /// </summary>
    /// <summary>
    /// ($raise-clr-exception e)：with-exception-handler 在自己的帧里接住回卷上来的 .NET 异常（C# 原语、throw 等未经宿主调用边界的）
    /// 后调用，包成 &amp;clr-exception 条件在当前处理器下 raise（cleat-058，skeg 13 §5）。续延逃逸与 SchemeException 原样重抛。
    /// </summary>
    [Primitive("$raise-clr-exception", Flags = PrimitiveFlags.Unsafe)]
    public static object RaiseClrException(object e) => Errors.RaiseClrException((Exception)e);

    [Primitive("with-clr-exception-handler")]
    public static object WithClrExceptionHandler(object handler, object thunk)
    {
        Procedure h = RequiresNotNull<Procedure>(handler);
        Procedure t = RequiresNotNull<Procedure>(thunk);
        try
        {
            return t.Call();
        }
        catch (ContinuationException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return h.Call(ex);
        }
    }


    /// <summary>当前 .NET 调用栈的文本行（供 Scheme 的 stacktrace 条件使用）。</summary>
    public static object GetStackTrace()
    {
        var sf = new StackTrace(2, true);
        List<string> newst = new List<string>();
        var sfs = sf.ToString().Split(Environment.NewLine.ToCharArray(), StringSplitOptions.RemoveEmptyEntries);

        foreach (var line in sfs)
        {
            newst.Add(line.Replace("   at ", string.Empty).Replace("System.Object ", string.Empty).Replace("Object ", string.Empty));
        }

        return newst.ToArray();
    }
}

