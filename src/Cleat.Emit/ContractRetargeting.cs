using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace Cleat.Emit;

/// <summary>
/// 保存的 dll 里框架类型的引用改指契约程序集（cleat-067，designs/17 §7）。PersistedAssemblyBuilder 按运行时类型所在的程序集写
/// TypeRef，框架类型因此都引用实现程序集 System.Private.CoreLib；C# / F# 编译器只看得到引用程序集（System.Runtime、
/// System.Collections …），引用了包程序集里类型的项目编译时报 CS0012。运行时共享框架目录里的门面程序集（System.Runtime.dll 等）
/// 用 TypeForwardedTo 把各自契约里的类型转发到 System.Private.CoreLib，与引用程序集的划分一致：据此得到 类型 → 契约。
/// 序列化前加上这些契约的 AssemblyRef（没用到的行无害），序列化后把解析范围是 System.Private.CoreLib 的 TypeRef 原地改指契约
/// （解析范围是定宽的编码索引，不移动任何内容）。编译器与运行时（经门面转发）都解析得到。
/// </summary>
internal static class ContractRetargeting
{
    private const string CoreLib = "System.Private.CoreLib";

    // 兼容门面（mscorlib、netstandard、System）也转发，但不是契约
    private static readonly HashSet<string> s_skip = new(StringComparer.OrdinalIgnoreCase) { "mscorlib", "netstandard", "System" };

    private static readonly Lazy<(Dictionary<string, string> TypeToContract, List<AssemblyName> Contracts)> s_map = new(Build);

    /// <summary>加上全部契约的 AssemblyRef；返回 契约名 → 行号（序列化后 <see cref="Retarget"/> 用）。</summary>
    public static Dictionary<string, int> AddContractReferences(MetadataBuilder metadata)
    {
        var rows = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var an in s_map.Value.Contracts)
        {
            var h = metadata.AddAssemblyReference(metadata.GetOrAddString(an.Name), an.Version ?? new Version(0, 0, 0, 0),
                default, metadata.GetOrAddBlob(an.GetPublicKeyToken() ?? Array.Empty<byte>()), default, default);
            rows[an.Name] = MetadataTokens.GetRowNumber(h);
        }
        return rows;
    }

    /// <summary>把解析范围是 System.Private.CoreLib 的 TypeRef 改指它的契约（找得到时）。</summary>
    public static void Retarget(byte[] image, Dictionary<string, int> contractRows)
    {
        if (contractRows.Count == 0) return;
        var map = s_map.Value.TypeToContract;
        var patches = new List<(int Offset, int Size, int Value)>();
        using (var pe = new PEReader(new MemoryStream(image)))
        {
            var md = pe.GetMetadataReader();
            AssemblyReferenceHandle core = default;
            foreach (var h in md.AssemblyReferences)
                if (md.GetString(md.GetAssemblyReference(h).Name) == CoreLib) core = h;
            if (core.IsNil) return;
            int rows = md.GetTableRowCount(TableIndex.TypeRef);
            if (rows == 0) return;
            int rowSize = md.GetTableRowSize(TableIndex.TypeRef);
            int stringIndex = md.GetHeapSize(HeapIndex.String) >= 0x10000 ? 4 : 2;
            int scopeSize = rowSize - 2 * stringIndex;      // ResolutionScope、Name、Namespace
            if (scopeSize != 2 && scopeSize != 4) return;
            int table = pe.PEHeaders.MetadataStartOffset + md.GetTableMetadataOffset(TableIndex.TypeRef);
            for (int i = 1; i <= rows; i++)
            {
                var tr = md.GetTypeReference(MetadataTokens.TypeReferenceHandle(i));
                if (tr.ResolutionScope.Kind != HandleKind.AssemblyReference || (AssemblyReferenceHandle)tr.ResolutionScope != core) continue;
                string name = md.GetString(tr.Namespace) + "." + md.GetString(tr.Name);
                if (!map.TryGetValue(name, out var contract) || !contractRows.TryGetValue(contract, out int row)) continue;
                patches.Add((table + (i - 1) * rowSize, scopeSize, (row << 2) | 2));   // 编码索引 ResolutionScope：AssemblyRef 的标记是 2
            }
        }
        foreach (var (offset, size, value) in patches)
        {
            image[offset] = (byte)value;
            image[offset + 1] = (byte)(value >> 8);
            if (size == 4)
            {
                image[offset + 2] = (byte)(value >> 16);
                image[offset + 3] = (byte)(value >> 24);
            }
        }
    }

    private static (Dictionary<string, string>, List<AssemblyName>) Build()
    {
        var typeToContract = new Dictionary<string, string>(StringComparer.Ordinal);
        var contracts = new SortedDictionary<string, AssemblyName>(StringComparer.Ordinal);
        string dir = Path.GetDirectoryName(typeof(object).Assembly.Location);
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return (typeToContract, new List<AssemblyName>());
        foreach (var file in Directory.GetFiles(dir, "*.dll").OrderBy(f => f, StringComparer.Ordinal))
        {
            string asmName = Path.GetFileNameWithoutExtension(file);
            if (s_skip.Contains(asmName) || asmName.StartsWith("System.Private.", StringComparison.Ordinal)) continue;
            try
            {
                using var pe = new PEReader(File.OpenRead(file));
                if (!pe.HasMetadata) continue;
                var md = pe.GetMetadataReader();
                if (!md.IsAssembly) continue;
                bool any = false;
                foreach (var h in md.ExportedTypes)
                {
                    var e = md.GetExportedType(h);
                    if (e.Implementation.Kind != HandleKind.AssemblyReference) continue;     // 嵌套类型跟着外层走
                    if (md.GetString(md.GetAssemblyReference((AssemblyReferenceHandle)e.Implementation).Name) != CoreLib) continue;
                    string name = md.GetString(e.Namespace) + "." + md.GetString(e.Name);
                    // 同一个类型被多个门面转发时 System.Runtime 优先，其余取文件名序的第一个
                    if (!typeToContract.TryGetValue(name, out var have) || (asmName == "System.Runtime" && have != "System.Runtime"))
                        typeToContract[name] = asmName;
                    any = true;
                }
                if (any) contracts[asmName] = md.GetAssemblyDefinition().GetAssemblyName();
            }
            catch (BadImageFormatException)
            {
                // 本机映像等：不是门面
            }
        }
        var used = typeToContract.Values.ToHashSet(StringComparer.Ordinal);
        return (typeToContract, contracts.Where(kv => used.Contains(kv.Key)).Select(kv => kv.Value).ToList());
    }
}
