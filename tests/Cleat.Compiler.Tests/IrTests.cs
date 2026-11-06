using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Cleat.Compiler.Ir;
using Cleat.Compiler.Passes;
using clojure.lang;
using NUnit.Framework;

namespace Cleat.Compiler.Tests;

// cleat-171：IR 节点、S 表达式打印 / 读入、校验器、重写基类、管线（designs/30 §2 D3）
public class IrTests
{
    // 每种 L0 节点至少出现一次
    private const string L0Sample = @"
(letrec ((f (lambda ""f"" (clause (a b) rest (begin (set! a (quote 1)) (call f a b))))))
  (let ((x (quote ""s"")) (y (uninitialized)))
    (if (check-initialized y)
        (try-catch (try-finally (throw x) (quote nil)) (System.Exception (lambda false (clause (e) false e))))
        (label loop (i) (jump loop i) (begin
          (clj-var-ref v false)
          (clj-var-set! v x)
          (clj-def d true false x (quote nil))
          (clj-invoke f x)
          (clj-keyword-invoke :k x y)
          (clj-call ""CallStatic"" 3 (quote clojure.lang.RT) x)
          (clj-static-call ""ns$f"" 1 x)
          (clj-case [:int] x (quote 0) (quote 1) (quote 2))
          (clj-deftype ns.T (a b) (lambda ""m"" (clause (this) false a))))))))";

    private const string L2Sample = "(let ((b (box (quote 1)))) (begin (set-box! b (quote 2)) (unbox b)))";
    private const string L3Sample = "(letrec ((f (lambda false (y z) (clause (x) false (call x y z))))) f)";

    private static string Text(object data) => RT.printString(data);

    [Test]
    public void EveryNodeKindRoundTripsThroughSExpressions()
    {
        foreach (var (text, lang) in new[] { (L0Sample, Langs.L0), (L2Sample, Langs.L2), (L3Sample, Langs.L3) })
        {
            var data = EdnReader.readString(text, PersistentHashMap.EMPTY);
            var node = IrReader.ReadExpr(data, lang);
            Assert.That(Text(IrPrinter.ToData(node)), Is.EqualTo(Text(data)), $"{lang} round trip");
            IrValidator.Check(node, lang);
        }
        var seen = new HashSet<NodeKind>();
        foreach (var (text, lang) in new[] { (L0Sample, Langs.L0), (L2Sample, Langs.L2), (L3Sample, Langs.L3) })
            new KindCollector(seen).Walk(IrReader.ReadText(text, lang));
        Assert.That(Enum.GetValues<NodeKind>().Except(seen), Is.Empty, "samples cover every node kind");
    }

    private sealed class KindCollector : Walker
    {
        private readonly HashSet<NodeKind> _seen;
        public KindCollector(HashSet<NodeKind> seen) { _seen = seen; }
        public override void Walk(Node n)
        {
            if (n != null) _seen.Add(n.Kind);
            base.Walk(n);
        }
    }

    [Test]
    public void SourcePositionsTravelAsListMetadata()
    {
        var inner = ((IObj)PersistentList.create(new object[] { Symbol.intern("quote"), 1L }))
            .withMeta(RT.map(Keyword.intern("line"), 7, Keyword.intern("column"), null, Keyword.intern("file"), "a.clj"));
        var form = PersistentList.create(new object[] { Symbol.intern("throw"), inner });
        var node = (Throw)IrReader.ReadExpr(form);
        Assert.That(node.Pos.IsKnown, Is.False);
        Assert.That(node.Value.Pos, Is.EqualTo(new SourcePos(7, 0, "a.clj")));
        var back = (ISeq)IrPrinter.ToData(node);
        var meta = ((IMeta)back.next().first()).meta();
        // 与 nanopass unparse 相同：column 至少为 1，file 只留文件名
        Assert.That(Text(meta), Is.EqualTo("{:line 7, :column 1, :file \"a.clj\"}"));
        node.Value.Pos = new SourcePos(3, 5, "/tmp/x/b.clj");
        meta = ((IMeta)((ISeq)IrPrinter.ToData(node)).next().first()).meta();
        Assert.That(Text(meta), Is.EqualTo("{:line 3, :column 5, :file \"b.clj\"}"));
    }

    [Test]
    public void FnNamesPrintAsMapsAndReadBack()
    {
        var lam = new Lambda(new FnName("user$f", 1L, null, true), default, ImmutableArrayOf<Clause>());
        var back = (Lambda)IrReader.ReadExpr(IrPrinter.ToData(lam));
        var nm = (FnName)back.Name;
        Assert.That((nm.Class, nm.Variadic, nm.Async), Is.EqualTo(((object)"user$f", (object)1L, true)));
    }

    [Test]
    public void ReaderRejectsFormsOutsideTheLanguage()
    {
        Assert.Throws<IrFormatException>(() => IrReader.ReadText("(box (quote 1))", Langs.L1), "box is L2+");
        Assert.Throws<IrFormatException>(() => IrReader.ReadText("(if x y)"), "arity");
        Assert.Throws<IrFormatException>(() => IrReader.ReadText("(begin)"), "begin needs one expression");
        Assert.Throws<IrFormatException>(() => IrReader.ReadText("(frob x)"), "unknown keyword");
        Assert.Throws<IrFormatException>(() => IrReader.ReadText("(clj-var-ref v 1)"), "flag must be boolean");
        Assert.Throws<IrFormatException>(() => IrReader.ReadText("(lambda false (clause (x) false x))", Langs.L3), "L3 lambda has a free list");
    }

