using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;

namespace Cleat.Emit;

// Lcil 对象模型（LCIL.md §2 ~ §4）与解析、校验。从 IronScheme（BSD）的 Model.cs 移植并扩展：接口实现、volatile 字段、程序集属性、runtime 子句。

public sealed class LcilAssembly
{
    public string Name;
    public List<string> References = new List<string>();
    public string EntryType, EntryMethod;
    /// <summary>(runtime scheme|clojure)：互操作指令与常量编码按哪个运行时解释（LCIL.md §2）；方法可单独覆盖。缺省 clojure。</summary>
    public string Runtime = LcilRuntime.Clojure;
    /// <summary>(attribute "T" "字符串参数" …)：程序集级自定义属性。</summary>
    public List<(string Type, List<string> Args)> Attributes = new List<(string, List<string>)>();
    /// <summary>(resource "名字" "文件路径")：嵌入资源（保存 dll 时读入文件）。</summary>
    public List<(string Name, string Path)> Resources = new List<(string, string)>();
    /// <summary>(lazy-start)：每个类型初始化器先调 RuntimeBootstrap.EnsureStarted（包程序集，cleat-067）。</summary>
    public bool LazyStart;
    /// <summary>常量表：下标 → 编码（§3），保持原样交给常量编码器。</summary>
    public List<SNode> Constants = new List<SNode>();
    /// <summary>(live-constants n)：常量由宿主在内存中直接提供（只用于内存模式），个数为 n。</summary>
    public int LiveConstants = -1;
    public int ConstantCount => LiveConstants >= 0 ? LiveConstants : Constants.Count;
    public List<LcilType> Types = new List<LcilType>();
    public string File;
}

/// <summary>Lcil 方法 / 常量表所属的运行时（互操作决议与值表示的规则，designs/15 §1）。</summary>
public static class LcilRuntime
{
    public const string Clojure = "clojure", Scheme = "scheme";
    public static readonly string[] All = { Clojure, Scheme };
}

public sealed class LcilType
{
    public string Name, Base = "System.Object";
    public HashSet<string> Attrs = new HashSet<string>();
    public List<string> Interfaces = new List<string>();
    /// <summary>(attribute "T" "字符串参数" …)：类型级自定义属性，构造函数只取字符串参数。</summary>
    public List<(string Type, List<string> Args)> Attributes = new List<(string, List<string>)>();
    public List<LcilField> Fields = new List<LcilField>();
    public List<LcilMethod> Methods = new List<LcilMethod>();
    public SNode Pos;
}

public sealed class LcilField
{
    public string Name, Type;
    public HashSet<string> Attrs = new HashSet<string>();
    public bool IsStatic => Attrs.Contains("static");
    public bool IsVolatile => Attrs.Contains("volatile");
}

public sealed class LcilMethod
{
    public string Name, Ret = "System.Void", Source;
    public HashSet<string> Attrs = new HashSet<string>();
    public List<string> Params = new List<string>();
    public List<string> Locals = new List<string>();
    public List<string> Namespaces = new List<string>();
    /// <summary>(implements "I" "名字")：显式实现接口 I 的方法（DefineMethodOverride）；此时本方法可以是 private。</summary>
    public string ImplementsType, ImplementsName;
    /// <summary>(runtime scheme|clojure)：本方法互操作指令的运行时；null 时沿用程序集的。</summary>
    public string Runtime;
    public List<Instr> Body = new List<Instr>();
    public SNode Pos;
    public bool IsStatic => Attrs.Contains("static") || Attrs.Contains("cctor");
    public bool IsCtor => Attrs.Contains("ctor");
    public bool IsCctor => Attrs.Contains("cctor");
}

/// <summary>方法引用："T" "名字" ("P"…) "R"；构造函数为 "T" ("P"…)。</summary>
public sealed class MethodRef
{
    public string Type, Name;
    public List<string> Params;
    public string Ret;
    public override string ToString() => $"{Type}::{Name}({string.Join(",", Params)})";
}

public sealed class FieldRef
{
    public string Type, Name;
}

public enum InstrKind { Cil, Label, LdConst, Try, Line, Tail, Clr }

