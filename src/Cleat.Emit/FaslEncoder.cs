using System.Collections.Generic;

namespace Cleat.Emit;

/// <summary>常量表的中立编码 → 二进制 fasl（designs/05 §3）。实现在 cleat-021；现在只占位。</summary>
public static class FaslEncoder
{
    public static byte[] EncodeTable(List<SNode> constants, string file)
        => throw new LcilException($"{file}: persisted constants are not implemented yet (cleat-021)");
}
