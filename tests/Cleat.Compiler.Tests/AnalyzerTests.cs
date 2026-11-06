using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Cleat.Compiler.Analyzer;
using Cleat.Compiler.Ir;
using Cleat.Core.Expand;
using clojure.lang;
using NUnit.Framework;

namespace Cleat.Compiler.Tests;

// cleat-172：C# 分析器的单元用例。不需要 boot 镜像：在一个不 refer clojure.core 的空命名空间里分析只用特殊形式的形式
// （与 Clojure 分析器的逐形式一致性由 tools/cs-diff.clj 在大语料上检查，G1）。
public class AnalyzerTests
{
    private static readonly Namespace s_ns = Namespace.findOrCreate(Symbol.intern("cleat.analyzer-test"));

    private static T InNs<T>(Func<T> f)
    {
        Var.pushThreadBindings(RT.mapUniqueKeys(RT.CurrentNSVar, s_ns));
        try { return f(); }
        finally { Var.popThreadBindings(); }
    }

    private static object Read(string text) => EdnReader.readString(text, PersistentHashMap.EMPTY);

    /// <summary>局部名 g$名字$N$后缀 按首次出现的顺序重编号（与 cs-diff 的规范化相同）。</summary>
    private static string Norm(string text)
    {
        var seen = new Dictionary<string, string>();
        return Regex.Replace(text, @"g\$([^$\s()]*)\$(\d+)\$(c|inl)", m =>
        {
            if (!seen.TryGetValue(m.Value, out var r)) seen[m.Value] = r = $"g${m.Groups[1].Value}$#{seen.Count}${m.Groups[3].Value}";
            return r;
        });
    }

    private static string Analyze(object form) => InNs(() =>
    {
        var l0 = FormAnalyzer.AnalyzeTop(form);
        IrValidator.Check(l0, Langs.L0);
        return Norm(RT.printString(IrPrinter.ToData(l0)));
    });

    [TestCase("(let* [x 1] (if x x nil))",
              "(call (lambda false (clause (g$x$#0$c) false (if g$x$#0$c g$x$#0$c (quote nil)))) (quote 1))",
              TestName = "let* is one psyntax let per binding; if without else gets (quote nil)")]
    [TestCase("(let* [x 1 x 2] x)",
              "(call (lambda false (clause (g$x$#0$c) false (call (lambda false (clause (g$x$#1$c) false g$x$#1$c)) (quote 2)))) (quote 1))",
              TestName = "let* shadowing")]
    [TestCase("(loop* [i 0] (if i (recur i) i))",
              "(call (lambda false (clause (g$i$#0$c) false (call (letrec ((g$loop$#1$c (lambda \"loop\" (clause (g$i$#2$c) false (if g$i$#2$c (call g$loop$#1$c g$i$#2$c) g$i$#2$c))))) g$loop$#1$c) g$i$#0$c))) (quote 0))",
              TestName = "loop* is a named let; recur calls the label")]
    [TestCase("(do)", "(quote nil)", TestName = "empty do")]
    [TestCase("(do 1 2)", "(begin (quote 1) (quote 2))", TestName = "do with two forms is begin")]
    [TestCase("(let* [a 1] [a 2])",
              "(call (lambda false (clause (g$a$#0$c) false (clj-call \"VectorOf\" 0 g$a$#0$c (quote 2)))) (quote 1))",
              TestName = "non-constant vector literal")]
    [TestCase("[1 {:a (quote b)}]", "(quote [1 {:a b}])", TestName = "constant collection literal")]
    [TestCase("(letfn* [f (fn* f [x] x)] f)",
              "(letrec ((g$f$#0$c (letrec ((g$f$#1$c (lambda {:class \"cleat.analyzer_test$f\", :variadic false, :prims [], :async? false} (clause (g$x$#2$c) false (call (letrec ((g$recur$#3$c (lambda \"recur\" (clause (g$x$#4$c) false g$x$#4$c)))) g$recur$#3$c) g$x$#2$c))))) g$f$#1$c))) g$f$#0$c)",
              TestName = "letfn* of fn* is a letrec; self-named fn* names its class")]
    public void SpecialForms(string form, string expected) =>
        Assert.That(Analyze(Read(form)), Is.EqualTo(expected));

