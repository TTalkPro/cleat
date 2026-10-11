#region License
/* Copyright (c) 2007-2016 Llewellyn Pritchard
 * All rights reserved.
 * This source code is subject to terms and conditions of the BSD License.
 * See docs/license.txt. */
#endregion

using System.Collections.Generic;
namespace Cleat.Scheme;

public partial class Builtins
{
    [Primitive("not", Flags = PrimitiveFlags.Pure, Inline = "not")]
    public static object Not(object obj)
    {
        return GetBool(!RT.IsTrue(obj));
    }
}
