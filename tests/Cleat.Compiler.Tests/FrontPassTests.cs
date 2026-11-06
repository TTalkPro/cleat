using Cleat.Compiler.Ir;
using Cleat.Compiler.Passes;
using Cleat.Compiler.Passes.Front;
using clojure.lang;
using NUnit.Framework;

namespace Cleat.Compiler.Tests;

// cleat-173：前中端 pass 的单元用例（与 Clojure 编译器的逐阶段一致性由 tools/cs-diff.clj 在大语料上检查）
public class FrontPassTests
{
    private static string Run(IPass pass, string input, Langs inLang, Langs outLang)
    {
        var ir = IrReader.ReadText(input, inLang);
        var r = pass.Run(ir);
        IrValidator.Check(r, outLang);
        return RT.printString(IrPrinter.ToData(r));
    }

    private static FrontState Analyzed(string input, Langs lang)
    {
        var st = new FrontState();
        st.Analysis = FrontAnalysis.Of(IrReader.ReadText(input, lang));
        return st;
    }

    private static string Norm(string text) => RT.printString(EdnReader.readString(text, PersistentHashMap.EMPTY));

    [TestCase("(letrec ((f (lambda \"f\" (clause (i) false (if i (call f i) (quote 0)))))) (call f (quote 1)))",
              "(label f (i) (jump f (quote 1)) (if i (jump f i) (quote 0)))", 1, TestName = "LowerLoops: tail-only letrec becomes a label")]
    [TestCase("(call (letrec ((f (lambda false (clause (i) false (call f i))))) f) (quote 1))",
              "(label f (i) (jump f (quote 1)) (jump f i))", 1, TestName = "LowerLoops: named letrec call")]
    [TestCase("(letrec ((f (lambda false (clause (i) false (call g (call f i)))))) (call f x))",
              "(letrec ((f (lambda false (clause (i) false (call g (call f i)))))) (call f x))", 0, TestName = "LowerLoops: non-tail call keeps letrec")]
    [TestCase("(call (lambda false (clause (a) false a)) (quote 1))",
              "(let ((a (quote 1))) a)", 0, TestName = "LowerLoops: immediate lambda is a let")]
    [TestCase("(let ((k (lambda false (clause (v) false v)))) (if x (call k (quote 1)) (call k (quote 2))))",
              "(label k (v) (if x (jump k (quote 1)) (jump k (quote 2))) v)", 1, TestName = "LowerLoops: tail-called let lambda becomes a label")]
    [TestCase("(let ((k (lambda false (clause (v) false v)))) (call g (call k (quote 1))))",
              "(let ((k (lambda false (clause (v) false v)))) (call g (call k (quote 1))))", 0, TestName = "LowerLoops: non-tail let lambda stays")]
    public void LowerLoopsCases(string input, string expected, int loops)
    {
        var st = new FrontState();
        Assert.That(Run(new LowerLoops(st), input, Langs.L0, Langs.L0), Is.EqualTo(Norm(expected)));
        Assert.That(st.LoopsMade, Is.EqualTo(loops));
    }

    [Test]
    public void SimplifyLetrecGroupsLambdasAndHoistsConstants()
    {
        const string input = "(letrec ((n (quote 1)) (f (lambda false (clause () false n)))) (call f))";
        Assert.That(Run(new SimplifyLetrec(Analyzed(input, Langs.L0)), input, Langs.L0, Langs.L1),
            Is.EqualTo(Norm("(let ((n (quote 1))) (letrec ((f (lambda false (clause () false n)))) (call f)))")));
    }

    [Test]
    public void SimplifyLetrecIntroducesForwardVariables()
    {
        // a 的初值不是 lambda，却引用了后绑定的 b：b 是前向变量
        // （letrec-refs 按节点身份做键：分析与运行必须是同一棵 IR）
        var ir = IrReader.ReadText("(letrec ((a (call g b)) (b (lambda false (clause () false a)))) a)");
        var st = new FrontState { Analysis = FrontAnalysis.Of(ir) };
        var r = new SimplifyLetrec(st).Run(ir);
        IrValidator.Check(r, Langs.L1);
        Assert.That(RT.printString(IrPrinter.ToData(r)),
            Is.EqualTo(Norm("(let ((b (uninitialized))) (let ((a (call g b))) (begin (set! b (lambda false (clause () false a))) a)))")));
        Assert.That(st.Analysis.Forward, Is.EquivalentTo(new[] { Symbol.intern("b") }));
        Assert.That(st.Analysis.Assigned, Does.Contain(Symbol.intern("b")));
        Assert.That(Run(new CheckForward(st), "(call g b)", Langs.L1, Langs.L1), Is.EqualTo(Norm("(call g (check-initialized b))")));
    }

