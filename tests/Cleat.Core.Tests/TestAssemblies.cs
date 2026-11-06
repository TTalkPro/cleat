using System;
using System.Reflection;
using System.Reflection.Emit;

namespace Cleat.Core.Tests;

/// <summary>
/// 测试用的小程序集（cleat-182：原来写成文本交给已删的发射器 Cleat.Emit，现在直接用 Reflection.Emit）：
/// 一个静态类型，若干无参 <c>public static object</c> 方法，加上程序集级特性（CleatModule / CleatPackage / CleatNamespace）。
/// </summary>
internal static class TestAssemblies
{
    public sealed record Method(string Name, Action<ILGenerator> Body);

    /// <summary>编出 path 处的 dll（Save）；type 是唯一的类型（abstract sealed），methods 是它的方法。</summary>
    public static void Save(string path, string assemblyName, string type, Method[] methods, params CustomAttributeBuilder[] attributes)
    {
        var ab = new PersistedAssemblyBuilder(new AssemblyName(assemblyName), typeof(object).Assembly);
        Define(ab, type, methods, attributes);
        ab.Save(path);
    }

    /// <summary>内存里的程序集（可运行），其余同 <see cref="Save"/>。</summary>
    public static Assembly InMemory(string assemblyName, string type, Method[] methods, params CustomAttributeBuilder[] attributes)
    {
        var ab = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName(assemblyName), AssemblyBuilderAccess.Run);
        Define(ab, type, methods, attributes);
        return ab;
    }

    private static void Define(AssemblyBuilder ab, string type, Method[] methods, CustomAttributeBuilder[] attributes)
    {
        foreach (var a in attributes) ab.SetCustomAttribute(a);
        var mb = ab.DefineDynamicModule(ab.GetName().Name!);
        var tb = mb.DefineType(type, TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed | TypeAttributes.Class, typeof(object));
        foreach (var m in methods)
        {
            var meth = tb.DefineMethod(m.Name, MethodAttributes.Public | MethodAttributes.Static, typeof(object), Type.EmptyTypes);
            m.Body(meth.GetILGenerator());
        }
        tb.CreateType();
    }

    public static CustomAttributeBuilder Attribute<T>(params object[] args) where T : Attribute
    {
        var types = Array.ConvertAll(args, a => a.GetType());
        return new CustomAttributeBuilder(typeof(T).GetConstructor(types)!, args);
    }

    /// <summary>方法体：依次 ldstr s 并调 <see cref="LoaderProbe.Hit"/>，最后返回 result（null 时 ldnull）。</summary>
    public static Action<ILGenerator> HitThenReturn(string hit, string result) => il =>
    {
        if (hit != null)
        {
            il.Emit(OpCodes.Ldstr, hit);
            il.Emit(OpCodes.Call, typeof(LoaderProbe).GetMethod(nameof(LoaderProbe.Hit))!);
        }
        if (result == null) il.Emit(OpCodes.Ldnull);
        else il.Emit(OpCodes.Ldstr, result);
        il.Emit(OpCodes.Ret);
    };
}
