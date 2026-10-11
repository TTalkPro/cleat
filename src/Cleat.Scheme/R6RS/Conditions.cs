using System.Collections.Generic;
using System.Linq;

namespace Cleat.Scheme;

/// <summary>
/// 条件：RTD 祖先链中有 &amp;condition 的记录。Scheme 中 (clr-is Condition obj) 判断"是否条件"，
/// 对简单条件与复合条件都成立。
/// </summary>
public class Condition : Record
{
    public Condition(RecordTypeDescriptor rtd, object[] fields) : base(rtd, fields) { }
}

/// <summary>复合条件：由简单条件组成（嵌套的复合条件被展开）。不是 record?。</summary>
public sealed class CompoundCondition : Condition
{
    internal readonly object[] _conds;

    public CompoundCondition(object[] conds) : base(null, System.Array.Empty<object>())
    {
        var inner = new List<object>();
        foreach (var c in conds)
        {
            if (c is CompoundCondition cc) inner.AddRange(cc._conds);
            else inner.Add(c);
        }
        _conds = inner.ToArray();
    }

    public override string ToString() => "#<compound-condition " + string.Join(" ", _conds.Select(c => c.ToString())) + ">";
}

/// <summary>从旧 Conditions.cs 移植的 4 个原语。</summary>
public class Conditions : Builtins
{
    /// <summary>单个参数时原样返回（非条件返回 null，与旧系统一致）。</summary>
    [Primitive("condition")]
    public static object MakeCondition(params object[] conds) =>
      conds.Length == 1 ? conds[0] as Condition : new CompoundCondition(conds);

    [Primitive("simple-conditions")]
    public static object SimpleConditions(object cond) =>
      cond is CompoundCondition cc ? Cleat.Scheme.Cons.FromArray(cc._conds) : new Cleat.Scheme.Cons(cond);

    [Primitive("condition-predicate")]
    public static object ConditionPredicate(object rtd)
    {
        var t = RequiresNotNull<RecordTypeDescriptor>(rtd);
        if (!t.IsCondition) return AssertionViolation("condition-predicate", "not a valid condition", rtd);
        return new CondPredicate(t);
    }

    [Primitive("condition-accessor")]
    public static object ConditionAccessor(object rtd, object proc)
    {
        var t = RequiresNotNull<RecordTypeDescriptor>(rtd);
        if (!t.IsCondition) return AssertionViolation("condition-accessor", "not a valid condition", rtd);
        return new CondAccessor(t, RequiresNotNull<Procedure>(proc));
    }

    private static IEnumerable<object> Components(object cond) => cond is CompoundCondition cc ? cc._conds : new[] { cond };

    private sealed class CondPredicate : Procedure
    {
        private readonly RecordTypeDescriptor _t;
        public CondPredicate(RecordTypeDescriptor t) { _t = t; }
        public override string Name => _t.Name + "?";
        public override object Invoke1(object cond) => RT.Bool(Components(cond).Any(_t.IsInstance));
    }

    private sealed class CondAccessor : Procedure
    {
        private readonly RecordTypeDescriptor _t; private readonly Procedure _proc;
        public CondAccessor(RecordTypeDescriptor t, Procedure proc) { _t = t; _proc = proc; }
        public override string Name => _t.Name + "-accessor";
        public override object Invoke1(object cond)
        {
            foreach (var c in Components(cond))
                if (_t.IsInstance(c)) return _proc.Invoke1(c);
            return RT.False;
        }
    }
}
