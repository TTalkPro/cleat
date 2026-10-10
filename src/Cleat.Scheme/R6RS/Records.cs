using System;
using System.Collections.Generic;
using System.Linq;

namespace Cleat.Scheme;

/// <summary>
/// 记录实例：RTD + 字段数组（父类型的字段在前）。所有记录都用这一种表示，不再运行时生成 CLR 类型。
/// 设计：designs/02-runtime.md §7。
/// </summary>
public class Record
{
    public readonly RecordTypeDescriptor Rtd;
    public readonly object[] Fields;

    public Record(RecordTypeDescriptor rtd, object[] fields)
    {
        Rtd = rtd;
        Fields = fields;
    }

    public override string ToString() => Rtd == null ? "#<record>" : $"#<record {Rtd.Name}>";
}

/// <summary>R6RS 记录类型描述符。成员名与旧运行时一致（boot/records/inspection.sls 直接访问）。</summary>
public sealed class RecordTypeDescriptor
{
    public string Name { get; }
    /// <summary>uid：符号（非生成性）、字符串（旧系统中的生成性 uid）或 #f。</summary>
    public object uid;
    public RecordTypeDescriptor Parent { get; }
    public FieldDescriptor[] Fields { get; }

    internal readonly bool _sealed, _opaque, _generative;
    public object Sealed => RT.Bool(_sealed);
    public object Opaque => RT.Bool(_opaque);
    public object Generative => RT.Bool(_generative);

    /// <summary>是否条件类型：名字为 &amp;condition 或父类型是条件类型（与旧系统相同的判定）。</summary>
    public bool IsCondition { get; }

    /// <summary>父类型字段总数（本类型字段在字段数组中的起始下标）。</summary>
    public int FieldOffset { get; }
    public int TotalFieldCount => FieldOffset + Fields.Length;

    // ancestors[d] 为深度 d 的祖先（含自身），使子类型判断为 O(1)
    private readonly RecordTypeDescriptor[] _ancestors;

    public Procedure Predicate { get; }

    internal RecordTypeDescriptor(string name, RecordTypeDescriptor parent, object uid, bool @sealed, bool opaque, bool generative,
      IList<(string Name, bool Mutable)> fields)
    {
        Name = name;
        Parent = parent;
        this.uid = uid;
        _sealed = @sealed;
        _opaque = opaque || (parent?._opaque ?? false);   // R6RS：父类型不透明则子类型也不透明
        _generative = generative;
        IsCondition = name == "&condition" || (parent?.IsCondition ?? false);
        FieldOffset = parent?.TotalFieldCount ?? 0;
        _ancestors = parent == null ? new[] { this } : parent._ancestors.Append(this).ToArray();
        Fields = fields.Select((f, i) => new FieldDescriptor(this, f.Name, f.Mutable, FieldOffset + i)).ToArray();
        Predicate = new RecordPredicate(this);
    }

    /// <summary>obj 是否本类型或其子类型的实例。</summary>
    public bool IsInstance(object obj) =>
      obj is Record r && r.Rtd != null && r.Rtd._ancestors.Length > _ancestors.Length - 1 && ReferenceEquals(r.Rtd._ancestors[_ancestors.Length - 1], this);

    public IEnumerable<FieldDescriptor> GetAllFields() =>
      Parent == null ? Fields : Parent.GetAllFields().Concat(Fields);

    internal Record Instantiate(object[] fields) => IsCondition ? new Condition(this, fields) : new Record(this, fields);

    public override string ToString() => "rtd: " + Name;

    private sealed class RecordPredicate : Procedure
    {
        private readonly RecordTypeDescriptor _rtd;
        public RecordPredicate(RecordTypeDescriptor rtd) { _rtd = rtd; }
        public override string Name => _rtd.Name + "?";
        public override object Invoke1(object obj) => RT.Bool(_rtd.IsInstance(obj));
    }
}

/// <summary>字段描述符。成员名与旧运行时一致。</summary>
public sealed class FieldDescriptor
{
    public string Name { get; }
    internal readonly bool _mutable;
    public object Mutable => RT.Bool(_mutable);
    /// <summary>在记录字段数组中的绝对下标。</summary>
    public int Index { get; }
    public Procedure Accessor { get; }
    /// <summary>可变字段为修改器过程；不可变字段为 #f（与旧系统一致）。</summary>
    public object Mutator { get; }
    /// <summary>字段类型：统一表示下都是 object（旧系统可声明 CLR 类型）。</summary>
    public Type Type => typeof(object);