    [Test]
    public void SimplifyLetrecRecognizesLet()
    {
        const string input = "(call (lambda false (clause (a) false a)) (quote 1))";
        Assert.That(Run(new SimplifyLetrec(Analyzed(input, Langs.L0)), input, Langs.L0, Langs.L1), Is.EqualTo(Norm("(let ((a (quote 1))) a)")));
    }

    [TestCase("(if (quote nil) a b)", "b")]
    [TestCase("(if (quote 0) a b)", "a")]
    [TestCase("(begin (quote 1) x (quote 2) y)", "(begin x y)")]
    [TestCase("(begin (quote 1) y)", "y")]
    public void Cp0LiteFolds(string input, string expected) =>
        Assert.That(Run(new Cp0Lite(), input, Langs.L1, Langs.L1), Is.EqualTo(Norm(expected)));

    [Test]
    public void SimplifyLetsPropagatesAndDrops()
    {
        // long 常量不传播（只传 Int32 / Double / 布尔 / 字符）；未被赋值的变量传播；没被引用的有副作用初值保留
        const string input = "(let ((a (quote 1)) (b x) (c (call f))) (call g a b))";
        Assert.That(Run(new SimplifyLets(Analyzed(input, Langs.L1)), input, Langs.L1, Langs.L1),
            Is.EqualTo(Norm("(let ((a (quote 1)) (c (call f))) (call g a x))")));
        const string dead = "(let ((u (quote 2.5))) (letrec ((h (lambda false (clause () false x)))) x))";
        Assert.That(Run(new SimplifyLets(Analyzed(dead, Langs.L1)), dead, Langs.L1, Langs.L1), Is.EqualTo("x"));
    }

    [Test]
    public void ConvertAssignmentsBoxesCapturedAssignedVariables()
    {
        const string input = "(let ((a (quote 1))) (begin (set! a (quote 2)) (lambda false (clause () false a))))";
        Assert.That(Run(new ConvertAssignments(Analyzed(input, Langs.L1)), input, Langs.L1, Langs.L2),
            Is.EqualTo(Norm("(let ((a (box (quote 1)))) (begin (set-box! a (quote 2)) (lambda false (clause () false (unbox a)))))")));

        const string param = "(lambda false (clause (p) false (begin (set! p (quote 1)) (lambda false (clause () false p)))))";
        Assert.That(Run(new ConvertAssignments(Analyzed(param, Langs.L1)), param, Langs.L1, Langs.L2),
            Is.EqualTo(Norm("(lambda false (clause (p) false (begin (set! p (box p)) (begin (set-box! p (quote 1)) (lambda false (clause () false (unbox p)))))))")));

        // 只被赋值、没被捕获：不装箱
        const string local = "(let ((a (quote 1))) (begin (set! a (quote 2)) a))";
        Assert.That(Run(new ConvertAssignments(Analyzed(local, Langs.L1)), local, Langs.L1, Langs.L2), Is.EqualTo(Norm(local)));
    }

    [Test]
    public void UncoverFreeAttachesOrderedFreeLists()
    {
        Assert.That(Run(new UncoverFree(), "(lambda false (clause (x) false (call x y z y)))", Langs.L2, Langs.L3),
            Is.EqualTo(Norm("(lambda false (y z) (clause (x) false (call x y z y)))")));
        Assert.That(Run(new UncoverFree(), "(label l (i) (jump l w) (lambda false (clause () false (call i v))))", Langs.L2, Langs.L3),
            Is.EqualTo(Norm("(label l (i) (jump l w) (lambda false (i v) (clause () false (call i v))))")));
    }

    [Test]
    public void FrontEndRunsTheWholeChainWithChecks()
    {
        var ir = IrReader.ReadText("(letrec ((f (lambda \"f\" (clause (i) false (if i (call f i) (quote 0)))))) (call f (quote 1)))");
        var st = new FrontState();
        var l3 = FrontEnd.Run(ir, st, new PipelineOptions { Check = true });
        IrValidator.Check(l3, Langs.L3);
        Assert.That(st.LoopsMade, Is.EqualTo(1));
    }
}
