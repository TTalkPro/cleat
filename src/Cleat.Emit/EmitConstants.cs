using System;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;

namespace Cleat.Emit;

// 常量表（designs/05 §3；从 IronScheme 移植）：编码成 fasl、用 Deflate 压缩后作为 "$Constants" 类型的初始化数据（RVA 静态数据）嵌入程序集，
// 类型初始化器把它复制到 byte[]（RuntimeHelpers.InitializeArray），由 Cleat.Core.Runtime.Fasl.ReadPackedTable 解压读回。
// object[] 存入静态字段 c；ldconst k 即 c[k]。原先以 base64 字符串存放（UTF-16，约为原始字节的 2.7 倍，iron-037 改）。
// 不用托管资源：内存模式（eval）的程序集不能带资源；初始化数据两种模式都可用。
public sealed partial class LcilEmitter
{
    public const string ConstantsTypeName = "$Constants";

    private TypeBuilder _constantsType;
    private object[] _live;

    private void DefineConstants()
    {
        if (_types.ContainsKey(ConstantsTypeName)) throw new LcilException($"{_asm.File}: type name {ConstantsTypeName} is reserved");
        var core = LoadReference(CoreAssemblyName);
        _constantsType = _module.DefineType(ConstantsTypeName, TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed | TypeAttributes.Class);
        _constantsField = _constantsType.DefineField("c", typeof(object[]), FieldAttributes.Public | FieldAttributes.Static | FieldAttributes.InitOnly);
        _types[ConstantsTypeName] = _constantsType;              // Lcil 中可写 (ldsfld "$Constants" "c")
        _fields[(ConstantsTypeName, "c")] = (FieldBuilder)_constantsField;
        var il = _constantsType.DefineTypeInitializer().GetILGenerator();
        if (_asm.LazyStart) EmitEnsureStarted(il);     // 常量（fasl 解码）之前运行时已起来（designs/17 §4.1）
        if (_live != null)
        {
            // 内存模式的活常量：登记到 ConstantPool，类型初始化器按编号取回
            int id = (int)core.GetType("Cleat.Core.Runtime.ConstantPool", true).GetMethod("Register").Invoke(null, new object[] { _live });
            il.Emit(OpCodes.Ldc_I4, id);
            il.Emit(OpCodes.Call, core.GetType("Cleat.Core.Runtime.ConstantPool", true).GetMethod("Take"));
        }
        else
        {
            var packed = Deflate(FaslEncoder.EncodeTable(_asm.Constants, _asm.File, _asm.Runtime));
            var data = _constantsType.DefineInitializedData("data", packed, FieldAttributes.Private | FieldAttributes.Static);
            il.Emit(OpCodes.Ldc_I4, packed.Length);
            il.Emit(OpCodes.Newarr, typeof(byte));
            il.Emit(OpCodes.Dup);
            il.Emit(OpCodes.Ldtoken, data);
            il.Emit(OpCodes.Call, typeof(RuntimeHelpers).GetMethod(nameof(RuntimeHelpers.InitializeArray), new[] { typeof(Array), typeof(RuntimeFieldHandle) }));
            // 常量表只有 Clojure 数据，由 Cleat.Core 读回（cleat-092）
            il.Emit(OpCodes.Call, core.GetType("Cleat.Core.Runtime.Fasl", true).GetMethod("ReadPackedTable", new[] { typeof(byte[]) }));
        }
        il.Emit(OpCodes.Stsfld, _constantsField);
        il.Emit(OpCodes.Ret);
    }

    /// <summary>固定压缩级别：同一运行时版本下输出确定（自举不动点比较 dll 字节）。</summary>
    private static byte[] Deflate(byte[] bytes)
    {
        var ms = new MemoryStream();
        using (var z = new DeflateStream(ms, CompressionLevel.Optimal, leaveOpen: true)) z.Write(bytes, 0, bytes.Length);
        return ms.ToArray();
    }
}