    internal FieldDescriptor(RecordTypeDescriptor rtd, string name, bool mutable, int index)
    {
        Name = name;
        _mutable = mutable;
        Index = index;
        Accessor = new FieldAccessor(rtd, this);
        Mutator = mutable ? new FieldMutator(rtd, this) : RT.False;
    }

    public override string ToString() => "fd: " + Name;

    private sealed class FieldAccessor : Procedure
    {
        private readonly RecordTypeDescriptor _rtd; private readonly FieldDescriptor _fd;
        public FieldAccessor(RecordTypeDescriptor rtd, FieldDescriptor fd) { _rtd = rtd; _fd = fd; }
        public override string Name => _rtd.Name + "-" + _fd.Name;
        public override object Invoke1(object obj) =>
          _rtd.IsInstance(obj) ? ((Record)obj).Fields[_fd.Index] : Errors.AssertionViolation(Name, "not a record of type " + _rtd.Name, obj);
    }

    private sealed class FieldMutator : Procedure
    {
        private readonly RecordTypeDescriptor _rtd; private readonly FieldDescriptor _fd;
        public FieldMutator(RecordTypeDescriptor rtd, FieldDescriptor fd) { _rtd = rtd; _fd = fd; }
        public override string Name => _rtd.Name + "-" + _fd.Name + "-set!";
        public override object Invoke2(object obj, object value)
        {
            if (!_rtd.IsInstance(obj)) return Errors.AssertionViolation(Name, "not a record of type " + _rtd.Name, obj);
            ((Record)obj).Fields[_fd.Index] = value;
            return Unspecified.Instance;
        }
    }
}

/// <summary>R6RS 记录构造描述符。字段名与旧运行时一致。</summary>
public sealed class RecordConstructorDescriptor
{
    public RecordTypeDescriptor type;
    public Procedure protocol;
    public RecordConstructorDescriptor parent;

    public override string ToString() => "rcd: " + type.Name;
}

/// <summary>记录的过程层与检视层原语。</summary>
public class Records : Builtins
{
    private static readonly Dictionary<string, RecordTypeDescriptor> s_nongenerative = new Dictionary<string, RecordTypeDescriptor>();
    private static readonly object s_gate = new object();

    private static readonly Symbol s_mutableSym = Symbol.Intern("mutable"), s_immutableSym = Symbol.Intern("immutable");

    /// <summary>(make-record-type-descriptor name parent uid sealed? opaque? fields)</summary>
    [Primitive("make-record-type-descriptor")]
    public static object MakeRecordTypeDescriptor(object name, object parent, object uid, object issealed, object isopaque, object fields)
    {
        string n = RequiresNotNull<Symbol>(name).Name;
        var prtd = parent as RecordTypeDescriptor;
        if (prtd == null && IsTrue(parent))
            return AssertionViolation("make-record-type-descriptor", "parent is not a record type descriptor", parent);
        if (prtd != null && prtd._sealed)
            return AssertionViolation("make-record-type-descriptor", "parent record type is sealed", parent);

        string id = uid is Symbol s ? s.Name : uid as string;
        bool @sealed = RequiresNotNull<bool>(issealed);
        bool opaque = RequiresNotNull<bool>(isopaque);

        var specs = new List<(string, bool)>();
        foreach (var f in RequiresNotNull<object[]>(fields))
        {
            var c = f as Cons;
            var kind = c?.car as Symbol;
            var fname = (c?.cdr as Cons)?.car as Symbol;
            if (fname == null || (kind != s_mutableSym && kind != s_immutableSym))
                return AssertionViolation("make-record-type-descriptor", "invalid field specifier", f);
            specs.Add((fname.Name, kind == s_mutableSym));
        }

        lock (s_gate)
        {
            // 非生成性：同名同 uid 返回已登记的同一 RTD（fasl 重建常量时依赖这一点）
            if (id != null && s_nongenerative.TryGetValue(n + id, out var existing))
                return existing;

            var rtd = new RecordTypeDescriptor(n, prtd, IsTrue(uid) ? uid : RT.False, @sealed, opaque,
              generative: id == null || uid is string, specs);
            if (id != null) s_nongenerative[n + id] = rtd;
            if (rtd.IsCondition) Symbol.Intern(n + "-rtd").Value = rtd;   // 条件类型：与旧系统一样登记 <名字>-rtd
            return rtd;
        }
    }

