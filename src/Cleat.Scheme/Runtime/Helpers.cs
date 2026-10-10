using System.Diagnostics;
using BigInteger = System.Numerics.BigInteger;

namespace Cleat.Scheme;

/// <summary>
/// 从旧 Helpers.cs 移植的、Scheme 源码直接调用的辅助方法。
/// GetTypeFast、ConvertToDelegate、FFI 转换、枚举与符号互转随 iron-011（互操作）移植；
/// DeserializeAssemblyConstants 由 iron-020 的 fasl 取代。
/// </summary>
public static partial class Helpers
{
    public static object LongToBignum(long l) => Builtins.ToIntegerIfPossible(new BigInteger(l));

    /// <summary>多值 → 单个值（编译器在只需要一个值的位置使用）。</summary>
    public static object UnwrapValue(object o) => o is MultipleValues mv && mv.Length == 1 ? mv[0] : o;

    /// <summary>值 → 多值（已经是多值时原样返回）。</summary>
    public static MultipleValues WrapValue(object o) => o as MultipleValues ?? new MultipleValues(o);

    public static bool StartProcess(Process p)
    {
        if (!p.Start()) return false;
        if (p.StartInfo.RedirectStandardOutput) p.BeginOutputReadLine();
        if (p.StartInfo.RedirectStandardError) p.BeginErrorReadLine();
        return true;
    }

    public static Process MakeProcess(string filename, string args, bool showwindow, object exit, object output, object error)
    {
        var p = new Process { StartInfo = new ProcessStartInfo(filename, args) { CreateNoWindow = !showwindow } };
        if (exit is Procedure exitp)
        {
            p.EnableRaisingEvents = true;
            p.Exited += delegate { exitp.Call(RT.Box(p.ExitCode)); };
        }
        if (output is Procedure outp)
        {
            p.StartInfo.UseShellExecute = false;
            p.StartInfo.RedirectStandardOutput = true;
            p.OutputDataReceived += (s, e) => { if (!string.IsNullOrEmpty(e.Data)) outp.Call(e.Data); };
        }
        if (error is Procedure errorp)
        {
            p.StartInfo.UseShellExecute = false;
            p.StartInfo.RedirectStandardError = true;
            p.ErrorDataReceived += (s, e) => { if (!string.IsNullOrEmpty(e.Data)) errorp.Call(e.Data); };
        }
        return p;
    }
}