public sealed class Instr
{
    public InstrKind Kind;
    public OpCode OpCode;          // Kind == Cil
    public object Operand;         // long / double / string / int(下标) / 标签名 / 类型名 / FieldRef / MethodRef / string[](switch)
    public SNode Pos;
    // Try
    public List<Instr> TryBody;
    public List<(string Type, List<Instr> Body)> Catches;
    public List<Instr> Finally;
    // Line
    public int L1, C1, L2, C2;
    // Clr
    public string ClrOp;           // call field-get field-set new new-array cast is type
    public string ClrType;         // null 表示 #f（运行时类型）
    public string ClrMember;
    public List<string> ClrParams; // 成员说明中显式的参数类型；null 表示未指定
    public List<string> ClrHints;  // 编译器已知的实参类型（实参都是 clr-cast 时）：有完全匹配的重载就静态调用，否则照常决议（iron-033）
    public List<string> ClrGenerics; // 泛型方法的类型参数；null 表示非泛型
    public bool ClrStatic;
    public int ClrArgc;
}

public static class LcilParser
{
    private static readonly Dictionary<string, OpCode> s_opcodes =
      typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
        .Select(f => (OpCode)f.GetValue(null))
        .Where(o => o.OpCodeType != OpCodeType.Nternal)
        .ToDictionary(o => o.Name, o => o);

    public static bool TryGetOpCode(string name, out OpCode op) => s_opcodes.TryGetValue(name, out op);

    /// <summary>已读入的 (lcil …) 树 → 模型（EmitInMemoryData 使用）。</summary>
    public static LcilAssembly FromTree(SList top, string file) => new Parser(file).Top(top);

    public static LcilAssembly Parse(string text, string file = "lcil")
    {
        var items = new SExprReader(text, file).ReadAll();
        if (items.Count != 1 || !(items[0] is SList top) || top.Head != "lcil")
            throw new LcilException($"{file}:1:1: expected a single (lcil 1 ...) form");
        var p = new Parser(file);
        return p.Top(top);
    }

    private sealed class Parser
    {
        private readonly string _file;
        public Parser(string file) { _file = file; }

        private LcilException Err(SNode n, string msg) => new LcilException($"{n.Where(_file)}: {msg}");

        private SList List(SNode n, string what) => n as SList ?? throw Err(n, "expected a list for " + what);
        private string Str(SNode n, string what) => n is SStr s ? s.Value : throw Err(n, "expected a string for " + what);
        private string Sym(SNode n, string what) => n is SSym s ? s.Name : throw Err(n, "expected a symbol for " + what);
        private long Int(SNode n, string what) => n is SInt i ? i.Value : throw Err(n, "expected an integer for " + what);

        private void Arity(SList l, int min, int max = -1)
        {
            int n = l.Count - 1;
            if (n < min || (max >= 0 && n > max)) throw Err(l, $"wrong number of operands for {l.Head}");
        }

        private List<string> Strings(SList l, int from, string what) => l.Items.Skip(from).Select(x => Str(x, what)).ToList();

