namespace Cleat.Scheme;

/// <summary>
/// 带源位置的数据（展开器用于语法错误定位与调试信息）。字段名与旧系统一致：
/// boot/io/ports.sls 通过 clr-field-get 访问 expression、source、stripped。
/// source 形如 (文件名 . "(起始行,起始列) - (结束行,结束列)")，行列从 1 开始，结束列为最后一个字符之后。
/// </summary>
public class Annotation
{
    public readonly object expression;
    public readonly object source;
    public readonly object stripped;

    public Annotation(object expression, object source, object stripped)
    {
        this.expression = expression;
        this.source = source;
        this.stripped = stripped;
    }

    public override string ToString() => source is Cons c ? c.cdr as string : (source ?? "").ToString();
}

/// <summary>列表的注解：expression 为由注解元素组成的列表，stripped 为去掉全部注解后的列表。</summary>
public sealed class ConsAnnotation : Annotation
{
    public ConsAnnotation(Cons expression, object source, Cons stripped) : base(expression, source, stripped) { }
}

/// <summary>源位置区间（行列从 1 开始）。</summary>
public readonly struct SourceSpan
{
    public readonly int StartLine, StartColumn, EndLine, EndColumn;

    public SourceSpan(int startLine, int startColumn, int endLine, int endColumn)
    {
        StartLine = startLine; StartColumn = startColumn; EndLine = endLine; EndColumn = endColumn;
    }

    /// <summary>与旧系统 Microsoft.Scripting.SourceSpan.ToString 相同的格式。</summary>
    public override string ToString() => $"({StartLine},{StartColumn}) - ({EndLine},{EndColumn})";
}

/// <summary>注解对象的原语（boot/io/ports.sls 的 annotation? 等经这里，iron-056）。</summary>
public static class AnnotationPrimitives
{
    [Primitive("$annotation?", Flags = PrimitiveFlags.Unsafe | PrimitiveFlags.Pure)]
    public static object IsAnnotation(object o) => RT.Bool(o is Annotation);

    [Primitive("$annotation-expression", Flags = PrimitiveFlags.Unsafe | PrimitiveFlags.Pure)]
    public static object Expression(object a) => ((Annotation)a).expression;

    [Primitive("$annotation-source", Flags = PrimitiveFlags.Unsafe | PrimitiveFlags.Pure)]
    public static object Source(object a) => ((Annotation)a).source;

    [Primitive("$annotation-stripped", Flags = PrimitiveFlags.Unsafe | PrimitiveFlags.Pure)]
    public static object Stripped(object a) => ((Annotation)a).stripped;
}
