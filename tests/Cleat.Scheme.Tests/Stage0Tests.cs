using System;
using System.IO;
using Cleat.Scheme;
using Cleat.Stage0;
using NUnit.Framework;

namespace Cleat.Scheme.Tests;

/// <summary>stage0 解释器（iron-016）：手写 Lcore 小程序。</summary>
public class Stage0Tests
{
    [OneTimeSetUp]
    public void Init()
    {
        Interpreter.Initialize();
        Run("(set! s0:list (lambda x x))");   // list 在 Scheme 中定义（种子），这里自备
    }

    /// <summary>测试用的简单打印（Scheme 打印器在种子中）。</summary>
    private static string Show(object x) => x switch
    {
        null => "()",
        Cons c => "(" + ShowTail(c) + ")",
        Symbol s => s.Name,
        _ => x.ToString(),
    };

    private static string ShowTail(Cons c) =>
      Show(c.car) + (c.cdr == null ? "" : c.cdr is Cons d ? " " + ShowTail(d) : " . " + Show(c.cdr));

    private static object Run(string src)
    {
        var r = new SchemeReader(new StringReader(src), "test", false);
        object result = null;
        foreach (var form in r.ReadAll()) result = Interpreter.Eval(form);
        return result;
    }

    [Test]
    public void ClosuresAndSet()
    {
        // 计数器：被捕获并被 set! 的变量
        Assert.That(Run(@"
        (set! s0:make-counter
          (case-lambda ((n) (case-lambda (() (begin (set! n ($fx+ n '1)) n))))))
        ((case-lambda ((c) (begin (c) (c) (c)))) (s0:make-counter '10))"), Is.EqualTo(13));
    }

    [Test]
    public void CaseLambdaAndRestArguments()
    {
        Assert.That(Show(Run(@"
        (set! s0:f (case-lambda ((a) (quote one)) ((a b) (quote two)) ((a b . r) r)))
        (s0:list (s0:f '1) (s0:f '1 '2) (s0:f '1 '2 '3 '4))")), Is.EqualTo("(one two (3 4))"));
        Assert.That(Show(Run(@"((lambda x x) '1 '2)")), Is.EqualTo("(1 2)"));
    }

    [Test]
    public void TailRecursionMillionDeep()
    {
        // 自尾调用、相互尾调用、经 apply 的尾调用都不增长栈
        Assert.That(Run(@"
        (letrec ((loop (case-lambda ((n acc) (if (eq? n '0) acc (loop ($fx- n '1) ($fx+ acc '1)))))))
          (loop '1000000 '0))"), Is.EqualTo(1000000));
        Assert.That(Run(@"
        (letrec* ((even? (case-lambda ((n) (if (eq? n '0) '#t (odd? ($fx- n '1))))))
                  (odd? (case-lambda ((n) (if (eq? n '0) '#f (even? ($fx- n '1)))))))
          (even? '1000001))"), Is.EqualTo(false));
        Assert.That(Show(Run(@"
        (letrec ((loop (case-lambda ((n) (if (eq? n '0) 'done (apply loop (s0:list ($fx- n '1))))))))
          (loop '1000000))")), Is.EqualTo("done"));
    }

    [Test]
    public void MultipleValues()
    {
        Assert.That(Show(Run(@"
        (call-with-values
          (case-lambda (() (clr-new-internal 'Cleat.Scheme.MultipleValues (vector '1 '2 '3))))
          (case-lambda ((a b c) (s0:list c b a))))")), Is.EqualTo("(3 2 1)"));
    }

    [Test]
    public void EscapeContinuationAndTryFinally()
    {
        Assert.That(Show(Run(@"
        (set! s0:log '())
        (s0:list
          (call-with-return
            (case-lambda
              ((k) ($try/finally
                     (begin (k 'escaped) 'not-reached)
                     (set! s0:log (cons 'finally s0:log))))))
          s0:log)")), Is.EqualTo("(escaped (finally))"));
        Assert.That(Run(@"($and? '1 '#f)"), Is.EqualTo(false));
        Assert.That(Run(@"($or? '#f '2)"), Is.EqualTo(true));
    }

    [Test]
    public void ThrowAndCatchClrException()
    {
        var e = Assert.Throws<InvalidOperationException>(() => Run(@"($throw (clr-new-internal 'InvalidOperationException '""boom""))"));
        Assert.That(e.Message, Is.EqualTo("boom"));
    }

    [Test]
    public void InteropForms()
    {
        Assert.That(Run(@"(clr-call-internal 'Math 'Max '() '3 '7)"), Is.EqualTo(7));
        Assert.That(Run(@"(clr-call-internal 'String 'ToUpper '""abc"")"), Is.EqualTo("ABC"));
        Assert.That(Run(@"(clr-call-internal '#f 'get_Length '""abcd"")"), Is.EqualTo(4));
        Assert.That(Run(@"(clr-call-internal 'String '(Concat String String) '() '""a"" '""b"")"), Is.EqualTo("ab"));
        Assert.That(Run(@"(clr-field-get-internal 'Int32 'MaxValue '())"), Is.EqualTo(int.MaxValue));
        // params 数组参数整体传入向量（boot/base.sls 的 string-append）
        Assert.That(Run(@"(clr-call-internal 'String '(Concat String[]) '() (vector '""a"" '""b""))"), Is.EqualTo("ab"));
        Assert.That(Run(@"(clr-is-internal 'String '""x"")"), Is.EqualTo(true));
        // 字符与整数按数值转换（boot/base.sls 的 char->integer）
        Assert.That(Run(@"(clr-cast-internal 'Int32 (clr-cast-internal 'Char '#\a))"), Is.EqualTo(97));
        Assert.That(Run(@"(clr-cast-internal 'Char '97)"), Is.EqualTo('a'));
        Assert.That(Run(@"(clr-cast-internal 'Byte '-127)"), Is.EqualTo(129));   // 不检查溢出，Byte 转回 fixnum
        Assert.That(Run(@"(clr-call-internal 'String 'Normalize '""e\x301;"" 'formc)"), Is.EqualTo("é"));
        Assert.That(Run(@"(clr-new-array-internal 'Int32 '3)"), Is.EqualTo(new int[3]));
        // clr-using 在其 letrec / lambda 范围内生效
        Assert.That(Run(@"
        (letrec* ((u (clr-using-internal 'System.Text)))
          (clr-call-internal 'StringBuilder 'ToString (clr-new-internal 'StringBuilder '""sb"")))"), Is.EqualTo("sb"));
    }

    [Test]
    public void LibraryLetrecWritesLocations()
    {
        Assert.That(Run(@"
        (library-letrec* (s0 test)
          ((sq s0:sq-loc (case-lambda ((x) ($fx* x x)))))
          (sq '5))"), Is.EqualTo(25));
        Assert.That(Run(@"(s0:sq-loc '6)"), Is.EqualTo(36));
        Assert.That(((Procedure)Symbol.Intern("s0:sq-loc").Value).Name, Is.EqualTo("sq"));
    }

    [Test]
    public void CompileCoreReturnsThunk()
    {
        Assert.That(Run(@"((compile-core (quote ($fx+ '1 '2))))"), Is.EqualTo(3));
    }

    [Test]
    public void ErrorsWithoutHooksFallBack()
    {
        Assert.Throws<SchemeRuntimeError>(() => Run(@"(s0:undefined-variable)"));
        Assert.Throws<SchemeRuntimeError>(() => Run(@"('1 '2)"));
        // letrec 限制：初始化前读取
        Assert.Throws<SchemeRuntimeError>(() => Run(@"(letrec ((x y) (y '1)) x)"));
    }
}
