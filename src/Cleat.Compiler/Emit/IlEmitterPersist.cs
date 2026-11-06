using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using Cleat.Compiler.Lower;
using Cleat.Core.Runtime;

namespace Cleat.Compiler.Emit;

/// <summary>持久化（cleat-176）：镜像与包程序集写成 dll（+ Portable PDB），确定性（MVID / 时间戳按内容 SHA256 回填）。</summary>
public sealed partial class IlEmitter
{
    /// <summary>发射并保存为 dll；pdb 为真时同时写同名 .pdb。</summary>
    public static void Save(UnitDecl unit, string outPath, bool pdb)
    {
        if (unit.Live) throw new InvalidOperationException("emit: live constants can only be emitted in memory");
        var ab = new PersistedAssemblyBuilder(new AssemblyName(unit.Name), typeof(object).Assembly);
        var e = new IlEmitter(unit, ab.DefineDynamicModule(unit.Name), pdb, false, false, null);
        e.Build();
        WritePE(ab, unit, outPath, pdb);
    }

    /// <summary>常量表：fasl 编码、Deflate 压缩后作为 $Constants 的 RVA 初始化数据，类型初始化器经 Fasl.ReadPackedTable 读回。</summary>
    private partial void EmitPackedConstants(ILGenerator il)
    {
        var packed = Deflate(ConstantEncoder.EncodeTable(_unit.Constants));
        var data = _constantsType.DefineInitializedData("data", packed, FieldAttributes.Private | FieldAttributes.Static);
        il.Emit(OpCodes.Ldc_I4, packed.Length);
        il.Emit(OpCodes.Newarr, typeof(byte));
        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Ldtoken, data);
        il.Emit(OpCodes.Call, typeof(RuntimeHelpers).GetMethod(nameof(RuntimeHelpers.InitializeArray), new[] { typeof(Array), typeof(RuntimeFieldHandle) }));
        il.Emit(OpCodes.Call, typeof(Fasl).GetMethod(nameof(Fasl.ReadPackedTable), new[] { typeof(byte[]) }));
    }

    /// <summary>固定压缩级别：同一运行时版本下输出确定。</summary>
    private static byte[] Deflate(byte[] bytes)
    {
        var ms = new MemoryStream();
        using (var z = new DeflateStream(ms, CompressionLevel.Optimal, leaveOpen: true)) z.Write(bytes, 0, bytes.Length);
        return ms.ToArray();
    }

    private static void WritePE(PersistedAssemblyBuilder ab, UnitDecl unit, string outPath, bool pdb)
    {
        MetadataBuilder metadata = ab.GenerateMetadata(out BlobBuilder ilStream, out BlobBuilder fieldData, out MetadataBuilder pdbBuilder);
        // 框架类型改引用契约程序集（cleat-067）
        var contracts = ContractRetargeting.AddContractReferences(metadata);
        Func<IEnumerable<Blob>, BlobContentId> idProvider = blobs =>
        {
            using var h = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            foreach (var blob in blobs) h.AppendData(blob.GetBytes());
            return BlobContentId.FromHash(h.GetHashAndReset());
        };
        DebugDirectoryBuilder debug = null;
        if (pdb)
        {
            var pdbBlob = new BlobBuilder();
            var portablePdb = new PortablePdbBuilder(pdbBuilder, metadata.GetRowCounts(), default, idProvider);
            BlobContentId pdbId = portablePdb.Serialize(pdbBlob);
            string pdbPath = Path.ChangeExtension(outPath, ".pdb");
            using (var fs = File.Create(pdbPath)) pdbBlob.WriteContentTo(fs);
            debug = new DebugDirectoryBuilder();
            debug.AddCodeViewEntry(Path.GetFileName(pdbPath), pdbId, portablePdb.FormatVersion);
        }
        // 嵌入资源（cleat-067）：资源段里每个资源是 4 字节长度加内容
        BlobBuilder resources = null;
        foreach (var (name, path) in unit.Resources)
        {
            resources ??= new BlobBuilder();
            byte[] data = File.ReadAllBytes(path);
            metadata.AddManifestResource(ManifestResourceAttributes.Public, metadata.GetOrAddString(name), default, (uint)resources.Count);
            resources.WriteInt32(data.Length);
            resources.WriteBytes(data);
            resources.Align(8);
        }
        var pe = new ManagedPEBuilder(
          new PEHeaderBuilder(imageCharacteristics: Characteristics.Dll | Characteristics.ExecutableImage),
          new MetadataRootBuilder(metadata), ilStream, fieldData, managedResources: resources,
          debugDirectoryBuilder: debug, deterministicIdProvider: idProvider);
        var peBlob = new BlobBuilder();
        pe.Serialize(peBlob);
        var image = peBlob.ToArray();
        ContractRetargeting.Retarget(image, contracts);
        MakeDeterministic(image);
        File.WriteAllBytes(outPath, image);
    }

    /// <summary>清零 MVID 与 COFF 时间戳 → SHA256 → 回填：相同输入得到逐字节相同的 dll。</summary>
    private static void MakeDeterministic(byte[] image)
    {
        int mvid, ts;
        using (var pe = new PEReader(new MemoryStream(image)))
        {
            var md = pe.GetMetadataReader();
            int heap = md.GetHeapMetadataOffset(HeapIndex.Guid);
            int index = MetadataTokens.GetHeapOffset(md.GetModuleDefinition().Mvid);
            mvid = pe.PEHeaders.MetadataStartOffset + heap + (index - 1) * 16;
            ts = pe.PEHeaders.CoffHeaderStartOffset + 4;
        }
        Array.Clear(image, mvid, 16);
        Array.Clear(image, ts, 4);
        byte[] hash = SHA256.HashData(image);
        Array.Copy(hash, 0, image, mvid, 16);
        Array.Copy(hash, 16, image, ts, 4);
    }
}
