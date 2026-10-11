using System;
using System.IO;
using NUnit.Framework;

namespace Cleat.Host.Tests;

/// <summary>
/// REPL 行编辑器的词法部分（cleat-034）：括号平衡（IsComplete）、补全起点（CompletionStart）、
/// 所在列表的首符号（EnclosingHead）。编辑与按键交互需要终端，不在单元测试里。
/// </summary>
public class LineEditorTests
{
    [TestCase("(+ 1 2)", true)]
    [TestCase("(+ 1 [2 {:k 3}])", true)]
    [TestCase("(+ 1 2", false)]
    [TestCase("{:a [1 2", false)]
    [TestCase("\"unclosed", false)]
    [TestCase("\"closed (str\"", true)]
    [TestCase("(str \"a)\" )", true)]
    [TestCase("; ) only a comment\n(+ 1 2)", true)]
    [TestCase("\\( not an open paren", true)]
    [TestCase("", true)]
    [TestCase("   ", true)]
    public void Completeness(string text, bool complete) =>
        Assert.That(LineEditor.IsComplete(text), Is.EqualTo(complete));

    [TestCase("inc", "inc")]
    [TestCase("(inc", "inc")]
    [TestCase("(str 1 in", "in")]
    [TestCase("[1 clojure.st", "clojure.st")]
    [TestCase("{:a :so", ":so")]
    [TestCase("\"in string", "")]
    public void CompletionStartsAtWordStart(string before, string prefix) =>
        Assert.That(before.Substring(LineEditor.CompletionStart(before)), Is.EqualTo(prefix));

    [TestCase("(inc2 ", "inc2")]
    [TestCase("(map f ", "map")]
    [TestCase("x", null)]
    [TestCase("(head", null)]
    [TestCase("(str \"x\" ", "str")]   // 提示发生在输入未闭合时
    public void EnclosingListHead(string before, string head) =>
        Assert.That(LineEditor.EnclosingHead(before), Is.EqualTo(head));
}