        public LcilAssembly Top(SList top)
        {
            var asm = new LcilAssembly { File = _file };
            if (top.Count < 2 || !(top[1] is SInt v)) throw Err(top, "missing format version");
            if (v.Value != 1) throw Err(top[1], $"unsupported Lcil version {v.Value}");
            bool sawAssembly = false;
            foreach (var item in top.Items.Skip(2))
            {
                var l = List(item, "top-level clause");
                switch (l.Head)
                {
                    case "assembly":
                        if (sawAssembly) throw Err(l, "duplicate assembly clause");
                        sawAssembly = true;
                        Arity(l, 1);
                        asm.Name = Str(l[1], "assembly name");
                        foreach (var c in l.Items.Skip(2))
                        {
                            var cl = List(c, "assembly clause");
                            switch (cl.Head)
                            {
                                case "references": asm.References.AddRange(Strings(cl, 1, "reference")); break;
                                case "entry": Arity(cl, 2, 2); asm.EntryType = Str(cl[1], "entry type"); asm.EntryMethod = Str(cl[2], "entry method"); break;
                                case "attribute": Arity(cl, 1); asm.Attributes.Add((Str(cl[1], "attribute type"), Strings(cl, 2, "attribute argument"))); break;
                                case "lazy-start": Arity(cl, 0, 0); asm.LazyStart = true; break;
                                case "resource": Arity(cl, 2, 2); asm.Resources.Add((Str(cl[1], "resource name"), Str(cl[2], "resource file"))); break;
                                case "runtime": Arity(cl, 1, 1); asm.Runtime = Check(cl[1], "runtime", LcilRuntime.All); break;
                                default: throw Err(cl, "unknown assembly clause: " + cl.Head);
                            }
                        }
                        break;
                    case "constants":
                        foreach (var c in l.Items.Skip(1))
                        {
                            var cl = List(c, "constant");
                            if (cl.Count != 2) throw Err(cl, "constant must be (index encoding)");
                            if (Int(cl[0], "constant index") != asm.Constants.Count) throw Err(cl[0], $"constant index must be {asm.Constants.Count}");
                            asm.Constants.Add(cl[1]);
                        }
                        break;
                    case "live-constants":
                        Arity(l, 1, 1);
                        if (asm.Constants.Count > 0) throw Err(l, "live-constants and constants are exclusive");
                        asm.LiveConstants = (int)Int(l[1], "constant count");
                        break;
                    case "type": asm.Types.Add(Type(l)); break;
                    default: throw Err(l, "unknown top-level clause: " + l.Head);
                }
            }
            if (!sawAssembly) throw Err(top, "missing assembly clause");
            var dup = asm.Types.GroupBy(t => t.Name).FirstOrDefault(g => g.Count() > 1);
            if (dup != null) throw Err(dup.Skip(1).First().Pos, "duplicate type " + dup.Key);
            foreach (var t in asm.Types)
            {
                if (t.Attrs.Contains("interface"))
                {
                    if (t.Fields.Any(f => !f.IsStatic)) throw Err(t.Pos, "interface cannot have instance fields");
                    foreach (var m in t.Methods)
                        if (!m.Attrs.Contains("abstract") && !m.IsStatic) throw Err(m.Pos, "interface methods must be abstract (or static)");
                }
                foreach (var m in t.Methods)
                {
                    if (m.ImplementsType != null && !t.Interfaces.Contains(m.ImplementsType))
                        throw Err(m.Pos, $"implements {m.ImplementsType} but the type does not list it in (interfaces ...)");
                    Validate(m, asm);
                }
            }
            return asm;
        }

        private LcilType Type(SList l)
        {
            Arity(l, 1);
            var t = new LcilType { Name = Str(l[1], "type name"), Pos = l };
            foreach (var c in l.Items.Skip(2))
            {
                var cl = List(c, "type clause");
                switch (cl.Head)
                {
                    case "base": Arity(cl, 1, 1); t.Base = Str(cl[1], "base type"); break;
                    case "attrs": foreach (var a in cl.Items.Skip(1)) t.Attrs.Add(Check(a, "type attribute", "public", "sealed", "abstract", "interface")); break;
                    case "interfaces": t.Interfaces.AddRange(Strings(cl, 1, "interface")); break;
                    case "attribute": Arity(cl, 1); t.Attributes.Add((Str(cl[1], "attribute type"), Strings(cl, 2, "attribute argument"))); break;
                    case "field":
                        {
                            Arity(cl, 2);
                            var f = new LcilField { Name = Str(cl[1], "field name"), Type = Str(cl[2], "field type") };
                            foreach (var a in cl.Items.Skip(3)) f.Attrs.Add(Check(a, "field attribute", "static", "public", "private", "readonly", "volatile"));
                            t.Fields.Add(f);
                            break;
                        }
                    case "method": t.Methods.Add(Method(cl)); break;
                    default: throw Err(cl, "unknown type clause: " + cl.Head);
                }
            }
            return t;
        }

        private string Check(SNode n, string what, params string[] allowed)
        {
            string s = Sym(n, what);
            if (Array.IndexOf(allowed, s) < 0) throw Err(n, $"unknown {what}: {s}");
            return s;
        }