    [Test]
    public void ValidatorChecksLanguageInvariants()
    {
        var l0Letrec = IrReader.ReadText("(letrec ((x (quote 1))) x)");
        IrValidator.Check(l0Letrec, Langs.L0);
        var e = Assert.Throws<IrValidationException>(() => IrValidator.Check(l0Letrec, Langs.L1));
        Assert.That(e.Message, Does.Contain("letrec binds only lambda"));

        var l2 = IrReader.ReadText(L2Sample, Langs.L2);
        IrValidator.Check(l2, Langs.L2);
        Assert.That(IrValidator.Errors(l2, Langs.L1), Has.Some.Contains("Box is not a node of L1").And.Some.Contains("SetBox is not a node of L1"));

        var l3 = IrReader.ReadText(L3Sample, Langs.L3);
        IrValidator.Check(l3, Langs.L3);
        Assert.That(IrValidator.Errors(l3, Langs.L2).Single(), Does.Contain("must not have a free-variable list"));
        var l1Lambda = IrReader.ReadText("(lambda false (clause () false (quote 1)))", Langs.L1);
        Assert.That(IrValidator.Errors(l1Lambda, Langs.L3).Single(), Does.Contain("needs a free-variable list"));

        var broken = new If(new Quote(true), null, new Quote(false));
        Assert.That(IrValidator.Errors(broken, Langs.L0).Single(), Does.Contain("null child"));
        Assert.Throws<ArgumentException>(() => IrValidator.Check(broken, Langs.L0 | Langs.L1));
    }

    private sealed class IdentityRewriter : Rewriter
    {
    }

    private sealed class BumpQuotes : Rewriter
    {
        public override Node VisitQuote(Quote n) => n.Datum is long v ? n.Update(v + 1) : n;
    }

    [Test]
    public void RewritersShareUnchangedStructureAndKeepPositions()
    {
        var ir = IrReader.ReadText("(if (quote true) (call f (quote 1)) (call g x))");
        Assert.That(new IdentityRewriter().Rewrite(ir), Is.SameAs(ir));

        var call = (Call)((If)ir).Then;
        call.Pos = new SourcePos(10, 2, "p.clj");
        var r = (If)new BumpQuotes().Rewrite(ir);
        Assert.That(r, Is.Not.SameAs(ir));
        Assert.That(r.Test, Is.SameAs(((If)ir).Test), "quote true unchanged");
        Assert.That(r.Else, Is.SameAs(((If)ir).Else), "untouched branch shared");
        Assert.That(r.Then, Is.Not.SameAs(call));
        Assert.That(r.Then.Pos, Is.EqualTo(call.Pos), "rebuilt node keeps the source position");
        Assert.That(Text(IrPrinter.ToData(r)), Is.EqualTo("(if (quote true) (call f (quote 2)) (call g x))"));
    }

    private sealed class Noop : IPass
    {
        public string Name => "Noop";
        public Langs Input => Langs.L0;
        public Langs Output => Langs.L0;
        public Node Run(Node ir) => ir;
    }

    private sealed class Breaks : IPass
    {
        public string Name => "Breaks";
        public Langs Input => Langs.L0;
        public Langs Output => Langs.L1;
        public Node Run(Node ir) => ir;   // 声称产出 L1，实际没处理 letrec
    }

    [Test]
    public void PipelineRunsPassesWithChecksAndDumps()
    {
        var ir = IrReader.ReadText("(letrec ((x (quote 1))) x)");
        var w = new StringWriter();
        var opts = new PipelineOptions { Check = true, Dump = new HashSet<string>(new[] { "noop" }, StringComparer.OrdinalIgnoreCase), DumpTo = w };
        Assert.That(new Pipeline(new Noop()).Run(ir, opts), Is.SameAs(ir));
        Assert.That(w.ToString(), Is.EqualTo(";; Noop (L0)\n(letrec ((x (quote 1))) x)\n"));

        var e = Assert.Throws<IrValidationException>(() => new Pipeline(new Noop(), new Breaks()).Run(ir, opts));
        Assert.That(e.Message, Does.Contain("after Breaks"));
        Assert.That(new Pipeline(new Noop(), new Breaks()).Run(ir, PipelineOptions.None), Is.SameAs(ir), "no check: runs through");
        Assert.Throws<ArgumentException>(() => new Pipeline(new Breaks(), new Noop()), "L1 output into an L0 pass");
    }

    [Test]
    public void PipelineOptionsParseTheEnvironment()
    {
        var env = new Dictionary<string, string> { ["CLEAT_IR_CHECK"] = "1", ["CLEAT_DUMP_IR"] = "Noop, Lower\tx" };
        var o = PipelineOptions.Read(k => env.TryGetValue(k, out var v) ? v : null);
        Assert.That(o.Check, Is.True);
        Assert.That(o.Dumps("noop") && o.Dumps("LOWER") && o.Dumps("x") && !o.Dumps("No"), Is.True);
        Assert.That(o.DumpTo, Is.Null);
        var none = PipelineOptions.Read(k => k == "CLEAT_IR_CHECK" ? "0" : null);
        Assert.That(none.Check || none.Dumps("Noop"), Is.False);
        Assert.That(PipelineOptions.Read(k => k == "CLEAT_DUMP_IR" ? "all" : null).Dumps("anything"), Is.True);
    }

    private static System.Collections.Immutable.ImmutableArray<T> ImmutableArrayOf<T>(params T[] xs) => System.Collections.Immutable.ImmutableArray.Create(xs);
}