    /// <summary>带字段类型的版本（旧系统用于生成强类型字段）。统一表示下忽略类型。</summary>
    [Primitive("make-record-type-descriptor")]
    public static object MakeRecordTypeDescriptorTyped(object name, object parent, object uid, object issealed, object isopaque, object fields, object fieldtypes) =>
      MakeRecordTypeDescriptor(name, parent, uid, issealed, isopaque, fields);

    /// <summary>按 uid 取已登记的非生成性 RTD（fasl 常量重建使用，iron-020）。</summary>
    public static RecordTypeDescriptor LookupNongenerative(string name, string uid)
    {
        lock (s_gate) return s_nongenerative.TryGetValue(name + uid, out var r) ? r : null;
    }

    [Primitive("make-record-constructor-descriptor")]
    public static object MakeRecordConstructorDescriptor(object rtd, object parent_constructor_descriptor, object protocol)
    {
        var t = RequiresNotNull<RecordTypeDescriptor>(rtd);
        var rcd = new RecordConstructorDescriptor
        {
            type = t,
            protocol = protocol as Procedure,
            parent = parent_constructor_descriptor as RecordConstructorDescriptor,
        };
        if (t.IsCondition) Symbol.Intern(t.Name + "-rcd").Value = rcd;     // 与旧系统一样登记 <名字>-rcd
        return rcd;
    }

    [Primitive("record-predicate")]
    public static object RecordPredicate(object rtd) => RequiresNotNull<RecordTypeDescriptor>(rtd).Predicate;

    [Primitive("record-accessor")]
    public static object RecordAccessor(object rtd, object k)
    {
        var t = RequiresNotNull<RecordTypeDescriptor>(rtd);
        int i = RequiresNotNull<int>(k);
        if (i < 0 || i >= t.Fields.Length) return AssertionViolation("record-accessor", "invalid field index", rtd, k);
        return t.Fields[i].Accessor;
    }

    /// <summary>不可变字段返回 #f（与旧系统一致）。</summary>
    [Primitive("record-mutator")]
    public static object RecordMutator(object rtd, object k)
    {
        var t = RequiresNotNull<RecordTypeDescriptor>(rtd);
        int i = RequiresNotNull<int>(k);
        if (i < 0 || i >= t.Fields.Length) return AssertionViolation("record-mutator", "invalid field index", rtd, k);
        return t.Fields[i].Mutator;
    }

    // —— define-record-type 展开用（iron-034 D6）：访问器 / 修改器 / 谓词展开为调用这些原语的 lambda，
    // 成为已知过程，调用点直接 call 静态方法（原来是 record-accessor 返回的过程对象，经 callvirt Invoke1）。
    // 检查与报错与 FieldAccessor / FieldMutator / RecordPredicate 完全相同。k 是本类型内的字段下标（同 record-accessor）。

    [Primitive("$record-field-ref", Flags = PrimitiveFlags.Unsafe)]
    public static object RecordFieldRef(object rtd, object k, object obj)
    {
        var t = (RecordTypeDescriptor)rtd;
        var fd = t.Fields[(int)k];
        return t.IsInstance(obj) ? ((Record)obj).Fields[fd.Index] : AssertionViolation(t.Name + "-" + fd.Name, "not a record of type " + t.Name, obj);
    }

    [Primitive("$record-field-set!", Flags = PrimitiveFlags.Unsafe)]
    public static object RecordFieldSet(object rtd, object k, object obj, object value)
    {
        var t = (RecordTypeDescriptor)rtd;
        var fd = t.Fields[(int)k];
        if (!t.IsInstance(obj)) return AssertionViolation(t.Name + "-" + fd.Name + "-set!", "not a record of type " + t.Name, obj);
        ((Record)obj).Fields[fd.Index] = value;
        return Cleat.Scheme.Unspecified.Instance;
    }

    [Primitive("$record-instance?", Flags = PrimitiveFlags.Unsafe)]
    public static object RecordInstance(object rtd, object obj) => GetBool(((RecordTypeDescriptor)rtd).IsInstance(obj));

    [Primitive("record?")]
    public static object IsRecord(object obj) => GetBool(obj is Record r && r.Rtd != null && !r.Rtd._opaque);

    [Primitive("record-rtd")]
    public static object RecordRtd(object obj) =>
      obj is Record r && r.Rtd != null && !r.Rtd._opaque ? r.Rtd : AssertionViolation("record-rtd", "not a record", obj);

