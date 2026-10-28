using System;
using System.IO;
using Cleat.Core.Runtime;
using clojure.lang;
using NUnit.Framework;

namespace Cleat.Core.Tests;

// cleat-161（designs/29 §3.3）：编译器延迟加载的两块地基——
//   1. SimpleForms 认哪些形式（认多了会把该编译的形式自己算错，认少了只是多加载一次编译器）；
//   2. RuntimeHooks 的编译类钩子在槽为空时走惰性入口，Startup.EvalForm / EvalEntry 只在惰性入口登记时才走快速路径
//      （没跑过 Start 的进程——比如这里的单测——与 cleat-161 之前逐字同路，包括"未登记"的明确错误）。
public class SimpleFormsTests
{
    private static object Read(string text)
    {
        var r = new LineNumberingTextReader(new StringReader(text));
        return LispReader.read(r, false, new object(), false, RT.map(Keyword.intern("read-cond"), Keyword.intern("allow")));
    }

    private static void AssertTrivial(string text)
    {
        object form = Read(text);
        Assert.That(SimpleForms.Try(form, out object v), Is.True, "该认的没认：" + text);
        // 常量与集合字面量的值就是它自己（nil 是 null）：不求值、不复制
        if (!(form is ISeq))
            Assert.That(ReferenceEquals(v, form) || (v is null && form is null), Is.True, "值该是形式本身：" + text);
    }

    /// <summary>(quote x) → x：值是该形式自己的操作数（和它自己的元数据），不是包着它的 quote。</summary>
    private static void AssertQuote(string text, string expectedPrinted)
    {
        Assert.That(SimpleForms.Try(Read(text), out object v), Is.True, "该认的没认：" + text);
        Assert.That(v is null ? "<nil>" : RT.printString(v), Is.EqualTo(expectedPrinted), "值该是 quote 的操作数：" + text);
    }

    private static void AssertNeedsCompiler(string text)
    {
        Assert.That(SimpleForms.Try(Read(text), out _), Is.False, "不该认的认了：" + text);
    }

    [Test]
    public void RecognisesSelfEvaluatingConstants()
    {
        AssertTrivial("nil");
        AssertTrivial("true");
        AssertTrivial("false");
        AssertTrivial("42");
        AssertTrivial("3.5");
        AssertTrivial("\"s\"");
        AssertTrivial("\\a");
        AssertTrivial(":kw");
        AssertTrivial("1N");
    }

    [Test]
    public void RecognisesQuoteAndConstantCollections()
    {
        // 空表求值还是它自己（与编译器一致，不是 nil）
        Assert.That(SimpleForms.Try(Read("()"), out object e), Is.True);
        Assert.That(RT.printString(e), Is.EqualTo("()"));
        AssertQuote("'x", "x");
        AssertQuote("(quote x)", "x");
        AssertQuote("'(1 2)", "(1 2)");
        AssertQuote("(quote (1 2))", "(1 2)");
        AssertQuote("(quote ())", "()");
        AssertQuote("(quote nil)", "<nil>");
        AssertQuote("'(1 a)", "(1 a)");          // quote 的操作数含符号也照原样交出
        AssertTrivial("[1 2 3]");
        AssertTrivial("[]");
        AssertTrivial("{:a 1}");
        AssertTrivial("#{1 2}");
        AssertTrivial("[[1 :a] {:b #{\"c\"}}]");     // 递归不含符号
    }

    [Test]
    public void RejectsAnythingNeedingTheCompiler()
    {
        AssertNeedsCompiler("a");                   // 符号要解析
        AssertNeedsCompiler("*ns*");
        AssertNeedsCompiler("(+ 1 2)");             // 调用
        AssertNeedsCompiler("(def a 1)");
        AssertNeedsCompiler("(let* [x 1] x)");
        AssertNeedsCompiler("[[1 (foo)]]");          // 集合里藏着调用
        AssertNeedsCompiler("[1 a]");               // 集合里藏着符号
        AssertNeedsCompiler("{:a b}");
        // 带元数据的一律交回编译器。元数据只能挂在 IMeta 上：^:foo 5 在读取期就报错（与编译器无关），
        // 所以这里用向量 / map 这种带得上元数据的字面量。
        AssertNeedsCompiler("^{:tag long} [1]");
        AssertNeedsCompiler("^:foo {:a 1}");
        AssertNeedsCompiler("^:foo [1 2]");
        AssertNeedsCompiler("(do 1)");               // 特殊形式：交回编译器
    }

    [Test]
    public void CompilingHooksRunTheLazyEntryOnlyWhileTheSlotIsEmpty()
    {
        RuntimeHooks.Reset();
        try
        {
            int calls = 0;
            RuntimeHooks.EnsureCompiler = () => calls++;
            // 槽为空：读一次试一次（真正的实现是幂等的：已加载 / 没有编译器时立刻返回）
            Assert.That(RuntimeHooks.Eval, Is.Null);
            Assert.That(RuntimeHooks.LoadSource, Is.Null);
            Assert.That(RuntimeHooks.Compile, Is.Null);
            Assert.That(RuntimeHooks.Macroexpand1, Is.Null);
            Assert.That(calls, Is.EqualTo(4));
            // 槽登记之后不再走惰性入口
            RuntimeHooks.Set("eval", new Fn1(x => x));
            Assert.That(RuntimeHooks.Eval, Is.Not.Null);
            Assert.That(calls, Is.EqualTo(4));
            // 没有惰性入口时（没跑过 Start）读属性就是普通的读：旧行为，未登记的错误照旧
            RuntimeHooks.Reset();
            var e = Assert.Throws<InvalidOperationException>(() => RuntimeHooks.Require(RuntimeHooks.Eval, "eval"));
            Assert.That(e.Message, Does.Contain("runtime hook 'eval' is not registered"));
        }
        finally { RuntimeHooks.Reset(); }
    }

    [Test]
    public void EvalFormKeepsTheOldPathWhenTheLazyEntryIsNotArmed()
    {
        RuntimeHooks.Reset();
        try
        {
            // 没有惰性入口：即使形式很平凡也照旧经钩子（HostTests / LoaderTests 的假钩子语义不变）
            Assert.Throws<InvalidOperationException>(() => Startup.EvalForm(1L));
            Func<object, object> fake = x => RT.list(Symbol.intern("evaluated"), x);
            RuntimeHooks.Eval = fake;
            Assert.That(Startup.EvalEntry(), Is.SameAs(fake));
            Assert.That(RuntimeHooks.Eval, Is.SameAs(fake));
            Assert.That(RT.printString(Startup.EvalForm(1L)), Is.EqualTo("(evaluated 1)"));
        }
        finally { RuntimeHooks.Reset(); }
    }
}