        private LcilMethod Method(SList l)
        {
            Arity(l, 1);
            var m = new LcilMethod { Name = Str(l[1], "method name"), Pos = l };
            SList body = null;
            foreach (var c in l.Items.Skip(2))
            {
                var cl = List(c, "method clause");
                switch (cl.Head)
                {
                    case "attrs":
                        foreach (var a in cl.Items.Skip(1))
                            m.Attrs.Add(Check(a, "method attribute", "static", "virtual", "override", "abstract", "public", "private", "protected", "ctor", "cctor", "optimize", "async"));
                        break;
                    case "params": m.Params = Strings(cl, 1, "parameter type"); break;
                    case "ret": Arity(cl, 1, 1); m.Ret = Str(cl[1], "return type"); break;
                    case "locals": m.Locals = Strings(cl, 1, "local type"); break;
                    case "clr-namespaces": m.Namespaces = Strings(cl, 1, "namespace"); break;
                    case "source": Arity(cl, 1, 1); m.Source = Str(cl[1], "source file"); break;
                    case "implements": Arity(cl, 2, 2); m.ImplementsType = Str(cl[1], "interface"); m.ImplementsName = Str(cl[2], "interface method"); break;
                    case "runtime": Arity(cl, 1, 1); m.Runtime = Check(cl[1], "runtime", LcilRuntime.All); break;
                    case "maxstack": break;   // 发射器自动计算，接受并忽略
                    case "body": body = cl; break;
                    default: throw Err(cl, "unknown method clause: " + cl.Head);
                }
            }
            if (m.IsCtor && m.Name != ".ctor") throw Err(l, "constructor must be named .ctor");
            if (m.IsCctor && m.Name != ".cctor") throw Err(l, "type initializer must be named .cctor");
            if (m.ImplementsType != null && (m.IsStatic || m.IsCtor || m.IsCctor || m.Attrs.Contains("abstract"))) throw Err(l, "implements needs an instance method with a body");
            if (body == null)
            {
                if (!m.Attrs.Contains("abstract")) throw Err(l, "method has no body");
            }
            else m.Body = Instrs(body.Items.Skip(1));
            return m;
        }

        private List<Instr> Instrs(IEnumerable<SNode> nodes) => nodes.Select(n => Instruction(List(n, "instruction"))).ToList();

        private Instr Instruction(SList l)
        {
            string op = l.Head ?? throw Err(l, "instruction must start with a symbol");
            var ins = new Instr { Pos = l };
            switch (op)
            {
                case "label": Arity(l, 1, 1); ins.Kind = InstrKind.Label; ins.Operand = Sym(l[1], "label"); return ins;
                case "ldconst": Arity(l, 1, 1); ins.Kind = InstrKind.LdConst; ins.Operand = (int)Int(l[1], "constant index"); return ins;
                case "tail": Arity(l, 0, 0); ins.Kind = InstrKind.Tail; return ins;
                case "line":
                    Arity(l, 4, 4);
                    ins.Kind = InstrKind.Line;
                    ins.L1 = (int)Int(l[1], "line"); ins.C1 = (int)Int(l[2], "column"); ins.L2 = (int)Int(l[3], "line"); ins.C2 = (int)Int(l[4], "column");
                    return ins;
                case "try": return Try(l, ins);
            }
            if (op.StartsWith("clr-", StringComparison.Ordinal)) return Clr(l, ins, op.Substring(4));
            if (!s_opcodes.TryGetValue(op, out var oc)) throw Err(l, "unknown instruction: " + op);
            ins.Kind = InstrKind.Cil;
            ins.OpCode = oc;
            switch (oc.OperandType)
            {
                case OperandType.InlineNone: Arity(l, 0, 0); break;
                case OperandType.ShortInlineI: case OperandType.InlineI: Arity(l, 1, 1); ins.Operand = Int(l[1], "integer operand"); break;
                case OperandType.InlineI8: Arity(l, 1, 1); ins.Operand = Int(l[1], "integer operand"); break;
                case OperandType.ShortInlineR: case OperandType.InlineR: Arity(l, 1, 1); ins.Operand = Real(l[1]); break;
                case OperandType.InlineString: Arity(l, 1, 1); ins.Operand = Str(l[1], "string operand"); break;
                case OperandType.ShortInlineVar: case OperandType.InlineVar: Arity(l, 1, 1); ins.Operand = (int)Int(l[1], "index"); break;
                case OperandType.ShortInlineBrTarget: case OperandType.InlineBrTarget: Arity(l, 1, 1); ins.Operand = Sym(l[1], "label"); break;
                case OperandType.InlineSwitch:
                    Arity(l, 1, 1);
                    ins.Operand = List(l[1], "switch labels").Items.Select(x => Sym(x, "label")).ToArray();
                    break;
                case OperandType.InlineType: case OperandType.InlineTok: Arity(l, 1, 1); ins.Operand = Str(l[1], "type"); break;
                case OperandType.InlineField: Arity(l, 2, 2); ins.Operand = new FieldRef { Type = Str(l[1], "type"), Name = Str(l[2], "field") }; break;
                case OperandType.InlineMethod:
                    if (oc == OpCodes.Newobj)
                    {
                        Arity(l, 2, 2);
                        ins.Operand = new MethodRef { Type = Str(l[1], "type"), Name = ".ctor", Params = Strings(List(l[2], "parameter types"), 0, "parameter type"), Ret = "System.Void" };
                    }
                    else
                    {
                        Arity(l, 4, 4);
                        ins.Operand = new MethodRef { Type = Str(l[1], "type"), Name = Str(l[2], "method"), Params = Strings(List(l[3], "parameter types"), 0, "parameter type"), Ret = Str(l[4], "return type") };
                    }
                    break;
                default: throw Err(l, "unsupported instruction: " + op);
            }
            return ins;
        }

