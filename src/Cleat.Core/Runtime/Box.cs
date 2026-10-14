namespace Cleat.Core.Runtime;

/// <summary>被赋值且被闭包捕获的局部变量（赋值转换，designs/03 §4 第 5 步；cleat-092 起生成的代码用它，不再用 Cleat.Scheme.Box）。</summary>
public sealed class Box
{
    public object Value;
    public Box(object value) { Value = value; }
}
