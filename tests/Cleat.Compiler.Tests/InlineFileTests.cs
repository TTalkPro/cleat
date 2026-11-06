using System;
using System.Collections.Generic;
using System.IO;
using Cleat.Compiler.Ir;
using clojure.lang;
using NUnit.Framework;
using Fasl = Cleat.Core.Runtime.Fasl;

namespace Cleat.Compiler.Tests;

// cleat-171 验收：src/boot/cleat.boot.inl 里 Clojure 编译器写的全部 L0 子句，读成 C# 节点再打印，与原数据完全相同（含源位置 metadata）。
// 这保证 C# 编译器读写 .inl 与 Clojure 编译器兼容（designs/30 §2 D6）。镜像还没生成时跳过。
public class InlineFileTests
{
    private static readonly Keyword L0Tag = Keyword.intern("cleat", "l0");

    internal static string FindBootFile(string name)
    {
        for (var d = new DirectoryInfo(TestContext.CurrentContext.TestDirectory); d != null; d = d.Parent)
        {
            string p = Path.Combine(d.FullName, "src", "boot", name);
            if (File.Exists(p)) return p;
        }
        return null;
    }

    /// <summary>.inl 的格式（InterfaceFile.LoadInline）：镜像 id、条目数、每条 (Var 名、子句数、每个 (参数个数、fasl 字节))。</summary>
    internal static List<(string Var, int Arity, byte[] Bytes)> ReadInlineFile(string path)
    {
        var r = new List<(string, int, byte[])>();
        using var br = new BinaryReader(File.OpenRead(path));
        br.ReadString();
        int count = br.ReadInt32();
        for (int i = 0; i < count; i++)
        {
            string v = br.ReadString();
            int k = br.ReadInt32();
            for (int j = 0; j < k; j++)
            {
                int n = br.ReadInt32();
                int len = br.ReadInt32();
                r.Add((v, n, br.ReadBytes(len)));
            }
        }
        return r;
    }

    [Test]
    public void BootInlineClausesRoundTripExactly()
    {
        string path = FindBootFile("cleat.boot.inl");
        string dll = FindBootFile("cleat.boot.dll");
        if (path is null || dll is null) Assert.Ignore("src/boot/cleat.boot.inl / .dll not built yet");
        // 子句里 quote 的类型（deftype 等）定义在镜像里：只载入程序集让类型可解析，不执行镜像
        System.Reflection.Assembly.LoadFrom(dll);
        var entries = ReadInlineFile(path);
        Assert.That(entries, Is.Not.Empty);
        int checkedClauses = 0, withPos = 0;
        foreach (var (v, n, bytes) in entries)
        {
            var data = Fasl.Read(bytes) as IPersistentVector;
            Assert.That(data != null && data.count() == 2 && Equals(data.nth(0), L0Tag), $"{v}/{n}: expected [:cleat/l0 clause]");
            object original = data.nth(1);
            var clause = IrReader.ReadClause(original);
            IrValidator.Check(clause, Langs.L0);
            object printed = IrPrinter.ToData(clause);
            string diff = Diff(original, printed, "");
            Assert.That(diff, Is.Null, $"{v}/{n}");
            Assert.That(RT.printString(printed), Is.EqualTo(RT.printString(original)), $"{v}/{n} text");
            withPos += CountPositions(clause);
            checkedClauses++;
        }
        TestContext.Out.WriteLine($"{checkedClauses} clauses, {withPos} nodes with source positions");
        Assert.That(withPos, Is.GreaterThan(0), "positions survive");
    }

    private static int CountPositions(Node n)
    {
        var c = new PosCounter();
        c.Walk(n);
        return c.Count;
    }

    private sealed class PosCounter : Walker
    {
        public int Count;
        public override void Walk(Node n)
        {
            if (n != null && n.Pos.IsKnown) Count++;
            base.Walk(n);
        }
    }

    /// <summary>结构比较（列表逐元素，且列表的 metadata 相等）；相同返回 null，否则返回第一处差异的路径与两边的值。</summary>
    internal static string Diff(object a, object b, string path)
    {
        if (ReferenceEquals(a, b)) return null;
        bool la = a is ISeq or IPersistentList, lb = b is ISeq or IPersistentList;
        if (la || lb)
        {
            if (!(la && lb)) return $"{path}: {Show(a)} vs {Show(b)}";
            var ma = (a as IMeta)?.meta();
            var mb = (b as IMeta)?.meta();
            if (!(ma is null && mb is null) && (ma is null || mb is null || !Util.equiv(ma, mb)))
                return $"{path} meta: {Show(ma)} vs {Show(mb)}";
            ISeq sa = RT.seq(a), sb = RT.seq(b);
            for (int i = 0; ; i++, sa = sa.next(), sb = sb.next())
            {
                if (sa is null || sb is null) return sa is null && sb is null ? null : $"{path}: length differs";
                string d = Diff(sa.first(), sb.first(), $"{path}/{i}");
                if (d != null) return d;
            }
        }
        return Util.equiv(a, b) ? null : $"{path}: {Show(a)} vs {Show(b)}";
    }

    private static string Show(object x)
    {
        try { return RT.printString(x); }
        catch (Exception) { return x?.ToString() ?? "nil"; }
    }
}