        private double Real(SNode n) => n switch
        {
            SReal r => r.Value,
            SInt i => i.Value,
            SSym { Name: "+inf.0" } => double.PositiveInfinity,
            SSym { Name: "-inf.0" } => double.NegativeInfinity,
            SSym { Name: "+nan.0" } => double.NaN,
            _ => throw Err(n, "expected a number"),
        };

        private Instr Try(SList l, Instr ins)
        {
            ins.Kind = InstrKind.Try;
            ins.Catches = new List<(string, List<Instr>)>();
            if (l.Count < 3) throw Err(l, "try needs a body and at least one handler");
            var body = List(l[1], "try body");
            if (body.Head != "body") throw Err(body, "expected (body ...)");
            ins.TryBody = Instrs(body.Items.Skip(1));
            foreach (var h in l.Items.Skip(2))
            {
                var hl = List(h, "handler");
                switch (hl.Head)
                {
                    case "catch":
                        Arity(hl, 1);
                        if (ins.Finally != null) throw Err(hl, "catch after finally");
                        ins.Catches.Add((Str(hl[1], "exception type"), Instrs(hl.Items.Skip(2))));
                        break;
                    case "finally":
                        if (ins.Finally != null) throw Err(hl, "duplicate finally");
                        ins.Finally = Instrs(hl.Items.Skip(1));
                        break;
                    default: throw Err(hl, "unknown handler: " + hl.Head);
                }
            }
            return ins;
        }

        private Instr Clr(SList l, Instr ins, string op)
        {
            ins.Kind = InstrKind.Clr;
            ins.ClrOp = op;
            string TypeOperand(SNode n) => n is SBool { Value: false } ? null : Str(n, "type");
            switch (op)
            {
                case "call":
                    Arity(l, 4, 5);
                    ins.ClrType = TypeOperand(l[1]);
                    if (l[2] is SList ml)
                    {
                        // ("名字" ("P" …)) 或 ("名字" ("P" …)|#f ("G" …))
                        if (ml.Count != 2 && ml.Count != 3) throw Err(ml, "member must be (\"name\" (\"P\" ...) [(\"G\" ...)])");
                        ins.ClrMember = Str(ml[0], "member");
                        ins.ClrParams = ml[1] is SBool { Value: false } ? null : Strings(List(ml[1], "parameter types"), 0, "parameter type");
                        if (ml.Count == 3) ins.ClrGenerics = Strings(List(ml[2], "generic arguments"), 0, "generic argument");
                    }
                    else ins.ClrMember = Str(l[2], "member");
                    ins.ClrStatic = l[3] is SBool b ? b.Value : throw Err(l[3], "expected #t or #f");
                    ins.ClrArgc = (int)Int(l[4], "argument count");
                    if (ins.ClrStatic && ins.ClrType == null) throw Err(l, "static call needs a type");
                    if (l.Count == 6) ins.ClrHints = Strings(List(l[5], "argument types"), 0, "argument type");
                    break;
                case "field-get":
                case "field-set":
                    Arity(l, 3, 3);
                    ins.ClrType = TypeOperand(l[1]);
                    ins.ClrMember = Str(l[2], "field");
                    ins.ClrStatic = l[3] is SBool fb ? fb.Value : throw Err(l[3], "expected #t or #f");
                    if (ins.ClrStatic && ins.ClrType == null) throw Err(l, "static field needs a type");
                    break;
                case "new":
                    Arity(l, 2, 3);
                    ins.ClrType = Str(l[1], "type");
                    ins.ClrArgc = (int)Int(l[2], "argument count");
                    if (l.Count == 4) ins.ClrHints = Strings(List(l[3], "argument types"), 0, "argument type");
                    break;
                case "new-array": case "cast": case "is": case "type": Arity(l, 1, 1); ins.ClrType = Str(l[1], "type"); break;
                default: throw Err(l, "unknown instruction: clr-" + op);
            }
            return ins;
        }

