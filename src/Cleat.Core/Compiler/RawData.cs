using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using clojure.lang;

namespace Cleat.Core.Compiler;

/// <summary>
/// 原样取出数据的元素（cleat-089）：Clojure 代码里宿主调用与字段读取的返回值经 RetObject，Int32 会被换成 long；常量编码要区分
/// Int32（如读入器给元数据的行号）与 Clojure 的 long，所以经这些 IFn 取元素（IFn 调用的结果不转换）。
/// </summary>
public static class RawData
{
    /// <summary>(items x) → 元素的向量：Clojure 集合按 seq 顺序（map 为 [k v k v …]），其余 IEnumerable 逐个。</summary>
    public static readonly IFn Items = new ItemsFn();

    /// <summary>(field obj "名字") → 公开实例字段的值。</summary>
    public static readonly IFn Field = new FieldFn();

    private sealed class ItemsFn : AFn
    {
        public override object invoke(object x)
        {
            var items = new List<object>();
            if (x is IPersistentMap)
            {
                for (ISeq s = RT.seq(x); s != null; s = s.next())
                {
                    var e = (IMapEntry)s.first();
                    items.Add(e.key());
                    items.Add(e.val());
                }
            }
            else if (x is Seqable || x is ISeq || x == null)
            {
                for (ISeq s = RT.seq(x); s != null; s = s.next()) items.Add(s.first());
            }
            else if (x is IEnumerable en)
            {
                foreach (var o in en) items.Add(o);
            }
            return LazilyPersistentVector.createOwning(items.ToArray());
        }
    }

    private sealed class FieldFn : AFn
    {
        private static readonly Dictionary<(System.Type, string), FieldInfo> s_fields = new();

        public override object invoke(object obj, object name)
        {
            var key = (obj.GetType(), (string)name);
            FieldInfo f;
            lock (s_fields)
            {
                if (!s_fields.TryGetValue(key, out f))
                    s_fields[key] = f = obj.GetType().GetField((string)name, BindingFlags.Public | BindingFlags.Instance)
                                        ?? throw new System.MissingFieldException(obj.GetType().FullName, (string)name);
            }
            return f.GetValue(obj);
        }
    }
}