    [Test]
    public void FnClassNameAndVariadic()
    {
        var s = Analyze(Read("(fn* ([a] a) ([a & more] more))"));
        Assert.That(s, Does.Match(@"^\(lambda \{:class ""cleat\.analyzer_test\$fn__\d+"", :variadic 1, :prims \[\], :async\? false\} \(clause \(g\$a\$#0\$c\)"));
        // CljFnName 的 :variadic 是 Int64（与 Clojure 分析器相同；下游按 is long 认变长子句）
        var l0 = InNs(() => FormAnalyzer.AnalyzeTop(Read("(fn* [& xs] xs)")));
        Assert.That(((FnName)((Lambda)l0).Name).Variadic, Is.TypeOf<long>());
    }

    [Test]
    public void DefInternsAtAnalysisTime()
    {
        var d = (CljDef)InNs(() => FormAnalyzer.AnalyzeTop(Read("(def answer 42)")));
        var v = s_ns.FindInternedVar(Symbol.intern("answer"));
        Assert.That(v, Is.Not.Null);
        Assert.That(d.Datum, Is.SameAs(v));
        Assert.That((d.Flag0, d.Flag1), Is.EqualTo((true, false)));
        Assert.That(((Quote)d.Expr0).Datum, Is.EqualTo(42L));
        // 元数据节点：:line :column :file（没有源位置时为 0 0 与 *source-path*）
        var m = (IPersistentMap)((Quote)d.Expr1).Datum;
        Assert.That(m.valAt(Keyword.intern("line")), Is.EqualTo(0L));
    }

    [Test]
    public void HostStaticCall()
    {
        var form = RT.list(Symbol.intern("."), Symbol.intern("System.Math"), RT.list(Symbol.intern("Abs"), -1L));
        var l0 = InNs(() => FormAnalyzer.AnalyzeTop(form));
        var c = (CljCall)l0;
        Assert.That(c.Op, Is.EqualTo("CallStatic"));
        Assert.That(c.Datum, Is.EqualTo(3L));
        Assert.That(((Quote)c.Args[0]).Datum, Is.EqualTo(typeof(Math)));
        Assert.That(((Quote)c.Args[1]).Datum, Is.EqualTo("Abs"));
    }

    [TestCase("(recur 1)", "Can only recur from tail position", "recur")]
    [TestCase("(fn* [a & b c] a)", "Unexpected parameter", "fn*")]
    [TestCase("(let* [x] x)", "Bad binding form, expected matched symbol expression pairs", "let*")]
    [TestCase("(if)", "Too few arguments to if", "if")]
    [TestCase("(fn* ([a] a) ([b] b))", "Can't have 2 overloads with same arity", "fn*")]
    [TestCase("no-such-thing", "Unable to resolve symbol: no-such-thing in this context", null)]
    public void SyntaxErrors(string form, string reason, string symbol)
    {
        var ex = Assert.Throws<ExpanderException>(() => InNs(() => FormAnalyzer.AnalyzeTop(Read(form))));
        Assert.That(ex.Reason, Is.EqualTo(reason));
        Assert.That(ex.Phase, Is.EqualTo(ExpanderException.PhaseCompile));
        Assert.That(ex.Symbol, Is.EqualTo(symbol));
    }

    [Test]
    public void TransferSourceKeepsOwnMetadata()
    {
        var old = (IObj)RT.list(Symbol.intern("a"));
        old = old.withMeta(RT.map(Keyword.intern("line"), 3L, Keyword.intern("column"), 4L, Keyword.intern("tag"), Symbol.intern("T")));
        var nw = ((IObj)RT.list(Symbol.intern("b"))).withMeta(RT.map(Keyword.intern("line"), 9L));
        var m = ((IMeta)FormAnalyzer.TransferSource(nw, old)).meta();
        Assert.That(m.valAt(Keyword.intern("line")), Is.EqualTo(9L));
        Assert.That(m.valAt(Keyword.intern("column")), Is.EqualTo(4L));
        Assert.That(m.valAt(Keyword.intern("tag")), Is.EqualTo(Symbol.intern("T")));
    }

    [Test]
    public void AnalyzerFlagsAreSettable()
    {
        bool old = FormAnalyzer.RestrictLoopInlines;
        FormAnalyzer.RestrictLoopInlines = !old;
        Assert.That(FormAnalyzer.RestrictLoopInlines, Is.EqualTo(!old));
        FormAnalyzer.RestrictLoopInlines = old;
    }
}