    /// <summary>(record-constructor rcd)</summary>
    [Primitive("record-constructor")]
    public static object RecordConstructor(object cd)
    {
        var rcd = RequiresNotNull<RecordConstructorDescriptor>(cd);
        for (var r = rcd; r != null; r = r.parent)
            if (r.protocol != null) return new ProtocolConstructor(rcd);
        return new DefaultConstructor(rcd.type);
    }

    /// <summary>没有 protocol：参数依次为全部字段（父类型字段在前）。</summary>
    private sealed class DefaultConstructor : VariadicProcedure
    {
        private readonly RecordTypeDescriptor _rtd;
        public DefaultConstructor(RecordTypeDescriptor rtd) { _rtd = rtd; }
        public override string Name => "make-" + _rtd.Name;
        public override object InvokeN(object[] args)
        {
            if (args.Length != _rtd.TotalFieldCount)
                return Errors.AssertionViolation(_rtd.Name,
                  $"Incorrect number of arguments, expected {_rtd.TotalFieldCount} got {args.Length}", args);
            return _rtd.Instantiate((object[])args.Clone());
        }
    }

    /// <summary>
    /// 有 protocol：先创建空实例，再沿构造描述符链调用各层 protocol。
    /// 每层的 "new" 过程：无父层时直接填写本层字段；有父层时接受父层参数、调用父层链，返回填写本层字段的过程。
    /// protocol 为 #f 的层用 R6RS 默认 protocol（参数中父层字段在前）。
    /// </summary>
    private sealed class ProtocolConstructor : VariadicProcedure
    {
        private readonly RecordConstructorDescriptor _rcd;
        public ProtocolConstructor(RecordConstructorDescriptor rcd) { _rcd = rcd; }
        public override string Name => "make-" + _rcd.type.Name;

        public override object InvokeN(object[] args)
        {
            var instance = _rcd.type.Instantiate(new object[_rcd.type.TotalFieldCount]);
            Chain(_rcd, instance).InvokeN(args);
            return instance;
        }

        private static Procedure Chain(RecordConstructorDescriptor level, Record instance)
        {
            var fill = new FieldFiller(level.type, instance);
            Procedure newProc = level.parent == null
              ? fill
              : new ParentThenFill(Chain(level.parent, instance), fill, level.parent.type.TotalFieldCount);
            if (level.protocol != null)
                return (Procedure)level.protocol.Invoke1(newProc);
            // 默认 protocol
            return level.parent == null ? fill : new DefaultProtocol(newProc, level.parent.type.TotalFieldCount);
        }
    }

    /// <summary>填写某一层自己的字段。</summary>
    private sealed class FieldFiller : VariadicProcedure
    {
        private readonly RecordTypeDescriptor _rtd; private readonly Record _instance;
        public FieldFiller(RecordTypeDescriptor rtd, Record instance) { _rtd = rtd; _instance = instance; }
        public override string Name => _rtd.Name;
        public override object InvokeN(object[] args)
        {
            if (args.Length != _rtd.Fields.Length)
                return Errors.AssertionViolation(_rtd.Name,
                  $"Incorrect number of arguments, expected {_rtd.Fields.Length} got {args.Length}", args);
            Array.Copy(args, 0, _instance.Fields, _rtd.FieldOffset, args.Length);
            return _instance;
        }
    }

    /// <summary>有父层时的 "new"：(n parent-arg ...) 调用父层链，返回填写本层字段的过程。</summary>
    private sealed class ParentThenFill : VariadicProcedure
    {
        private readonly Procedure _parentChain, _fill; private readonly int _parentCount;
        public ParentThenFill(Procedure parentChain, Procedure fill, int parentCount) { _parentChain = parentChain; _fill = fill; _parentCount = parentCount; }
        public override object InvokeN(object[] args)
        {
            _parentChain.InvokeN(args);
            return _fill;
        }
    }

    /// <summary>R6RS 默认 protocol（有父层时）：参数前 parentCount 个给父层，其余给本层。</summary>
    private sealed class DefaultProtocol : VariadicProcedure
    {
        private readonly Procedure _n; private readonly int _parentCount;
        public DefaultProtocol(Procedure n, int parentCount) { _n = n; _parentCount = parentCount; }
        public override object InvokeN(object[] args)
        {
            if (args.Length < _parentCount)
                return Errors.AssertionViolation("record-constructor", "too few arguments", args);
            var p = (Procedure)_n.InvokeN(args[.._parentCount]);
            return p.InvokeN(args[_parentCount..]);
        }
    }
}
