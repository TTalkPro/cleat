using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Text;

namespace Cleat.Core.Compiler;

/// <summary>
/// 已装载程序集的 IL 文本（cleat-177，测试与诊断用）：每个类 <c>.class 名</c>，每个有体的方法 <c> .method 名(形参类型, …) : 返回类型</c>，
/// 其下每条指令一行（<c>  call clojure.lang.RT::aget(System.Int64[], System.Int32) : System.Int64</c>、<c>  isinst System.Int64</c>、
/// <c>  ldfld accinline.gen.Foo, eval$c4::a</c>）。成员属于另一个用户程序集（不是框架、Cleat.Core、镜像与随附库）时所属类型后带
/// <c>, 程序集名</c>（跨编译单元的引用）。按反射读已装载的程序集，eval 单元与包程序集都能看，不依赖编译器自己的转储格式。
/// </summary>
public static class IlText
{
    private static readonly Dictionary<short, OpCode> s_ops =
        typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static).Select(f => (OpCode)f.GetValue(null)).ToDictionary(o => o.Value);

    private static readonly HashSet<string> s_runtimeAssemblies = new HashSet<string>(StringComparer.Ordinal)
    {
        "Cleat.Core", "cleat.boot", "Cleat.Clojure.Libs", "Cleat.Compiler", "netstandard", "mscorlib",
    };

    /// <summary>按名字找已装载的程序集（包程序集先 require）。</summary>
    public static string Of(string assemblyName)
    {
        var a = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(x => x.GetName().Name == assemblyName)
            ?? throw new InvalidOperationException("IlText: assembly not loaded: " + assemblyName);
        return Of(a);
    }

    /// <summary>这个进程里全部 eval 单元（名字以 eval$ 开头的程序集），按装载顺序（名字里的序号）。</summary>
    public static string EvalUnits()
    {
        static long Seq(string n)
        {
            int i = n.Length;
            while (i > 0 && char.IsDigit(n[i - 1])) i--;
            return i < n.Length && long.TryParse(n.Substring(i), out long k) ? k : 0;
        }
        var sb = new StringBuilder();
        foreach (var a in AppDomain.CurrentDomain.GetAssemblies().Where(a => a.GetName().Name?.StartsWith("eval$", StringComparison.Ordinal) == true)
                     .OrderBy(a => Seq(a.GetName().Name)))
            sb.Append(Of(a));
        return sb.ToString();
    }

    public static string Of(Assembly a)
    {
        var sb = new StringBuilder();
        sb.Append(".assembly ").Append(a.GetName().Name).Append('\n');
        if (a.IsDynamic)
        {
            sb.Append(" (dynamic: no IL)\n");
            return sb.ToString();
        }
        Type[] types;
        try { types = a.GetTypes(); }
        catch (ReflectionTypeLoadException e) { types = e.Types.Where(t => t != null).ToArray(); }
        const BindingFlags all = BindingFlags.DeclaredOnly | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
        foreach (var t in types.OrderBy(t => t.FullName, StringComparer.Ordinal))
        {
            sb.Append(".class ").Append(t.FullName).Append('\n');
            var methods = t.GetConstructors(all).Cast<MethodBase>().Concat(t.GetMethods(all)).OrderBy(m => m.MetadataToken);
            foreach (var m in methods)
            {
                MethodBody body;
                try { body = m.GetMethodBody(); }
                catch (Exception) { body = null; }
                if (body == null) continue;
                sb.Append(" .method ").Append(m.Name).Append('(').Append(string.Join(", ", m.GetParameters().Select(p => Name(p.ParameterType))))
                  .Append(") : ").Append(m is MethodInfo mi ? Name(mi.ReturnType) : "System.Void").Append('\n');
                Decode(body.GetILAsByteArray(), m, a, sb);
            }
        }
        return sb.ToString();
    }

    private static string Name(Type t) => t.FullName ?? t.Name;

    private static bool IsUser(Assembly a)
    {
        string n = a.GetName().Name ?? "";
        return a != typeof(object).Assembly && !s_runtimeAssemblies.Contains(n)
            && !n.StartsWith("System.", StringComparison.Ordinal) && !n.StartsWith("Microsoft.", StringComparison.Ordinal);
    }

    private static string Owner(Type t, Assembly self) =>
        t == null ? "?" : Name(t) + (t.Assembly != self && IsUser(t.Assembly) ? ", " + t.Assembly.GetName().Name : "");

    private static string Member(MemberInfo mi, Assembly self) => mi switch
    {
        MethodBase mb => Owner(mb.DeclaringType, self) + "::" + mb.Name + "(" + string.Join(", ", mb.GetParameters().Select(p => Name(p.ParameterType))) + ") : "
                         + (mb is MethodInfo m ? Name(m.ReturnType) : "System.Void"),
        FieldInfo f => Owner(f.DeclaringType, self) + "::" + f.Name,
        Type t => Owner(t, self),
        _ => mi?.ToString() ?? "?",
    };

    private static void Decode(byte[] il, MethodBase m, Assembly self, StringBuilder sb)
    {
        var module = m.Module;
        Type[] typeArgs = m.DeclaringType is { IsGenericType: true } dt ? dt.GetGenericArguments() : null;
        Type[] methodArgs = m.IsGenericMethod ? m.GetGenericArguments() : null;
        int i = 0;
        while (i < il.Length)
        {
            short v = il[i++];
            if (v == 0xFE) v = unchecked((short)(0xFE00 | il[i++]));
            if (!s_ops.TryGetValue(v, out var op))
            {
                sb.Append("  ?? 0x").Append(v.ToString("X")).Append('\n');
                return;
            }
            sb.Append("  ").Append(op.Name);
            int token;
            string operand = null;
            switch (op.OperandType)
            {
                case OperandType.InlineNone: break;
                case OperandType.ShortInlineBrTarget: i += 1; break;
                case OperandType.ShortInlineI:
                case OperandType.ShortInlineVar: operand = il[i].ToString(); i += 1; break;
                case OperandType.InlineVar: operand = BitConverter.ToUInt16(il, i).ToString(); i += 2; break;
                case OperandType.InlineBrTarget: i += 4; break;
                case OperandType.InlineI: operand = BitConverter.ToInt32(il, i).ToString(); i += 4; break;
                case OperandType.ShortInlineR: operand = BitConverter.ToSingle(il, i).ToString("R"); i += 4; break;
                case OperandType.InlineI8: operand = BitConverter.ToInt64(il, i).ToString(); i += 8; break;
                case OperandType.InlineR: operand = BitConverter.ToDouble(il, i).ToString("R"); i += 8; break;
                case OperandType.InlineSwitch: i += 4 + 4 * BitConverter.ToInt32(il, i); break;
                case OperandType.InlineSig: i += 4; break;
                case OperandType.InlineString:
                    token = BitConverter.ToInt32(il, i);
                    i += 4;
                    try { operand = "\"" + module.ResolveString(token) + "\""; }
                    catch (Exception) { operand = "string 0x" + token.ToString("X8"); }
                    break;
                case OperandType.InlineMethod:
                case OperandType.InlineField:
                case OperandType.InlineType:
                case OperandType.InlineTok:
                    token = BitConverter.ToInt32(il, i);
                    i += 4;
                    try { operand = Member(module.ResolveMember(token, typeArgs, methodArgs), self); }
                    catch (Exception) { operand = "token 0x" + token.ToString("X8"); }
                    break;
            }
            if (operand != null) sb.Append(' ').Append(operand);
            sb.Append('\n');
        }
    }
}