        // —— 校验：标签、下标、常量、tail 前缀 ——

        private void Validate(LcilMethod m, LcilAssembly asm)
        {
            int argc = m.Params.Count + (m.IsStatic ? 0 : 1);
            var labels = new HashSet<string>();
            var used = new List<Instr>();
            void Walk(List<Instr> list)
            {
                for (int i = 0; i < list.Count; i++)
                {
                    var ins = list[i];
                    switch (ins.Kind)
                    {
                        case InstrKind.Label:
                            if (!labels.Add((string)ins.Operand)) throw Err(ins.Pos, "duplicate label " + ins.Operand);
                            break;
                        case InstrKind.LdConst:
                            if ((int)ins.Operand < 0 || (int)ins.Operand >= asm.ConstantCount) throw Err(ins.Pos, "constant index out of range");
                            break;
                        case InstrKind.Tail:
                            if (i + 1 >= list.Count || list[i + 1].Kind != InstrKind.Cil || !(list[i + 1].OpCode == OpCodes.Call || list[i + 1].OpCode == OpCodes.Callvirt))
                                throw Err(ins.Pos, "(tail) must be followed by call or callvirt");
                            if (i + 2 >= list.Count || list[i + 2].Kind != InstrKind.Cil || list[i + 2].OpCode != OpCodes.Ret)
                                throw Err(ins.Pos, "tail call must be followed by ret");
                            break;
                        case InstrKind.Line:
                            if (m.Source == null) throw Err(ins.Pos, "(line ...) needs a (source ...) clause");
                            break;
                        case InstrKind.Try:
                            Walk(ins.TryBody);
                            foreach (var c in ins.Catches) Walk(c.Body);
                            if (ins.Finally != null) Walk(ins.Finally);
                            break;
                        case InstrKind.Cil:
                            if (ins.OpCode.OperandType is OperandType.InlineVar or OperandType.ShortInlineVar)
                            {
                                int idx = (int)ins.Operand;
                                bool isArg = ins.OpCode.Name.Contains("arg");
                                int limit = isArg ? argc : m.Locals.Count;
                                if (idx < 0 || idx >= limit) throw Err(ins.Pos, (isArg ? "argument" : "local") + " index out of range: " + idx);
                            }
                            else if (ins.OpCode.Name.StartsWith("ldarg.", StringComparison.Ordinal) && char.IsDigit(ins.OpCode.Name[^1]) && ins.OpCode.Name[^1] - '0' >= argc)
                                throw Err(ins.Pos, "argument index out of range");
                            else if ((ins.OpCode.Name.StartsWith("ldloc.", StringComparison.Ordinal) || ins.OpCode.Name.StartsWith("stloc.", StringComparison.Ordinal))
                                     && char.IsDigit(ins.OpCode.Name[^1]) && ins.OpCode.Name[^1] - '0' >= m.Locals.Count)
                                throw Err(ins.Pos, "local index out of range");
                            if (ins.Operand is string || ins.Operand is string[]) used.Add(ins);
                            break;
                    }
                }
            }
            Walk(m.Body);
            foreach (var ins in used)
            {
                if (ins.OpCode.OperandType is OperandType.InlineBrTarget or OperandType.ShortInlineBrTarget && !labels.Contains((string)ins.Operand))
                    throw Err(ins.Pos, "undefined label " + ins.Operand);
                if (ins.Operand is string[] targets)
                    foreach (var t in targets) if (!labels.Contains(t)) throw Err(ins.Pos, "undefined label " + t);
            }
        }
    }
}
