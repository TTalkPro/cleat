using NUnit.Framework;

namespace Cleat.Host.Tests;

/// <summary>cleat-128：eval 候选的剖面记录、单态触发与开关语义。</summary>
[Category("Host")]
public class TypeFeedbackTests
{
    [Test]
    public void RecordsMonomorphicCandidatesAndPolymorphicParameters()
    {
        var forms = new[]
        {
            "-e", "(require 'cleat.compiler.pipeline)",
            "-e", "(defn tf-id [x] (if (pos? x) x 0))",
            "-e", "(defn tf-two [x y] (+ x y))",
            "-e", "(defn tf-three [x y z] (+ x y z))",
            "-e", "(defn tf-four [x y z w] (+ x y z w))",
            "-e", "(defn tf-poly [x] (if (= x 0) x 1))",
            "-e", "(dotimes [_ 1100] (tf-id 7))",
            "-e", "(dotimes [_ 1001] (tf-two 7 8) (tf-three 7 8 9) (tf-four 7 8 9 10))",
            "-e", "(dotimes [_ 500] (tf-poly 7))",
            "-e", "(dotimes [_ 501] (tf-poly \"x\"))",
            "-e", "(def s (. Cleat.Core.Runtime.TypeFeedback Summary))",
            "-e", "(assert (re-find #\"tf_id/1 calls=1000 types=\\[Int64\\]\" s))",
            "-e", "(assert (re-find #\"tf_four/4 calls=1000 types=\\[Int64,Int64,Int64,Int64\\]\" s))",
            "-e", "(assert (re-find #\"triggered=4 profiled-calls=5000 param-types=\\[Int64:10,polymorphic:1\\]\" s))",
            "-e", "(prn :tf-done)",
        };
        var on = HostProcessTests.CleatIn(HostProcessTests.RepoDir, null, null, forms);
        Assert.That(on.ExitCode, Is.EqualTo(0), on.ToString());
        Assert.That(on.Out, Does.Contain(":tf-done"), on.ToString());
    }

    [Test]
    public void ProfilingCanProduceByteIdenticalProgramOutput()
    {
        var forms = new[]
        {
            "-e", "(require 'cleat.compiler.pipeline)",
            "-e", "(defn tf-two [x y] (+ x y))",
            "-e", "(dotimes [_ 1100] (tf-two 7 8))",
            "-e", "(prn (tf-two 20 22))",
        };
        var on = HostProcessTests.CleatIn(HostProcessTests.RepoDir, null, null, forms);
        var env = new[] { ("CLEAT_NO_OPT", "typefeedback") };
        var off = HostProcessTests.CleatIn(HostProcessTests.RepoDir, null, env, forms);
        Assert.That(on.ExitCode, Is.EqualTo(0), on.ToString());
        Assert.That(off.ExitCode, Is.EqualTo(0), off.ToString());
        Assert.That(off.Out, Is.EqualTo(on.Out), on + "\n--- off:\n" + off);
        Assert.That(on.Out, Is.EqualTo("#'user/tf-two\n42\n"), on.ToString());
    }
}
