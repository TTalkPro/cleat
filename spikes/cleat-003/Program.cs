// cleat-003：验证 Clojure 额外需要的 .NET 10 能力
// （tail.、PersistedAssemblyBuilder 基本保存、内存程序集、MVID 回填、PDB 已由 IronScheme iron-003 验证，不重复）
//   interfaces —— 内存程序集（RunAndCollect）里生成实现接口的类：公开可变字段、volatile 字段（modreq）、显式接口实现
//   table      —— stage0 方法表转发：ldsfld IFn[] table; ldc.i4 i; ldelem.ref; ldarg…; callvirt invoke(k)，类型创建后再填表
//   switch     —— switch 指令带 500 个标签
//   save       —— PersistedAssemblyBuilder 保存含接口、List<object>（嵌套泛型）、params 调用、switch 的程序集，并 spawn 另一进程 load
//   load <dll> —— 另一进程加载并运行
//   count      —— 10000 个 fn 类：单程序集 vs 每 1000 个一个程序集；跨程序集 newobj/call 对非公开类型的要求
//   threading  —— Monitor 可重入；ThreadLocal vs AsyncLocal 开销
//   all        —— 以上全部（load 由 save 自动 spawn）
using System.Diagnostics;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;

namespace Cleat.Spike;

// spike 自带的接口，形状仿 clojure.lang.ISeq / IFn
public interface ISeq
{
    object? first();
    ISeq? next();
}

public interface IFn
{
    object? invoke(object? a);
    object? invoke(object? a, object? b);
    object? invoke(object? a, object? b, object? c);
}

public abstract class AFn : IFn
{
    public virtual object? invoke(object? a) => throw new InvalidOperationException("arity 1");
    public virtual object? invoke(object? a, object? b) => throw new InvalidOperationException("arity 2");
    public virtual object? invoke(object? a, object? b, object? c) => throw new InvalidOperationException("arity 3");
}

public sealed class Lambda : AFn
{
    readonly Func<object?, object?, object?>? f2;
    readonly Func<object?, object?, object?, object?>? f3;
    public Lambda(Func<object?, object?, object?> f) => f2 = f;
    public Lambda(Func<object?, object?, object?, object?> f) => f3 = f;
    public override object? invoke(object? a, object? b) => f2!(a, b);
    public override object? invoke(object? a, object? b, object? c) => f3!(a, b, c);
}

public static class Helpers
{
    // C# 的 params 方法，供生成代码调用（生成代码必须自己建数组）
    public static string Join(params object?[] xs) => string.Join(",", xs);
}

public static class Program
{
    static int rc;

    public static int Main(string[] args)
    {
        string cmd = args.Length > 0 ? args[0] : "all";
        string dir = Path.Combine(AppContext.BaseDirectory, "out");
        Directory.CreateDirectory(dir);
        Console.WriteLine($".NET {Environment.Version}  {System.Runtime.InteropServices.RuntimeInformation.OSDescription}  {System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}");
        switch (cmd)
        {
            case "interfaces": Interfaces(); break;
            case "table": Table(); break;
            case "switch": Switch(); break;
            case "save": SaveAndSpawnLoad(Path.Combine(dir, "gen.dll")); break;
            case "load": LoadAndRun(args[1]); break;
            case "count": Count(); break;
            case "threading": Threading(); break;
            case "all":
                Interfaces(); Table(); Switch(); SaveAndSpawnLoad(Path.Combine(dir, "gen.dll")); Count(); Threading();
                break;
            default:
                Console.WriteLine("usage: interfaces|table|switch|save|load <dll>|count|threading|all");
                return 2;
        }
        Console.WriteLine(rc == 0 ? "ALL PASS" : "SOME FAILED");
        return rc;
    }

    static void Check(string name, bool ok, string? detail = null)
    {
        if (!ok) rc = 1;
        Console.WriteLine($"{(ok ? "PASS" : "FAIL")} {name}{(detail is null ? "" : " = " + detail)}");
    }

    static void Info(string text) => Console.WriteLine($"INFO {text}");

    static T Timed<T>(string name, Func<T> f)
    {
        var sw = Stopwatch.StartNew();
        var r = f();
        sw.Stop();
        Info($"{name}: {sw.Elapsed.TotalMilliseconds:F1} ms");
        return r;
    }

    // 跑两遍取第二遍（排除 JIT），输出 ns/次
    static double Bench(string name, int iters, Action<int> body)
    {
        body(iters / 10);
        long alloc0 = GC.GetAllocatedBytesForCurrentThread();
        var sw = Stopwatch.StartNew();
        body(iters);
        sw.Stop();
        long alloc = GC.GetAllocatedBytesForCurrentThread() - alloc0;
        double ns = sw.Elapsed.TotalNanoseconds / iters;
        Info($"{name}: {ns:F1} ns/次，{sw.ElapsedMilliseconds} ms / {iters:N0} 次，分配 {alloc / (double)iters:F1} B/次");
        return ns;
    }

