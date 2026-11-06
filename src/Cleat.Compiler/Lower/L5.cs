using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using clojure.lang;

namespace Cleat.Compiler.Lower;

// L5（designs/30 §3.4）：一个编译单元的类、字段、方法声明；方法体是已经决议的 CIL 指令序列（结构化的 try 块、标签、局部变量显式）。
// 类型引用（TypeRef）是三者之一：System.Type、本单元的 ClassDecl、类型名字符串（分析器给出的 deftype 名等，发射时解析）。
// 方法引用：MethodBase（已有的）、MethodDecl（本单元的）、MethodRef（按名字与形参类型在发射时找）。字段引用：FieldInfo / FieldDecl / FieldRef。

/// <summary>编译单元。</summary>
public sealed class UnitDecl
{
    public UnitDecl(string name, string moduleName, bool live)
    {
        Name = name;
        Live = live;
        Module = new ClassDecl(moduleName) { Abstract = true, Sealed = true };
    }

    public string Name { get; }

    /// <summary>内存模式（eval）：常量对象直接交给发射器（ConstantPool），不编码。</summary>
    public bool Live { get; }

    /// <summary>入口类型（顶层形式的 Init 方法与 Initialize）。</summary>
    public ClassDecl Module { get; }

    public readonly List<ClassDecl> Classes = new List<ClassDecl>();

    /// <summary>常量表（ldconst k 取第 k 项）。</summary>
    public readonly List<object> Constants = new List<object>();

    public MethodDecl Entry;

    /// <summary>每个类型初始化器先拉起运行时（包程序集，designs/17 §4.1）。</summary>
    public bool LazyStart;

    /// <summary>程序集级属性（类型名, 字符串参数）。</summary>
    public readonly List<(string Type, string[] Args)> Attributes = new List<(string, string[])>();

    /// <summary>嵌入资源（名字, 文件路径）。</summary>
    public readonly List<(string Name, string Path)> Resources = new List<(string, string)>();

    public IEnumerable<ClassDecl> AllClasses => new[] { Module }.Concat(Classes);
}

public sealed class ClassDecl
{
    public ClassDecl(string name) { Name = name; }

    public string Name { get; }
    public object Base = typeof(object);
    public bool Sealed, Abstract, Interface;
    public readonly List<object> Interfaces = new List<object>();
    public readonly List<FieldDecl> Fields = new List<FieldDecl>();
    public readonly List<MethodDecl> Methods = new List<MethodDecl>();
    public readonly List<(string Type, string[] Args)> Attributes = new List<(string, string[])>();

    public FieldDecl Field(string name) => Fields.FirstOrDefault(f => f.Name == name) ?? throw new InvalidOperationException($"no field {Name}::{name}");

    public FieldDecl AddField(string name, object type, bool isStatic = false, bool readOnly = false)
    {
        var f = new FieldDecl(this, name, type) { Static = isStatic, ReadOnly = readOnly };
        Fields.Add(f);
        return f;
    }

    public MethodDecl AddMethod(MethodDecl m)
    {
        m.Owner = this;
        Methods.Add(m);
        return m;
    }

    public override string ToString() => Name;
}

public sealed class FieldDecl
{
    public FieldDecl(ClassDecl owner, string name, object type)
    {
        Owner = owner;
        Name = name;
        Type = type;
    }

    public ClassDecl Owner { get; }
    public string Name { get; }
    public object Type { get; }
    public bool Static, ReadOnly, Volatile, Private;
}

public enum MethodKind { Method, Ctor, Cctor }

public sealed class MethodDecl
{
    public MethodDecl(string name, MethodKind kind = MethodKind.Method)
    {
        Name = name;
        Kind = kind;
    }

    public ClassDecl Owner;
    public string Name { get; }
    public MethodKind Kind { get; }
    public bool Static, Override, Virtual, AbstractMethod, Private, Protected, Optimize, Tiered, Async;
    public object[] Params = Array.Empty<object>();
    public object Ret = typeof(void);
    public readonly List<object> Locals = new List<object>();
    public List<Ins> Body = new List<Ins>();

    /// <summary>源文件（序列点挂的文档）；null 时方法里不能有 Line。</summary>
    public string Source;

    /// <summary>显式接口实现：接口与方法名。</summary>
    public object ImplementsType;
    public string ImplementsName;

    public override string ToString() => $"{Owner?.Name}::{Name}";
}

/// <summary>按名字与形参类型找的方法（发射时决议）：Owner 为 TypeRef；Ctor 时 Name 忽略。</summary>
public sealed class MethodRef
{
    public MethodRef(object owner, string name, object[] ps, object ret, bool ctor = false)
    {
        Owner = owner;
        Name = name;
        Params = ps;
        Ret = ret;
        Ctor = ctor;
    }

    public object Owner { get; }
    public string Name { get; }
    public object[] Params { get; }
    public object Ret { get; }
    public bool Ctor { get; }

    public override string ToString() => $"{Owner}::{Name}";
}

/// <summary>按名字找的字段（发射时决议）。</summary>
public sealed class FieldRef
{
    public FieldRef(object owner, string name)
    {
        Owner = owner;
        Name = name;
    }

    public object Owner { get; }
    public string Name { get; }
}

/// <summary>标签（按身份）。</summary>
public sealed class Lbl
{
    private static int s_next;
    public readonly int Id = System.Threading.Interlocked.Increment(ref s_next);
    public override string ToString() => "L" + Id;
}

/// <summary>指令。</summary>
public abstract class Ins
{
}

/// <summary>一条 CIL 指令。Arg：null、int（含局部 / 形参下标）、long、double、string、TypeRef、方法引用、字段引用、Lbl、Lbl[]（switch）。</summary>
public sealed class Il : Ins
{
    public Il(OpCode op, object arg = null)
    {
        Op = op;
        Arg = arg;
    }

    public OpCode Op { get; }
    public object Arg { get; }
    public override string ToString() => Arg is null ? Op.Name : $"{Op.Name} {Arg}";
}

public sealed class LabelIns : Ins
{
    public LabelIns(Lbl l) { L = l; }
    public Lbl L { get; }
    public override string ToString() => L + ":";
}

/// <summary>常量表第 k 项（ldsfld $Constants::c; ldc.i4 k; ldelem.ref）。</summary>
public sealed class LdConst : Ins
{
    public LdConst(int k) { Index = k; }
    public int Index { get; }
}

/// <summary>序列点（方法须有 Source）。</summary>
public sealed class Line : Ins
{
    public Line(int l1, int c1, int l2, int c2)
    {
        L1 = l1;
        C1 = c1;
        L2 = l2;
        C2 = c2;
    }

    public int L1 { get; }
    public int C1 { get; }
    public int L2 { get; }
    public int C2 { get; }
}

/// <summary>结构化异常：进入时栈须为空；各块末尾的 leave / endfinally 由发射器加入；catch 入口栈上是异常对象。</summary>
public sealed class TryIns : Ins
{
    public List<Ins> Body = new List<Ins>();
    public readonly List<(object Type, List<Ins> Code)> Catches = new List<(object, List<Ins>)>();
    public List<Ins> Finally;
}
