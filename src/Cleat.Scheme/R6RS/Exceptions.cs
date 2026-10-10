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
    /// 捕获任意 .NET 异常交给 handler（异常对象本身，不转成条件）。Scheme 条件以 <see cref="SchemeException"/> 传播，
    /// 按类型捕获用核心形式 $try/catch（cleat-047）。
    /// </summary>
    [Primitive("with-clr-exception-handler")]
    public static object WithClrExceptionHandler(object handler, object thunk)
    {
        Procedure h = RequiresNotNull<Procedure>(handler);
        Procedure t = RequiresNotNull<Procedure>(thunk);
        try
        {
            return t.Call();
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

