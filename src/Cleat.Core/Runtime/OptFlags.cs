using System;
using System.Collections.Generic;

namespace Cleat.Core.Runtime;

// CLEAT_NO_OPT 的唯一读取点（cleat-164）。"1" 关掉全部；否则按逗号 / 空白分词后精确匹配记号。
// 旧实现是子串匹配，CLEAT_NO_OPT=accinline 会顺带关掉 inline；"=1" 的语义不变。
// 环境变量在类型初始化时读一次，与原先各处 static readonly 的求值时机相同。
// 记号表与每项用途见 designs/README 的开关附表。
public static class OptFlags
{
    private static readonly bool All = Environment.GetEnvironmentVariable("CLEAT_NO_OPT") == "1";
    private static readonly HashSet<string> Tokens = Tokenize(Environment.GetEnvironmentVariable("CLEAT_NO_OPT") ?? "");

    public static bool Off(string token) => All || Tokens.Contains(token);

    // GcProfile 的宿主测试会在进程启动后设置 / 清除环境变量；这条诊断读取路径保持每次现读。
    public static bool OffNow(string token)
    {
        var value = Environment.GetEnvironmentVariable("CLEAT_NO_OPT");
        return value == "1" || Tokenize(value).Contains(token);
    }

    // 分词规则单独暴露给测试：逗号 / 空白分隔，空记号忽略；"1" 与空值都不是记号。
    public static HashSet<string> Tokenize(string value)
    {
        var tokens = new HashSet<string>(StringComparer.Ordinal);
        if (value is null || value.Length == 0 || value == "1") return tokens;
        foreach (var token in value.Split([',', ' ', '\t', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries))
            tokens.Add(token);
        return tokens;
    }
}