    static (AssemblyBuilder ab, ModuleBuilder mb) Dyn(string name, AssemblyBuilderAccess access = AssemblyBuilderAccess.RunAndCollect)
    {
        var ab = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName(name), access);
        return (ab, ab.DefineDynamicModule(name));
    }

    const MethodAttributes ImplicitImpl = MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.HideBySig | MethodAttributes.NewSlot | MethodAttributes.Final;
    const MethodAttributes ExplicitImpl = MethodAttributes.Private | MethodAttributes.Virtual | MethodAttributes.HideBySig | MethodAttributes.NewSlot | MethodAttributes.Final;
    const MethodAttributes Override = MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.HideBySig;
    const MethodAttributes Abstract = MethodAttributes.Public | MethodAttributes.Abstract | MethodAttributes.Virtual | MethodAttributes.HideBySig | MethodAttributes.NewSlot;

    static void EmitThrow(ILGenerator il, string msg)
    {
        il.Emit(OpCodes.Ldstr, msg);
        il.Emit(OpCodes.Newobj, typeof(InvalidOperationException).GetConstructor(new[] { typeof(string) })!);
        il.Emit(OpCodes.Throw);
    }

    // ---------------------------------------------------------------- 1. 接口实现类型

    // 生成 deftype 风格的类 Cons : ISeq, IFn
    //   public object head            —— ^:mutable
    //   public volatile ISeq tail     —— ^:volatile-mutable（modreq IsVolatile）
    //   ISeq.first / ISeq.next 显式实现（DefineMethodOverride）；IFn.invoke 隐式实现
    static Type DefineCons(ModuleBuilder mb, string name, Type iseq, MethodInfo iseqFirst, MethodInfo iseqNext, bool alsoSpikeISeq)
    {
        var ifaces = alsoSpikeISeq ? new[] { iseq, typeof(ISeq), typeof(IFn) } : new[] { iseq, typeof(IFn) };
        var tb = mb.DefineType(name, TypeAttributes.Public | TypeAttributes.Sealed, typeof(object), ifaces);
        var head = tb.DefineField("head", typeof(object), FieldAttributes.Public);
        var tail = tb.DefineField("tail", iseq, new[] { typeof(IsVolatile) }, null, FieldAttributes.Public);
        var hits = tb.DefineField("hits", typeof(int), FieldAttributes.Public);

        var ctor = tb.DefineConstructor(MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName,
            CallingConventions.Standard, new[] { typeof(object), iseq });
        {
            var il = ctor.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Call, typeof(object).GetConstructor(Type.EmptyTypes)!);
            il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldarg_1); il.Emit(OpCodes.Stfld, head);
            il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldarg_2); il.Emit(OpCodes.Volatile); il.Emit(OpCodes.Stfld, tail);
            il.Emit(OpCodes.Ret);
        }

        var first = tb.DefineMethod(iseq.FullName + ".first", ExplicitImpl, typeof(object), Type.EmptyTypes);
        { var il = first.GetILGenerator(); il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldfld, head); il.Emit(OpCodes.Ret); }
        tb.DefineMethodOverride(first, iseqFirst);

        var next = tb.DefineMethod(iseq.FullName + ".next", ExplicitImpl, iseq, Type.EmptyTypes);
        { var il = next.GetILGenerator(); il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Volatile); il.Emit(OpCodes.Ldfld, tail); il.Emit(OpCodes.Ret); }
        tb.DefineMethodOverride(next, iseqNext);

        if (alsoSpikeISeq)
        {
            // 同一个类同时显式实现两个同名接口方法（生成程序集里的 IGenSeq 与宿主的 ISeq）
            var f2 = tb.DefineMethod("Cleat.Spike.ISeq.first", ExplicitImpl, typeof(object), Type.EmptyTypes);
            { var il = f2.GetILGenerator(); il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldfld, head); il.Emit(OpCodes.Ret); }
            tb.DefineMethodOverride(f2, typeof(ISeq).GetMethod("first")!);
            var n2 = tb.DefineMethod("Cleat.Spike.ISeq.next", ExplicitImpl, typeof(ISeq), Type.EmptyTypes);
            { var il = n2.GetILGenerator(); il.Emit(OpCodes.Ldnull); il.Emit(OpCodes.Ret); }
            tb.DefineMethodOverride(n2, typeof(ISeq).GetMethod("next")!);
        }

        // IFn.invoke(a)：hits++ 然后返回 a（隐式实现：名字与签名匹配即可）
        var inv1 = tb.DefineMethod("invoke", ImplicitImpl, typeof(object), new[] { typeof(object) });
        {
            var il = inv1.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Dup); il.Emit(OpCodes.Ldfld, hits); il.Emit(OpCodes.Ldc_I4_1); il.Emit(OpCodes.Add); il.Emit(OpCodes.Stfld, hits);
            il.Emit(OpCodes.Ldarg_1); il.Emit(OpCodes.Ret);
        }
        foreach (int k in new[] { 2, 3 })
        {
            var m = tb.DefineMethod("invoke", ImplicitImpl, typeof(object), Enumerable.Repeat(typeof(object), k).ToArray());
            EmitThrow(m.GetILGenerator(), $"arity {k}");
        }
        return tb.CreateType()!;
    }

    static void Interfaces()
    {
        Console.WriteLine("== 1. 接口实现类型（RunAndCollect）");
        var (_, mb) = Dyn("ifaces");
        var t = DefineCons(mb, "Cons", typeof(ISeq), typeof(ISeq).GetMethod("first")!, typeof(ISeq).GetMethod("next")!, alsoSpikeISeq: false);
        CheckCons(t, typeof(ISeq), "a");

        // 经 ISeq 调用
        var c2 = (ISeq)Activator.CreateInstance(t, "b", null)!;
        var c1 = (ISeq)Activator.CreateInstance(t, "a", c2)!;
        Check("经 ISeq.first 调用", Equals(c1.first(), "a"), c1.first()?.ToString());
        Check("经 ISeq.next 调用（volatile 字段）", ReferenceEquals(c1.next(), c2));
        // 公开可变字段：C# 直接写
        t.GetField("head")!.SetValue(c1, "a2");
        t.GetField("tail")!.SetValue(c1, null);
        Check("公开可变字段写后经接口可见", Equals(c1.first(), "a2") && c1.next() is null);
        // 经 IFn 调用
        var f = (IFn)c1;
        Check("经 IFn.invoke(1) 调用", Equals(f.invoke(42), 42) && (int)t.GetField("hits")!.GetValue(c1)! == 1);
        try { f.invoke(1, 2); Check("invoke(2) 抛出", false); }
        catch (InvalidOperationException e) { Check("invoke(2) 抛出", e.Message == "arity 2", e.Message); }
    }

    static void CheckCons(Type t, Type iseq, object headValue)
    {
        var tail = t.GetField("tail")!;
        Check("volatile 字段带 modreq IsVolatile", tail.GetRequiredCustomModifiers().Contains(typeof(IsVolatile)),
            string.Join(",", tail.GetRequiredCustomModifiers().Select(m => m.Name)));
        Check("公开可变字段 head 是 public 非 initonly", t.GetField("head")!.IsPublic && !t.GetField("head")!.IsInitOnly);
        Check("显式接口实现：无公开 first 方法", t.GetMethod("first") is null);
        var map = t.GetInterfaceMap(iseq);
        var target = map.TargetMethods[Array.IndexOf(map.InterfaceMethods, iseq.GetMethod("first"))];
        Check("显式接口实现：接口映射指向私有方法", target.IsPrivate && target.IsFinal && target.IsVirtual, target.Name);
        var obj = Activator.CreateInstance(t, headValue, null)!;
        Check("反射调用接口方法", Equals(iseq.GetMethod("first")!.Invoke(obj, null), headValue));
    }

    // ---------------------------------------------------------------- 2. stage0 方法表转发

    static void Table()
    {
        Console.WriteLine("== 2. 方法表转发（类型创建后填表）");
        var (_, mb) = Dyn("table");
        // 表放在单独生成的持有者类型里
        var holder = mb.DefineType("Fwd$Holder", TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed);
        var table = holder.DefineField("table", typeof(IFn[]), FieldAttributes.Public | FieldAttributes.Static);
        var holderType = holder.CreateType()!;

        var tb = mb.DefineType("Fwd", TypeAttributes.Public | TypeAttributes.Sealed, typeof(object), new[] { typeof(IFn) });
        tb.DefineDefaultConstructor(MethodAttributes.Public);
        // invoke(a)   → table[0].invoke(this, a)
        // invoke(a,b) → table[1].invoke(this, a, b)
        for (int k = 1; k <= 2; k++)
        {
            var m = tb.DefineMethod("invoke", ImplicitImpl, typeof(object), Enumerable.Repeat(typeof(object), k).ToArray());
            var il = m.GetILGenerator();
            il.Emit(OpCodes.Ldsfld, table);
            il.Emit(OpCodes.Ldc_I4, k - 1);
            il.Emit(OpCodes.Ldelem_Ref);
            for (int a = 0; a <= k; a++) il.Emit(OpCodes.Ldarg, (short)a);
            il.Emit(OpCodes.Callvirt, typeof(IFn).GetMethod("invoke", Enumerable.Repeat(typeof(object), k + 1).ToArray())!);
            il.Emit(OpCodes.Ret);
        }
        {
            var m = tb.DefineMethod("invoke", ImplicitImpl, typeof(object), Enumerable.Repeat(typeof(object), 3).ToArray());
            EmitThrow(m.GetILGenerator(), "arity 3");
        }
        var fwdType = tb.CreateType()!;
        var fwd = (IFn)Activator.CreateInstance(fwdType)!;

        // 未填表：空引用
        try { fwd.invoke(1); Check("填表前调用抛 NullReferenceException", false); }
        catch (NullReferenceException) { Check("填表前调用抛 NullReferenceException", true); }

        // 类型创建后填表：C# 对象（Lambda : AFn）
        var tableField = holderType.GetField("table")!;
        var arr = new IFn[]
        {
            new Lambda((self, a) => $"m1({self!.GetType().Name},{a})"),
            new Lambda((self, a, b) => $"m2({self!.GetType().Name},{a},{b})"),
        };
        tableField.SetValue(null, arr);
        Check("invoke(1) 转发到 C# 委托", Equals(fwd.invoke(7), "m1(Fwd,7)"), fwd.invoke(7)?.ToString());
        Check("invoke(2) 转发到 C# 委托", Equals(fwd.invoke(7, 8), "m2(Fwd,7,8)"), fwd.invoke(7, 8)?.ToString());

        // 运行中换表项
        arr[0] = new Lambda((self, a) => "replaced");
        Check("运行中替换表项立即生效", Equals(fwd.invoke(0), "replaced"));
        arr[0] = new Lambda((self, a) => a);

        // 开销：转发 vs 直接调 C# 对象
        IFn direct = new Lambda((self, a) => a);
        object? sink = null;
        double d = Bench("直接 callvirt C# IFn", 10_000_000, n => { for (int i = 0; i < n; i++) sink = direct.invoke(null, sink); });
        double t = Bench("经方法表转发（多一次 ldsfld+ldelem+callvirt）", 10_000_000, n => { for (int i = 0; i < n; i++) sink = fwd.invoke(sink); });
        Info($"转发开销约 {t - d:F1} ns/次");
    }

    // ---------------------------------------------------------------- 3. switch 与持久化

    static MethodBuilder DefineSwitch(TypeBuilder tb, string name, int n)
    {
        var m = tb.DefineMethod(name, MethodAttributes.Public | MethodAttributes.Static, typeof(int), new[] { typeof(int) });
        var il = m.GetILGenerator();
        var labels = new Label[n];
        for (int i = 0; i < n; i++) labels[i] = il.DefineLabel();
        var dflt = il.DefineLabel();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Switch, labels);
        il.Emit(OpCodes.Br, dflt);
        for (int i = 0; i < n; i++)
        {
            il.MarkLabel(labels[i]);
            il.Emit(OpCodes.Ldc_I4, i * 7 + 1);
            il.Emit(OpCodes.Ret);
        }
        il.MarkLabel(dflt);
        il.Emit(OpCodes.Ldc_I4_M1);
        il.Emit(OpCodes.Ret);
        return m;
    }

    static bool VerifySwitch(MethodInfo m, int n)
    {
        var f = m.CreateDelegate<Func<int, int>>();
        for (int i = 0; i < n; i++) if (f(i) != i * 7 + 1) return false;
        return f(-1) == -1 && f(n) == -1 && f(int.MaxValue) == -1;
    }

    static void Switch()
    {
        Console.WriteLine("== 3a. switch 指令");
        var (_, mb) = Dyn("sw");
        foreach (int n in new[] { 500, 5000 })
        {
            var tb = mb.DefineType($"Sw{n}", TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed);
            DefineSwitch(tb, "Dispatch", n);
            var t = Timed($"定义+创建 {n} 个标签的 switch 方法", () => tb.CreateType()!);
            var m = t.GetMethod("Dispatch")!;
            bool ok = Timed($"首次调用（含 JIT）+ 遍历全部 {n} 个分支", () => VerifySwitch(m, n));
            Check($"switch {n} 个标签全部分支正确，越界走 default", ok);
        }
    }

    // 生成要保存的程序集：接口 IGenSeq、实现类 GenCons、GenOps（switch、List<object>、嵌套泛型、params）
    static void DefineSaved(ModuleBuilder mb)
    {
        // 程序集内定义的接口（definterface）
        var igen = mb.DefineType("IGenSeq", TypeAttributes.Public | TypeAttributes.Interface | TypeAttributes.Abstract);
        var igenFirst = igen.DefineMethod("first", Abstract, typeof(object), Type.EmptyTypes);
        var igenNext = igen.DefineMethod("next", Abstract, igen, Type.EmptyTypes);
        var igenType = igen.CreateType()!;

        DefineCons(mb, "GenCons", igenType, igenFirst, igenNext, alsoSpikeISeq: true);

        var ops = mb.DefineType("GenOps", TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed);
        DefineSwitch(ops, "Dispatch", 500);

        // Collect(n) : List<object>  —— 泛型实例化的 newobj / callvirt
        var listT = typeof(List<object>);
        var collect = ops.DefineMethod("Collect", MethodAttributes.Public | MethodAttributes.Static, listT, new[] { typeof(int) });
        {
            var il = collect.GetILGenerator();
            var list = il.DeclareLocal(listT); var i = il.DeclareLocal(typeof(int));
            var top = il.DefineLabel(); var test = il.DefineLabel();
            il.Emit(OpCodes.Newobj, listT.GetConstructor(Type.EmptyTypes)!); il.Emit(OpCodes.Stloc, list);
            il.Emit(OpCodes.Ldc_I4_0); il.Emit(OpCodes.Stloc, i); il.Emit(OpCodes.Br, test);
            il.MarkLabel(top);
            il.Emit(OpCodes.Ldloc, list); il.Emit(OpCodes.Ldloc, i); il.Emit(OpCodes.Box, typeof(int)); il.Emit(OpCodes.Callvirt, listT.GetMethod("Add")!);
            il.Emit(OpCodes.Ldloc, i); il.Emit(OpCodes.Ldc_I4_1); il.Emit(OpCodes.Add); il.Emit(OpCodes.Stloc, i);
            il.MarkLabel(test);
            il.Emit(OpCodes.Ldloc, i); il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Blt, top);
            il.Emit(OpCodes.Ldloc, list); il.Emit(OpCodes.Ret);
        }

        // Nested() : Dictionary<string, List<object>>  —— 嵌套泛型引用
        var dictT = typeof(Dictionary<string, List<object>>);
        var nested = ops.DefineMethod("Nested", MethodAttributes.Public | MethodAttributes.Static, dictT, Type.EmptyTypes);
        {
            var il = nested.GetILGenerator();
            il.Emit(OpCodes.Newobj, dictT.GetConstructor(Type.EmptyTypes)!);
            il.Emit(OpCodes.Dup); il.Emit(OpCodes.Ldstr, "k"); il.Emit(OpCodes.Ldc_I4_3); il.Emit(OpCodes.Call, collect);
            il.Emit(OpCodes.Callvirt, dictT.GetMethod("set_Item")!);
            il.Emit(OpCodes.Ret);
        }

        // Sum(params object[] xs) : int  —— 生成程序集里带 ParamArrayAttribute 的方法
        var sum = ops.DefineMethod("Sum", MethodAttributes.Public | MethodAttributes.Static, typeof(int), new[] { typeof(object[]) });
        sum.DefineParameter(1, ParameterAttributes.None, "xs")
           .SetCustomAttribute(new CustomAttributeBuilder(typeof(ParamArrayAttribute).GetConstructor(Type.EmptyTypes)!, Array.Empty<object>()));
        {
            var il = sum.GetILGenerator();
            var acc = il.DeclareLocal(typeof(int)); var i = il.DeclareLocal(typeof(int));
            var top = il.DefineLabel(); var test = il.DefineLabel();
            il.Emit(OpCodes.Ldc_I4_0); il.Emit(OpCodes.Stloc, acc);
            il.Emit(OpCodes.Ldc_I4_0); il.Emit(OpCodes.Stloc, i); il.Emit(OpCodes.Br, test);
            il.MarkLabel(top);
            il.Emit(OpCodes.Ldloc, acc); il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldloc, i); il.Emit(OpCodes.Ldelem_Ref); il.Emit(OpCodes.Unbox_Any, typeof(int)); il.Emit(OpCodes.Add); il.Emit(OpCodes.Stloc, acc);
            il.Emit(OpCodes.Ldloc, i); il.Emit(OpCodes.Ldc_I4_1); il.Emit(OpCodes.Add); il.Emit(OpCodes.Stloc, i);
            il.MarkLabel(test);
            il.Emit(OpCodes.Ldloc, i); il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldlen); il.Emit(OpCodes.Conv_I4); il.Emit(OpCodes.Blt, top);
            il.Emit(OpCodes.Ldloc, acc); il.Emit(OpCodes.Ret);
        }

        // CallParams() : string —— 调用 params 方法（自己建数组）：String.Format(string, object[])、Helpers.Join(object[])、Sum(object[])
        var callParams = ops.DefineMethod("CallParams", MethodAttributes.Public | MethodAttributes.Static, typeof(string), Type.EmptyTypes);
        {
            var il = callParams.GetILGenerator();
            void Arr(int[] vals)
            {
                il.Emit(OpCodes.Ldc_I4, vals.Length); il.Emit(OpCodes.Newarr, typeof(object));
                for (int k = 0; k < vals.Length; k++)
                {
                    il.Emit(OpCodes.Dup); il.Emit(OpCodes.Ldc_I4, k); il.Emit(OpCodes.Ldc_I4, vals[k]); il.Emit(OpCodes.Box, typeof(int)); il.Emit(OpCodes.Stelem_Ref);
                }
            }
            il.Emit(OpCodes.Ldstr, "{0}|{1}|{2}");
            Arr(new[] { 1, 2, 3 });
            il.Emit(OpCodes.Call, typeof(string).GetMethod("Format", new[] { typeof(string), typeof(object[]) })!);
            il.Emit(OpCodes.Ldstr, ";");
            Arr(new[] { 4, 5 });
            il.Emit(OpCodes.Call, typeof(Helpers).GetMethod("Join")!);
            il.Emit(OpCodes.Ldstr, ";");
            Arr(new[] { 10, 20, 30 });
            il.Emit(OpCodes.Call, sum);
            il.Emit(OpCodes.Box, typeof(int)); il.Emit(OpCodes.Callvirt, typeof(object).GetMethod("ToString")!);
            il.Emit(OpCodes.Call, typeof(string).GetMethod("Concat", new[] { typeof(string), typeof(string), typeof(string), typeof(string) })!);
            il.Emit(OpCodes.Call, typeof(string).GetMethod("Concat", new[] { typeof(string), typeof(string) })!);
            il.Emit(OpCodes.Ret);
        }
        ops.CreateType();
    }

    static void SaveAndSpawnLoad(string path)
    {
        Console.WriteLine("== 3b. PersistedAssemblyBuilder 保存");
        var ab = new PersistedAssemblyBuilder(new AssemblyName("gen"), typeof(object).Assembly);
        DefineSaved(ab.DefineDynamicModule("gen"));
        Timed("保存", () => { ab.Save(path); return 0; });
        Check("已保存", File.Exists(path), $"{new FileInfo(path).Length} 字节 {path}");

        Console.WriteLine("== 3c. 另一进程加载");
        var psi = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true };
        psi.ArgumentList.Add(typeof(Program).Assembly.Location);
        psi.ArgumentList.Add("load");
        psi.ArgumentList.Add(path);
        using var p = Process.Start(psi)!;
        string output = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries)) Console.WriteLine("  | " + line);
        Check($"子进程（pid {p.Id}）加载并运行", p.ExitCode == 0, $"exit {p.ExitCode}");
    }

    static void LoadAndRun(string path)
    {
        Console.WriteLine($"-- load {path}（pid {Environment.ProcessId}）");
        var asm = Assembly.LoadFrom(path);
        var igen = asm.GetType("IGenSeq")!;
        var cons = asm.GetType("GenCons")!;
        Check("程序集内定义的接口是接口", igen.IsInterface && igen.IsPublic);
        CheckCons(cons, igen, "x");
        // 同一类型同时显式实现 IGenSeq 与宿主 ISeq
        var obj = Activator.CreateInstance(cons, "x", null)!;
        Check("经宿主 ISeq 调用", obj is ISeq s && Equals(s.first(), "x"));
        Check("经生成接口 IGenSeq（反射）调用", Equals(igen.GetMethod("first")!.Invoke(obj, null), "x"));
        Check("经 IFn 调用", obj is IFn f && Equals(f.invoke("y"), "y"));

        var ops = asm.GetType("GenOps")!;
        Check("switch 500 个标签（已保存程序集）", VerifySwitch(ops.GetMethod("Dispatch")!, 500));
        var list = (List<object>)ops.GetMethod("Collect")!.Invoke(null, new object[] { 5 })!;
        Check("List<object> 构造与 Add", list.Count == 5 && Equals(list[4], 4));
        var dict = (Dictionary<string, List<object>>)ops.GetMethod("Nested")!.Invoke(null, null)!;
        Check("嵌套泛型 Dictionary<string, List<object>>", dict["k"].Count == 3);
        var sum = ops.GetMethod("Sum")!;
        Check("生成的 params 方法带 ParamArrayAttribute", sum.GetParameters()[0].IsDefined(typeof(ParamArrayAttribute), false));
        Check("params 方法反射调用", Equals(sum.Invoke(null, new object[] { new object[] { 1, 2, 3 } }), 6));
        var r = (string)ops.GetMethod("CallParams")!.Invoke(null, null)!;
        Check("生成代码调用 params 方法（String.Format、C# params、生成的 params）", r == "1|2|3;4,5;60", r);
        Console.WriteLine(rc == 0 ? "-- load PASS" : "-- load FAIL");
    }

    // ---------------------------------------------------------------- 4. 动态程序集数量

    static TypeBuilder DefineFnType(ModuleBuilder mb, int i, TypeAttributes vis = TypeAttributes.Public)
    {
        var tb = mb.DefineType($"fn{i}", vis | TypeAttributes.Sealed, typeof(AFn));
        tb.DefineDefaultConstructor(MethodAttributes.Public);
        var m = tb.DefineMethod("invoke", Override, typeof(object), new[] { typeof(object) });
        var il = m.GetILGenerator();
        il.Emit(OpCodes.Ldc_I4, i); il.Emit(OpCodes.Box, typeof(int)); il.Emit(OpCodes.Ret);
        return tb;
    }

    static void Count()
    {
        Console.WriteLine("== 4. 动态程序集数量");
        const int N = 10_000;
        void Run(string name, int perAssembly, AssemblyBuilderAccess access, bool call = true, bool interleave = false)
        {
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            long ws0 = Environment.WorkingSet;
            var sw = Stopwatch.StartNew();
            var builders = new TypeBuilder[N];
            ModuleBuilder? mb = null;
            int asmCount = 0;
            for (int i = 0; i < N; i++)
            {
                if (i % perAssembly == 0) { mb = Dyn($"fns{asmCount++}", access).mb; }
                builders[i] = DefineFnType(mb!, i);
                if (interleave) builders[i].CreateType();   // 定义一个就创建一个（stage0 按需编译 fn 的方式）
            }
            double define = sw.Elapsed.TotalMilliseconds;
            var types = new Type[N];
            for (int i = 0; i < N; i++) types[i] = builders[i].CreateType()!;
            double create = sw.Elapsed.TotalMilliseconds - define;
            if (interleave) { create = define; define = 0; }
            if (!call)
            {
                Info(interleave ? $"{name}: {asmCount} 个程序集；DefineType+发射+CreateType 合计 {create:F0} ms"
                                : $"{name}: {asmCount} 个程序集；DefineType+发射 {define:F0} ms，CreateType {create:F0} ms");
                return;
            }
            long sum = 0;
            for (int i = 0; i < N; i++) sum += (int)((AFn)Activator.CreateInstance(types[i])!).invoke(null)!;
            double callMs = sw.Elapsed.TotalMilliseconds - define - create;
            long ws = Environment.WorkingSet - ws0;
            Check(name, sum == (long)N * (N - 1) / 2,
                $"{asmCount} 个程序集；DefineType+发射 {define:F0} ms，CreateType {create:F0} ms，实例化+首次调用（JIT）{callMs:F0} ms，合计 {define + create + callMs:F0} ms；工作集 +{ws / 1048576.0:F0} MB");
        }
        Run($"单个 RunAndCollect 程序集 {N} 类", N, AssemblyBuilderAccess.RunAndCollect);
        Run($"每 1000 类一个 RunAndCollect 程序集", 1000, AssemblyBuilderAccess.RunAndCollect);
        Run($"单个 Run 程序集 {N} 类", N, AssemblyBuilderAccess.Run);
        Run($"每 1000 类一个 Run 程序集", 1000, AssemblyBuilderAccess.Run);
        Run($"单个 RunAndCollect 程序集 {N} 类（每定义一个立即 CreateType）", N, AssemblyBuilderAccess.RunAndCollect, call: false, interleave: true);
        Run($"每 1000 类一个 RunAndCollect 程序集（每定义一个立即 CreateType）", 1000, AssemblyBuilderAccess.RunAndCollect, call: false, interleave: true);
        Console.WriteLine("-- 每个程序集的类型数与耗时（RunAndCollect，先全部定义再全部 CreateType，不实例化）");
        foreach (int per in new[] { 100, 500, 1000, 2000, 5000, 10000 }) Run($"每 {per} 类一个程序集", per, AssemblyBuilderAccess.RunAndCollect, call: false);

        Console.WriteLine("-- 跨程序集访问");
        // A：公开类型 fn0、非公开类型 fn1
        void CrossAssembly(string name, bool hidden, Action<AssemblyBuilder>? decorateA, Action<AssemblyBuilder, ModuleBuilder>? decorateB)
        {
            var (aAb, aMb) = Dyn("A_" + name);
            decorateA?.Invoke(aAb);
            var target = DefineFnType(aMb, 1, hidden ? TypeAttributes.NotPublic : TypeAttributes.Public).CreateType()!;
            var (bAb, bMb) = Dyn("B_" + name);
            decorateB?.Invoke(bAb, bMb);
            var tb = bMb.DefineType("Caller", TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed);
            var m = tb.DefineMethod("Call", MethodAttributes.Public | MethodAttributes.Static, typeof(object), Type.EmptyTypes);
            var il = m.GetILGenerator();
            il.Emit(OpCodes.Newobj, target.GetConstructor(Type.EmptyTypes)!);          // 跨程序集 newobj
            il.Emit(OpCodes.Ldnull);
            il.Emit(OpCodes.Call, target.GetMethod("invoke", new[] { typeof(object) })!); // 跨程序集非虚 call
            il.Emit(OpCodes.Ret);
            Type callerType;
            try { callerType = tb.CreateType()!; }
            catch (Exception e) { Info($"{name}: CreateType 抛 {e.GetType().Name}: {e.Message}"); return; }
            try
            {
                var r = callerType.GetMethod("Call")!.Invoke(null, null);
                Info($"{name}: 调用成功，返回 {r}");
            }
            catch (TargetInvocationException e)
            {
                Info($"{name}: 调用抛 {e.InnerException!.GetType().Name}: {e.InnerException.Message}");
            }
        }
        CrossAssembly("公开类型", hidden: false, null, null);
        CrossAssembly("非公开类型", hidden: true, null, null);
        CrossAssembly("非公开类型+InternalsVisibleTo", hidden: true,
            a => a.SetCustomAttribute(new CustomAttributeBuilder(typeof(InternalsVisibleToAttribute).GetConstructor(new[] { typeof(string) })!, new object[] { "B_非公开类型+InternalsVisibleTo" })),
            null);
        CrossAssembly("非公开类型+IgnoresAccessChecksTo", hidden: true, null, (b, mb) =>
        {
            // B 自己定义 System.Runtime.CompilerServices.IgnoresAccessChecksToAttribute（运行时按名字识别）
            var attr = mb.DefineType("System.Runtime.CompilerServices.IgnoresAccessChecksToAttribute", TypeAttributes.Public | TypeAttributes.Sealed, typeof(Attribute));
            var ctor = attr.DefineConstructor(MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName,
                CallingConventions.Standard, new[] { typeof(string) });
            var il = ctor.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Call, typeof(Attribute).GetConstructor(BindingFlags.NonPublic | BindingFlags.Instance, Type.EmptyTypes)!); il.Emit(OpCodes.Ret);
            attr.CreateType();
            b.SetCustomAttribute(new CustomAttributeBuilder(ctor, new object[] { "A_非公开类型+IgnoresAccessChecksTo" }));
        });
        // 非可回收程序集引用可回收程序集中的类型
        try
        {
            var (_, aMb) = Dyn("A_collectible", AssemblyBuilderAccess.RunAndCollect);
            var target = DefineFnType(aMb, 1).CreateType()!;
            var (_, bMb) = Dyn("B_noncollectible", AssemblyBuilderAccess.Run);
            var tb = bMb.DefineType("Caller", TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed);
            var m = tb.DefineMethod("Call", MethodAttributes.Public | MethodAttributes.Static, typeof(object), Type.EmptyTypes);
            var il = m.GetILGenerator();
            il.Emit(OpCodes.Newobj, target.GetConstructor(Type.EmptyTypes)!); il.Emit(OpCodes.Ldnull);
            il.Emit(OpCodes.Callvirt, typeof(AFn).GetMethod("invoke", new[] { typeof(object) })!); il.Emit(OpCodes.Ret);
            var r = tb.CreateType()!.GetMethod("Call")!.Invoke(null, null);
            Info($"Run 程序集引用 RunAndCollect 程序集的类型: 调用成功，返回 {r}");
        }
        catch (Exception e) { Info($"Run 程序集引用 RunAndCollect 程序集的类型: 抛 {(e.InnerException ?? e).GetType().Name}: {(e.InnerException ?? e).Message}"); }
        // 反向：可回收程序集引用非可回收程序集（上面公开类型一例已是 RunAndCollect→RunAndCollect；这里 RunAndCollect→Run）
        try
        {
            var (_, aMb) = Dyn("A_noncollectible2", AssemblyBuilderAccess.Run);
            var target = DefineFnType(aMb, 1).CreateType()!;
            var (_, bMb) = Dyn("B_collectible2", AssemblyBuilderAccess.RunAndCollect);
            var tb = bMb.DefineType("Caller", TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed);
            var m = tb.DefineMethod("Call", MethodAttributes.Public | MethodAttributes.Static, typeof(object), Type.EmptyTypes);
            var il = m.GetILGenerator();
            il.Emit(OpCodes.Newobj, target.GetConstructor(Type.EmptyTypes)!); il.Emit(OpCodes.Ldnull);
            il.Emit(OpCodes.Callvirt, typeof(AFn).GetMethod("invoke", new[] { typeof(object) })!); il.Emit(OpCodes.Ret);
            var r = tb.CreateType()!.GetMethod("Call")!.Invoke(null, null);
            Info($"RunAndCollect 程序集引用 Run 程序集的类型: 调用成功，返回 {r}");
        }
        catch (Exception e) { Info($"RunAndCollect 程序集引用 Run 程序集的类型: 抛 {(e.InnerException ?? e).GetType().Name}: {(e.InnerException ?? e).Message}"); }
    }

    // ---------------------------------------------------------------- 5. Monitor 与线程局部

    [ThreadStatic] static object? threadStatic;

    static void Threading()
    {
        Console.WriteLine("== 5. Monitor 可重入、ThreadLocal vs AsyncLocal");
        var o = new object();
        bool OtherThreadCanEnter()
        {
            bool got = false;
            var t = new Thread(() => { got = Monitor.TryEnter(o, 100); if (got) Monitor.Exit(o); });
            t.Start(); t.Join();
            return got;
        }
        Monitor.Enter(o);
        Monitor.Enter(o);   // 同线程第二次进入
        bool blocked1 = !OtherThreadCanEnter();
        Monitor.Exit(o);
        bool blocked2 = !OtherThreadCanEnter();   // 只退出一次，仍持有
        Monitor.Exit(o);
        bool free = OtherThreadCanEnter();
        Check("Monitor 同线程重入两次，需退出两次才释放", blocked1 && blocked2 && free && !Monitor.IsEntered(o));
        try { Monitor.Exit(o); Check("多余的 Exit 抛 SynchronizationLockException", false); }
        catch (SynchronizationLockException) { Check("多余的 Exit 抛 SynchronizationLockException", true); }

        Bench("Monitor.Enter/Exit 无竞争", 10_000_000, n => { for (int i = 0; i < n; i++) { Monitor.Enter(o); Monitor.Exit(o); } });
        Bench("Monitor.Enter/Exit 重入（嵌套两层）", 10_000_000, n => { for (int i = 0; i < n; i++) { Monitor.Enter(o); Monitor.Enter(o); Monitor.Exit(o); Monitor.Exit(o); } });

        var tl = new ThreadLocal<object?>();
        var al = new AsyncLocal<object?>();
        object v1 = new(), v2 = new();
        object? sink = null;
        tl.Value = v1; al.Value = v1; threadStatic = v1;
        Bench("[ThreadStatic] 读", 10_000_000, n => { for (int i = 0; i < n; i++) sink = threadStatic; });
        Bench("[ThreadStatic] 写", 10_000_000, n => { for (int i = 0; i < n; i++) threadStatic = (i & 1) == 0 ? v1 : v2; });
        Bench("ThreadLocal<object>.Value 读", 10_000_000, n => { for (int i = 0; i < n; i++) sink = tl.Value; });
        Bench("ThreadLocal<object>.Value 写（交替两个值）", 10_000_000, n => { for (int i = 0; i < n; i++) tl.Value = (i & 1) == 0 ? v1 : v2; });
        Bench("AsyncLocal<object>.Value 读", 10_000_000, n => { for (int i = 0; i < n; i++) sink = al.Value; });
        Bench("AsyncLocal<object>.Value 写（同一值，短路）", 10_000_000, n => { for (int i = 0; i < n; i++) al.Value = v1; });
        Bench("AsyncLocal<object>.Value 写（交替两个值）", 10_000_000, n => { for (int i = 0; i < n; i++) al.Value = (i & 1) == 0 ? v1 : v2; });
        // 多个 AsyncLocal 同时有值时写的代价（ExecutionContext 的局部值表变成字典）
        var others = Enumerable.Range(0, 8).Select(_ => new AsyncLocal<object?>()).ToArray();
        foreach (var x in others) x.Value = v1;
        Bench("AsyncLocal<object>.Value 写（另有 8 个 AsyncLocal 有值）", 10_000_000, n => { for (int i = 0; i < n; i++) al.Value = (i & 1) == 0 ? v1 : v2; });
        GC.KeepAlive(sink);

        // 语义差异：AsyncLocal 随 ExecutionContext 流到新任务/线程，ThreadLocal 不流
        tl.Value = v1; al.Value = v1;
        var flowed = Task.Run(() => (ReferenceEquals(tl.Value, v1), ReferenceEquals(al.Value, v1))).Result;
        Check("Task.Run 中 ThreadLocal 不流、AsyncLocal 流", !flowed.Item1 && flowed.Item2, $"ThreadLocal={flowed.Item1}, AsyncLocal={flowed.Item2}");
        object? inNewThread = v2; var th = new Thread(() => inNewThread = al.Value); th.Start(); th.Join();
        Check("new Thread 中 AsyncLocal 也流（ExecutionContext 默认捕获）", ReferenceEquals(inNewThread, v1));
        using (ExecutionContext.SuppressFlow())
        {
            var th2 = new Thread(() => inNewThread = al.Value); th2.Start(); th2.Join();
            Check("SuppressFlow 后 new Thread 中 AsyncLocal 为 null", inNewThread is null);
        }
    }
}
