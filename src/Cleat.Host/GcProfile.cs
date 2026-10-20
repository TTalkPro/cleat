using System;
using System.Runtime;

namespace Cleat.Host;

/// <summary>
/// cleat-148：GC profile 只能由 CLR 启动时的环境变量选择；宿主 Main 运行时 GC 已初始化。
/// 这里只声明与校验名字，不在进程内改 DOTNET_* 变量。batch 固定为 22 §0.2 验证过的
/// Server GC + gen0 256MB，用于批负载；没有 <c>CLEAT_GC_PROFILE</c> 时不读取其余配置、不改默认。
/// </summary>
internal static class GcProfile
{
    internal const string EnvironmentVariable = "CLEAT_GC_PROFILE";
    internal const string ServerVariable = "DOTNET_gcServer";
    internal const string Gen0Variable = "DOTNET_GCgen0size";
    internal const string ServerValue = "1";
    internal const string Gen0Value = "0x10000000";

    /// <summary>未声明或 no-opt 退回时为 null；声明无效 / 实际 GC 形态不一致时返回错误。</summary>
    public static string ValidateEnvironment()
    {
        var selectionError = ValidateSelection();
        if (selectionError != null) return selectionError;
        if ((Environment.GetEnvironmentVariable("CLEAT_NO_OPT") ?? "").Contains("gcprofile")) return null;
        var profile = Environment.GetEnvironmentVariable(EnvironmentVariable);
        if (string.IsNullOrEmpty(profile) || profile.Equals("default", StringComparison.OrdinalIgnoreCase))
            return null;
        if (!GCSettings.IsServerGC)
            return "GC profile 'batch' was not applied: GCSettings.IsServerGC is false";
        if (!TryGen0Budget(out var budget) || budget != 0x10000000)
            return "GC profile 'batch' was not applied: gen0 budget is not 0x10000000";
        return null;
    }

    /// <summary>只校验声明与必须由父进程设置的 DOTNET_* 变量；可在 CLR 已启动后做单元测试。</summary>
    internal static string ValidateSelection()
    {
        if ((Environment.GetEnvironmentVariable("CLEAT_NO_OPT") ?? "").Contains("gcprofile")) return null;
        var profile = Environment.GetEnvironmentVariable(EnvironmentVariable);
        if (string.IsNullOrEmpty(profile) || profile.Equals("default", StringComparison.OrdinalIgnoreCase)) return null;
        if (!profile.Equals("batch", StringComparison.OrdinalIgnoreCase))
            return $"unknown GC profile '{profile}': expected 'batch' or 'default'";
        if (Environment.GetEnvironmentVariable(ServerVariable) != ServerValue
            || Environment.GetEnvironmentVariable(Gen0Variable) != Gen0Value)
            return $"GC profile 'batch' must be selected before launch: {ServerVariable}={ServerValue} {Gen0Variable}={Gen0Value}";
        return null;
    }

    private static bool TryGen0Budget(out long budget)
    {
        budget = 0;
        if (GC.GetConfigurationVariables().TryGetValue("GCGen0MaxBudget", out var value)
            && long.TryParse(value?.ToString(), out budget))
            return true;
        budget = 0;
        return false;
    }
}
