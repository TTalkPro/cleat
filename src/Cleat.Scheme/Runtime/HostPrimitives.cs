using System;
using System.Collections;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using BigInteger = System.Numerics.BigInteger;

namespace Cleat.Scheme;

/// <summary>
/// boot 与 (cleat scheme std …) 用的宿主原语（iron-056，designs/11-portability.md §1 D3）：语言核心不再直接写 clr-*，
/// 每个用到的 .NET 成员在这里包成一个 $ 原语（PortPrimitives.cs 是端口部分）。参数不做类型检查（$ 原语）。
/// 原语表因此是完整的宿主接口（02 §5）；另一个宿主只要提供同名原语。
/// </summary>
public static class HostPrimitives
{
    private static object Unspecified => Cleat.Scheme.Unspecified.Instance;
    private static object Int(long v) => Builtins.ToIntegerIfPossible(v);

    #region 宿主侦测（11 §1 D5）

    [Primitive("host-type", Flags = PrimitiveFlags.Pure)]
    public static object HostType() => Symbol.Intern("clr");

    /// <summary>运行时的描述，如 ".NET 10.0.12"。</summary>
    [Primitive("host-version", Flags = PrimitiveFlags.Pure)]
    public static object HostVersion() => RuntimeInformation.FrameworkDescription;

    /// <summary>宿主-架构-系统，如 clr-x86-64-linux。</summary>
    [Primitive("machine-type", Flags = PrimitiveFlags.Pure)]
    public static object MachineType() => Symbol.Intern("clr-" + CpuArchitectureName() + "-" + OsNameLower());

    /// <summary>处理器架构，SRFI 112 的写法："x86-64"、"i386"、"arm64" …</summary>
    [Primitive("$cpu-architecture", Flags = PrimitiveFlags.Unsafe | PrimitiveFlags.Pure)]
    public static object CpuArchitecture() => CpuArchitectureName();

    private static string CpuArchitectureName() => RuntimeInformation.ProcessArchitecture switch
    {
        Architecture.X64 => "x86-64",
        Architecture.X86 => "i386",
        _ => RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant(),
    };

    /// <summary>"Windows"、"Linux"、"macOS"、"FreeBSD"，其他系统为运行时的描述。</summary>
    [Primitive("$os-name", Flags = PrimitiveFlags.Unsafe | PrimitiveFlags.Pure)]
    public static object OsName() =>
      OperatingSystem.IsWindows() ? "Windows"
      : OperatingSystem.IsLinux() ? "Linux"
      : OperatingSystem.IsMacOS() ? "macOS"
      : OperatingSystem.IsFreeBSD() ? "FreeBSD"
      : RuntimeInformation.OSDescription;

    private static string OsNameLower() =>
      OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsLinux() ? "linux" : OperatingSystem.IsMacOS() ? "macos"
      : OperatingSystem.IsFreeBSD() ? "freebsd" : "unknown";

    [Primitive("$os-description", Flags = PrimitiveFlags.Unsafe | PrimitiveFlags.Pure)]
    public static object OsDescription() => RuntimeInformation.OSDescription;

    [Primitive("$machine-name", Flags = PrimitiveFlags.Unsafe)]
    public static object MachineName() => Environment.MachineName;

    [Primitive("$os-windows?", Flags = PrimitiveFlags.Unsafe | PrimitiveFlags.Pure)]
    public static object IsWindows() => RT.Bool(OperatingSystem.IsWindows());

    [Primitive("$exit", Flags = PrimitiveFlags.Unsafe)]
    public static object Exit(object code)
    {
        Environment.Exit((int)code);
        return Unspecified;
    }

    [Primitive("$gc-collect", Flags = PrimitiveFlags.Unsafe)]
    public static object GcCollect()
    {
        GC.Collect();
        return Unspecified;
    }

    [Primitive("$make-guid", Flags = PrimitiveFlags.Unsafe)]
    public static object MakeGuid() => Guid.NewGuid();

    [Primitive("$get-environment-variable", Flags = PrimitiveFlags.Unsafe)]
    public static object GetEnvironmentVariable(object name) =>
      Environment.GetEnvironmentVariable(name.ToString()) is string s ? SchemeString.Normalize(s) : RT.False;

    #endregion

    #region 编译器的常量编码用（compiler/constants.sls）：检查的是编译器自己的宿主对象

    [Primitive("$compound-condition?", Flags = PrimitiveFlags.Unsafe | PrimitiveFlags.Pure)]
    public static object IsCompoundCondition(object o) => RT.Bool(o is CompoundCondition);

    /// <summary>记录对象（复合条件也是 Record 的子类，但不算）。</summary>
    [Primitive("$record?", Flags = PrimitiveFlags.Unsafe | PrimitiveFlags.Pure)]
    public static object IsRecord(object o) => RT.Bool(o is Record && o is not CompoundCondition);

    [Primitive("$record-rtd", Flags = PrimitiveFlags.Unsafe | PrimitiveFlags.Pure)]
    public static object RecordRtd(object r) => ((Record)r).Rtd;

    [Primitive("$record-fields", Flags = PrimitiveFlags.Unsafe | PrimitiveFlags.Pure)]
    public static object RecordFields(object r) => ((Record)r).Fields;

    [Primitive("$rcd-rtd", Flags = PrimitiveFlags.Unsafe | PrimitiveFlags.Pure)]
    public static object RcdRtd(object rcd) => ((RecordConstructorDescriptor)rcd).type;

    [Primitive("$rcd-parent", Flags = PrimitiveFlags.Unsafe | PrimitiveFlags.Pure)]
    public static object RcdParent(object rcd) => ((RecordConstructorDescriptor)rcd).parent;

    [Primitive("$rcd-protocol", Flags = PrimitiveFlags.Unsafe | PrimitiveFlags.Pure)]
    public static object RcdProtocol(object rcd) => ((RecordConstructorDescriptor)rcd).protocol;

    /// <summary>可变串：SchemeString 的可变串，或旧运行时的 StringBuilder。</summary>
    [Primitive("$string-mutable?", Flags = PrimitiveFlags.Unsafe | PrimitiveFlags.Pure)]
    public static object IsStringMutable(object o) => RT.Bool(o is StringBuilder || (o is SchemeString s && s.IsMutable));

    /// <summary>恰为 eq 散列表（不是自定义散列表或只读表）。</summary>
    [Primitive("$eq-hashtable?", Flags = PrimitiveFlags.Unsafe | PrimitiveFlags.Pure)]
    public static object IsEqHashtable(object o) => RT.Bool(o != null && o.GetType() == typeof(Hashtable));

    private static readonly System.Text.RegularExpressions.Regex s_plainString =
      new System.Text.RegularExpressions.Regex("^[ !#-\\[\\]-~]*\\z", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>只含可打印 ASCII、且没有 " 与 \ 的字符串（Lcil 文本里可整段写出）。</summary>
    [Primitive("$plain-string?", Flags = PrimitiveFlags.Unsafe | PrimitiveFlags.Pure)]
    public static object IsPlainString(object s) => RT.Bool(s_plainString.IsMatch(s.ToString()));

    #endregion

    #region 编译目标（11 §1 D5：target-host-type；不交叉编译时与 host-type 相同）

    /// <summary>当前编译的目标宿主：编译器的 current-compile-target 设置；未设置时为本宿主。</summary>
    [Primitive("target-host-type")]
    public static object TargetHostType() => RuntimeOptions.CompileTarget ?? HostType();

    [Primitive("$set-compile-target!", Flags = PrimitiveFlags.Unsafe)]
    public static object SetCompileTarget(object target)
    {
        RuntimeOptions.CompileTarget = target is Symbol s ? s : null;
        return Unspecified;
    }

    #endregion

    #region 类型谓词（boot/predicates.sls）

    [Primitive("$valuetype-vector?", Flags = PrimitiveFlags.Unsafe | PrimitiveFlags.Pure)]
    public static object IsValueTypeVector(object o) => RT.Bool(o is Array a && a.GetType().GetElementType().IsValueType);

    [Primitive("$condition?", Flags = PrimitiveFlags.Unsafe | PrimitiveFlags.Pure)]
    public static object IsCondition(object o) => RT.Bool(o is Condition);

    [Primitive("$fixnum?", Flags = PrimitiveFlags.Unsafe | PrimitiveFlags.Pure, Inline = "$fixnum?")]
    public static object IsFixnum(object o) => RT.Bool(o is int);

    [Primitive("$flonum?", Flags = PrimitiveFlags.Unsafe | PrimitiveFlags.Pure, Inline = "$flonum?")]
    public static object IsFlonum(object o) => RT.Bool(o is double);

    [Primitive("$bignum?", Flags = PrimitiveFlags.Unsafe | PrimitiveFlags.Pure)]
    public static object IsBignum(object o) => RT.Bool(o is BigInteger);

    [Primitive("$rectnum?", Flags = PrimitiveFlags.Unsafe | PrimitiveFlags.Pure)]
    public static object IsRectnum(object o) => RT.Bool(o is ComplexFraction);

    [Primitive("$ratnum?", Flags = PrimitiveFlags.Unsafe | PrimitiveFlags.Pure)]
    public static object IsRatnum(object o) => RT.Bool(o is Fraction);

    [Primitive("$complexnum?", Flags = PrimitiveFlags.Unsafe | PrimitiveFlags.Pure)]
    public static object IsComplexnum(object o) => RT.Bool(o is Complex64);

    /// <summary>不可变字符串（宿主的字符串对象）。</summary>
    [Primitive("$immutable-string?", Flags = PrimitiveFlags.Unsafe | PrimitiveFlags.Pure)]
    public static object IsImmutableString(object o) => RT.Bool(o is string);

    /// <summary>可变字符串（SchemeString；旧的 StringBuilder 也算）。</summary>
    [Primitive("$mutable-string?", Flags = PrimitiveFlags.Unsafe | PrimitiveFlags.Pure)]
    public static object IsMutableString(object o) => RT.Bool(o is SchemeString || o is StringBuilder);

    [Primitive("$char?", Flags = PrimitiveFlags.Unsafe | PrimitiveFlags.Pure)]
    public static object IsChar(object o) => RT.Bool(o is char || o is SchemeChar);

    [Primitive("$vector?", Flags = PrimitiveFlags.Unsafe | PrimitiveFlags.Pure)]
    public static object IsVector(object o) => RT.Bool(o is object[]);

    [Primitive("$bytevector?", Flags = PrimitiveFlags.Unsafe | PrimitiveFlags.Pure)]
    public static object IsBytevector(object o) => RT.Bool(o is byte[]);

    [Primitive("$symbol?", Flags = PrimitiveFlags.Unsafe | PrimitiveFlags.Pure)]
    public static object IsSymbol(object o) => RT.Bool(o is Symbol);

    [Primitive("$boolean?", Flags = PrimitiveFlags.Unsafe | PrimitiveFlags.Pure)]
    public static object IsBoolean(object o) => RT.Bool(o is bool);

    [Primitive("$procedure?", Flags = PrimitiveFlags.Unsafe | PrimitiveFlags.Pure)]
    public static object IsProcedure(object o) => RT.Bool(o is Procedure);

    [Primitive("$multiple-values?", Flags = PrimitiveFlags.Unsafe | PrimitiveFlags.Pure)]
    public static object IsMultipleValues(object o) => RT.Bool(o is MultipleValues);

    [Primitive("$make-multiple-values", Flags = PrimitiveFlags.Unsafe)]
    public static object MakeMultipleValues(object vector) => new MultipleValues((object[])vector);

    [Primitive("$exception?", Flags = PrimitiveFlags.Unsafe | PrimitiveFlags.Pure)]
    public static object IsException(object o) => RT.Bool(o is Exception);

    [Primitive("$system-exception?", Flags = PrimitiveFlags.Unsafe | PrimitiveFlags.Pure)]
    public static object IsSystemException(object o) => RT.Bool(o is SystemException);

    [Primitive("$exception-message", Flags = PrimitiveFlags.Unsafe)]
    public static object ExceptionMessage(object e) => SchemeString.Normalize(((Exception)e).Message);

    /// <summary>异常的完整文本（类型、消息与调用栈）。</summary>
    [Primitive("$exception->string", Flags = PrimitiveFlags.Unsafe)]
    public static object ExceptionToString(object e) => SchemeString.Normalize(((Exception)e).ToString());

    [Primitive("$type?", Flags = PrimitiveFlags.Unsafe | PrimitiveFlags.Pure)]
    public static object IsType(object o) => RT.Bool(o is Type);

    /// <summary>对象的宿主类型（空表为 System.Object）。</summary>
    [Primitive("$type-of", Flags = PrimitiveFlags.Unsafe | PrimitiveFlags.Pure)]
    public static object TypeOf(object o) => o == null ? typeof(object) : o.GetType();

    /// <summary>对象宿主类型的全名（写器打印 #&lt;clr-type …&gt; 用）。</summary>
    [Primitive("$type-name", Flags = PrimitiveFlags.Unsafe | PrimitiveFlags.Pure)]
    public static object TypeName(object o) => (o == null ? typeof(object) : o.GetType()).FullName;

    /// <summary>对象的宿主字符串形式（ToString）。</summary>
    [Primitive("$object->string", Flags = PrimitiveFlags.Unsafe)]
    public static object ObjectToString(object o) => SchemeString.Normalize(o?.ToString() ?? "");

    [Primitive("$object-hash", Flags = PrimitiveFlags.Unsafe | PrimitiveFlags.Pure)]
    public static object ObjectHash(object o) => RT.Box(o == null ? 0 : o.GetHashCode());

    [Primitive("$stack-trace", Flags = PrimitiveFlags.Unsafe)]
    public static object StackTrace() => Exceptions.GetStackTrace();

    /// <summary>以"程序失败"异常结束（load/program 在顶层程序有未处理条件时调用，宿主据此返回非零退出码）。</summary>
    [Primitive("$program-failed", Flags = PrimitiveFlags.Unsafe)]
    public static object ProgramFailed() => throw new ProgramFailedException();

    #endregion

    #region 字符与字符串（boot/base.sls、unicode.sls）

    [Primitive("$char->integer", Flags = PrimitiveFlags.Unsafe | PrimitiveFlags.Pure)]
    public static object CharToInteger(object ch) => RT.Box(Builtins.CharCode(ch));

    /// <summary>码点 → 字符（BMP 外为 SchemeChar）；范围由调用方检查。</summary>
    [Primitive("$integer->char", Flags = PrimitiveFlags.Unsafe | PrimitiveFlags.Pure)]
    public static object IntegerToChar(object n)
    {
        int k = (int)n;
        return k > 0xFFFF ? new SchemeChar(k) : (char)k;
    }

    [Primitive("$char-upcase", Flags = PrimitiveFlags.Unsafe | PrimitiveFlags.Pure)]
    public static object CharUpcase(object ch) => ch is char c ? char.ToUpper(c) : Builtins.CharUpcaseWide(ch);

    [Primitive("$char-downcase", Flags = PrimitiveFlags.Unsafe | PrimitiveFlags.Pure)]
    public static object CharDowncase(object ch) => ch is char c ? char.ToLower(c) : Builtins.CharDowncaseWide(ch);

    [Primitive("$char-alphabetic?", Flags = PrimitiveFlags.Unsafe | PrimitiveFlags.Pure)]
    public static object IsCharAlphabetic(object ch) => RT.Bool(char.IsLetter(ch.ToString(), 0));

    [Primitive("$char-numeric?", Flags = PrimitiveFlags.Unsafe | PrimitiveFlags.Pure)]
    public static object IsCharNumeric(object ch) => RT.Bool(char.IsNumber(ch.ToString(), 0));

    [Primitive("$char-whitespace?", Flags = PrimitiveFlags.Unsafe | PrimitiveFlags.Pure)]
    public static object IsCharWhitespace(object ch) => RT.Bool(char.IsWhiteSpace(ch.ToString(), 0));

    /// <summary>Unicode 一般类别，R6RS 的两字母符号（Lu、Ll …）。</summary>
    [Primitive("$char-general-category", Flags = PrimitiveFlags.Unsafe | PrimitiveFlags.Pure)]
    public static object CharGeneralCategory(object ch) => Symbol.Intern(char.GetUnicodeCategory(ch.ToString(), 0) switch
    {
        UnicodeCategory.ClosePunctuation => "Pe",
        UnicodeCategory.ConnectorPunctuation => "Pc",
        UnicodeCategory.Control => "Cc",
        UnicodeCategory.CurrencySymbol => "Sc",
        UnicodeCategory.DashPunctuation => "Pd",
        UnicodeCategory.DecimalDigitNumber => "Nd",
        UnicodeCategory.EnclosingMark => "Me",
        UnicodeCategory.FinalQuotePunctuation => "Pf",
        UnicodeCategory.Format => "Cf",
        UnicodeCategory.InitialQuotePunctuation => "Pi",
        UnicodeCategory.LetterNumber => "Nl",
        UnicodeCategory.LineSeparator => "Zl",
        UnicodeCategory.LowercaseLetter => "Ll",
        UnicodeCategory.MathSymbol => "Sm",
        UnicodeCategory.ModifierLetter => "Lm",
        UnicodeCategory.ModifierSymbol => "Sk",
        UnicodeCategory.NonSpacingMark => "Mn",
        UnicodeCategory.OpenPunctuation => "Ps",
        UnicodeCategory.OtherLetter => "Lo",
        UnicodeCategory.OtherNotAssigned => "Cn",
        UnicodeCategory.OtherNumber => "No",
        UnicodeCategory.OtherPunctuation => "Po",
        UnicodeCategory.OtherSymbol => "So",
        UnicodeCategory.ParagraphSeparator => "Zp",
        UnicodeCategory.PrivateUse => "Co",
        UnicodeCategory.SpaceSeparator => "Zs",
        UnicodeCategory.SpacingCombiningMark => "Mc",
        UnicodeCategory.Surrogate => "Cs",
        UnicodeCategory.TitlecaseLetter => "Lt",
        UnicodeCategory.UppercaseLetter => "Lu",
        _ => "Cn",
    });

    [Primitive("$make-string", Flags = PrimitiveFlags.Unsafe)]
    public static object MakeString(object k, object fill) => Builtins.MakeString((int)k, fill);

    [Primitive("$string-length", Flags = PrimitiveFlags.Unsafe | PrimitiveFlags.Pure)]
    public static object StringLength(object s) => RT.Box(Builtins.StringLength(s));

    [Primitive("$string-ref", Flags = PrimitiveFlags.Unsafe | PrimitiveFlags.Pure)]
    public static object StringRef(object s, object k) => Builtins.StringRef(s, (int)k);

    [Primitive("$string-set!", Flags = PrimitiveFlags.Unsafe)]
    public static object StringSet(object s, object k, object ch) => Builtins.StringSet(s, (int)k, ch);

    [Primitive("$string-fill!", Flags = PrimitiveFlags.Unsafe)]
    public static object StringFill(object s, object ch) => Builtins.StringFill(s, ch);

    [Primitive("$chars->string", Flags = PrimitiveFlags.Unsafe)]
    public static object CharsToString(object chars) => Builtins.CharsToString(chars);

    [Primitive("$string->list", Flags = PrimitiveFlags.Unsafe)]
    public static object StringToList(object s) => Builtins.StringToList(s);

    [Primitive("$string-copy", Flags = PrimitiveFlags.Unsafe)]
    public static object StringCopy(object s) => Builtins.StringCopy(s);

    [Primitive("$substring", Flags = PrimitiveFlags.Unsafe)]
    public static object Substring(object s, object start, object end) => Builtins.Substring(s, (int)start, (int)end);

    [Primitive("$string-append", Flags = PrimitiveFlags.Unsafe)]
    public static object StringAppend(object list) => Builtins.StringAppend(list);

    /// <summary>字符串的不可变副本（宿主字符串）。</summary>
    [Primitive("$string->immutable", Flags = PrimitiveFlags.Unsafe)]
    public static object StringToImmutable(object s) => SchemeString.Normalize(s.ToString());

    /// <summary>(string-format 格式 参数表)：宿主的 String.Format。</summary>
    [Primitive("$string-format", Flags = PrimitiveFlags.Unsafe)]
    public static object StringFormat(object fmt, object args) =>
      SchemeString.Normalize(string.Format(fmt.ToString(), Builtins.ListToVector(args)));

    [Primitive("$string-compare", Flags = PrimitiveFlags.Unsafe | PrimitiveFlags.Pure)]
    public static object StringCompare(object a, object b) => RT.Box(Builtins.StringCompare(a, b));

    [Primitive("$string-lower", Flags = PrimitiveFlags.Unsafe | PrimitiveFlags.Pure)]
    public static object StringLower(object s) => SchemeString.Normalize(s.ToString().ToLowerInvariant());

    /// <summary>(string-replace 串 旧 新)：全部替换。</summary>
    [Primitive("$string-replace", Flags = PrimitiveFlags.Unsafe | PrimitiveFlags.Pure)]
    public static object StringReplace(object s, object oldValue, object newValue) =>
      SchemeString.Normalize(s.ToString().Replace(oldValue.ToString(), newValue.ToString()));

    /// <summary>Unicode 规范化，form 为符号 nfc / nfd / nfkc / nfkd。</summary>
    [Primitive("$string-normalize", Flags = PrimitiveFlags.Unsafe | PrimitiveFlags.Pure)]
    public static object StringNormalize(object s, object form)
    {
        var f = ((Symbol)form).Name.ToLowerInvariant() switch
        {
            "nfc" or "formc" => NormalizationForm.FormC,
            "nfd" or "formd" => NormalizationForm.FormD,
            "nfkc" or "formkc" => NormalizationForm.FormKC,
            "nfkd" or "formkd" => NormalizationForm.FormKD,
            _ => throw new ArgumentException("normalization form", nameof(form)),
        };
        return SchemeString.Normalize(s.ToString().Normalize(f));
    }

    [Primitive("$symbol-name", Flags = PrimitiveFlags.Unsafe | PrimitiveFlags.Pure)]
    public static object SymbolName(object sym) => SchemeString.Normalize(((Symbol)sym).Name);

    [Primitive("$string->symbol", Flags = PrimitiveFlags.Unsafe | PrimitiveFlags.Pure)]
    public static object StringToSymbol(object s) => Symbol.Intern(s.ToString());

    [Primitive("$symbol-value-set!", Flags = PrimitiveFlags.Unsafe)]
    public static object SymbolValueSet(object sym, object value)
    {
        ((Symbol)sym).Value = value;
        return Unspecified;
    }

    #endregion

    #region 向量、表、散列表

    [Primitive("$make-vector", Flags = PrimitiveFlags.Unsafe)]
    public static object MakeVector(object k)
    {
        // 未填充的槽为 '()（R6RS 未规定内容；与 '() 曾是 null 时的行为一致，不暴露 #nil）
        var v = new object[(int)k];
        Array.Fill(v, EmptyList.Instance);
        return v;
    }

    /// <summary>(vector-fill! v x) 的主体：Array.Fill（iron-067，原来是 Scheme 循环，每元素约 3 ns）。</summary>
    [Primitive("$vector-fill!", Flags = PrimitiveFlags.Unsafe)]
    public static object VectorFill(object v, object value)
    {
        Array.Fill((object[])v, value);
        return Unspecified;
    }

    /// <summary>任何宿主数组的长度（object[]、byte[]、int[]、double[] …）。</summary>
    [Primitive("$vector-length", Flags = PrimitiveFlags.Unsafe | PrimitiveFlags.Pure)]
    public static object VectorLength(object v) => RT.Box(((Array)v).Length);

    [Primitive("$vector->list", Flags = PrimitiveFlags.Unsafe)]
    public static object VectorToList(object v) => Cons.FromList((IEnumerable)v);

    [Primitive("$list->vector", Flags = PrimitiveFlags.Unsafe)]
    public static object ListToVector(object list) => Builtins.ListToVector(list);

    [Primitive("$list-copy", Flags = PrimitiveFlags.Unsafe)]
    public static object ListCopy(object list)
    {
        if (list is not Cons) return Cons.FromList((IEnumerable)list);
        // 直接沿 cdr 复制，不经 IEnumerable 枚举器（iron-067）；与 FromList 一样遇到非序对的 cdr 即停
        Cons first = null, last = null;
        for (var t = list; t is Cons c; t = c.cdr)
        {
            var n = new Cons(c.car);
            if (last == null) first = n; else last.cdr = n;
            last = n;
        }
        return first;
    }

    private static Type ElementType(object typeName) => ((Symbol)typeName).Name switch
    {
        "Double" => typeof(double),
        "Int32" => typeof(int),
        "Byte" => typeof(byte),
        "Char" => typeof(char),
        "Object" => typeof(object),
        _ => throw new ArgumentException("element type", nameof(typeName)),
    };

    /// <summary>带类型的向量（flvector、fxvector …，boot/vectors.sls）：元素类型是符号 Double / Int32 …</summary>
    [Primitive("$typed-vector?", Flags = PrimitiveFlags.Unsafe | PrimitiveFlags.Pure)]
    public static object IsTypedVector(object typeName, object o) =>
      RT.Bool(o is Array a && a.GetType().GetElementType() == ElementType(typeName));

    [Primitive("$make-typed-vector", Flags = PrimitiveFlags.Unsafe)]
    public static object MakeTypedVector(object typeName, object k) => Array.CreateInstance(ElementType(typeName), (int)k);

    [Primitive("$list->typed-vector", Flags = PrimitiveFlags.Unsafe)]
    public static object ListToTypedVector(object typeName, object list)
    {
        var items = Builtins.ListToVector(list);
        var r = Array.CreateInstance(ElementType(typeName), items.Length);
        var et = ElementType(typeName);
        for (int i = 0; i < items.Length; i++) r.SetValue(Interop.ConvertTo(items[i], et, "list->vector"), i);
        return r;
    }

    [Primitive("$set-car!", Flags = PrimitiveFlags.Unsafe)]
    public static object SetCar(object pair, object v)
    {
        ((Cons)pair).car = v;
        return Unspecified;
    }

    [Primitive("$set-cdr!", Flags = PrimitiveFlags.Unsafe)]
    public static object SetCdr(object pair, object v)
    {
        ((Cons)pair).cdr = v;
        return Unspecified;
    }

    /// <summary>(vector-sort! 比较过程 向量)：就地排序；比较过程不一致时宿主抛出异常，由调用方转为 &amp;assertion。</summary>
    [Primitive("$vector-sort!", Flags = PrimitiveFlags.Unsafe)]
    public static object VectorSort(object less, object vector)
    {
        var p = (Procedure)less;
        Array.Sort((object[])vector, (a, b) => ReferenceEquals(a, b) ? 0 : RT.IsTrue(p.Invoke2(a, b)) ? -1 : 1);
        return Unspecified;
    }

    [Primitive("$vector-clone", Flags = PrimitiveFlags.Unsafe)]
    public static object VectorClone(object v) => ((Array)v).Clone();

    [Primitive("$vector-reverse!", Flags = PrimitiveFlags.Unsafe)]
    public static object VectorReverse(object v)
    {
        Array.Reverse((Array)v);
        return Unspecified;
    }

    /// <summary>obj 在向量中的下标（按 Equals），没有为 -1。</summary>
    [Primitive("$vector-index-of", Flags = PrimitiveFlags.Unsafe)]
    public static object VectorIndexOf(object v, object obj) => RT.Box(Array.IndexOf((object[])v, obj));

    [Primitive("$make-random", Flags = PrimitiveFlags.Unsafe)]
    public static object MakeRandom() => new Random();

    [Primitive("$random-next", Flags = PrimitiveFlags.Unsafe)]
    public static object RandomNext(object random, object k) => RT.Box(((Random)random).Next((int)k));

    [Primitive("$hashtable?", Flags = PrimitiveFlags.Unsafe | PrimitiveFlags.Pure)]
    public static object IsHashtable(object o) => RT.Bool(o is Hashtable);

    /// <summary>散列表的键：空表（null）换成哨兵，宿主散列表的键不能是 null。</summary>
    [Primitive("$hashtable-to-null", Flags = PrimitiveFlags.Unsafe | PrimitiveFlags.Pure)]
    public static object HashtableToNull(object o) => Hashtables.ToNull(o);

    [Primitive("$hashtable-from-null", Flags = PrimitiveFlags.Unsafe | PrimitiveFlags.Pure)]
    public static object HashtableFromNull(object o) => Hashtables.FromNull(o);

    [Primitive("$make-eq-hashtable", Flags = PrimitiveFlags.Unsafe)]
    public static object MakeEqHashtable(object k) => new Hashtable((int)k);

    [Primitive("$make-hashtable", Flags = PrimitiveFlags.Unsafe)]
    public static object MakeHashtable(object hash, object equiv, object k) =>
      new HashtableEx((int)k, new HashComparer((Procedure)hash, (Procedure)equiv));

    [Primitive("$hashtable-clone", Flags = PrimitiveFlags.Unsafe)]
    public static object HashtableClone(object ht) => ((Hashtable)ht).Clone();

    /// <summary>只读副本：自定义散列表保留散列与等价过程。</summary>
    [Primitive("$hashtable-readonly-copy", Flags = PrimitiveFlags.Unsafe)]
    public static object HashtableReadOnlyCopy(object ht) =>
      ht is HashtableEx ex ? ex.MakeReadOnly() : new ReadOnlyHashtable((Hashtable)ht);

    [Primitive("$hashtable-mutable?", Flags = PrimitiveFlags.Unsafe | PrimitiveFlags.Pure)]
    public static object IsHashtableMutable(object ht) => RT.Bool(ht is not ReadOnlyHashtable);

    /// <summary>键的向量（哨兵已换回空表）。</summary>
    [Primitive("$hashtable-keys", Flags = PrimitiveFlags.Unsafe)]
    public static object HashtableKeys(object ht)
    {
        var h = (Hashtable)ht;
        var keys = new object[h.Count];
        int i = 0;
        foreach (var k in h.Keys) keys[i++] = Hashtables.FromNull(k);
        return keys;
    }

    /// <summary>自定义散列表的等价过程；eq / eqv 表为 #f。</summary>
    [Primitive("$hashtable-equivalence-function", Flags = PrimitiveFlags.Unsafe | PrimitiveFlags.Pure)]
    public static object HashtableEquivalenceFunction(object ht) => ht switch
    {
        HashtableEx ex => (object)ex.EqualityFunction ?? RT.False,
        ReadOnlyHashtable ro => (object)ro.EqualityFunction ?? RT.False,
        _ => RT.False,
    };

    /// <summary>自定义散列表的散列过程；eq 表为 #f。</summary>
    [Primitive("$hashtable-hash-function", Flags = PrimitiveFlags.Unsafe | PrimitiveFlags.Pure)]
    public static object HashtableHashFunction(object ht) => ht switch
    {
        HashtableEx ex => (object)ex.HashFunction ?? RT.False,
        ReadOnlyHashtable ro => (object)ro.HashFunction ?? RT.False,
        _ => RT.False,
    };

    [Primitive("$hashtable-count", Flags = PrimitiveFlags.Unsafe)]
    public static object HashtableCount(object ht) => RT.Box(((Hashtable)ht).Count);

    /// <summary>取值（键已换过哨兵）；没有时为空表，调用方再用 $hashtable-contains? 区分。</summary>
    [Primitive("$hashtable-get", Flags = PrimitiveFlags.Unsafe)]
    public static object HashtableGet(object ht, object key)
    {
        // 缺失的键返回 '()（hashtable-ref 据此再查 contains?；'() 曾经就是 null）；存着 #nil 的键返回 null
        var h = (Hashtable)ht;
        var v = h[key];
        return v == null && !h.ContainsKey(key) ? EmptyList.Instance : v;
    }

    [Primitive("$hashtable-put!", Flags = PrimitiveFlags.Unsafe)]
    public static object HashtablePut(object ht, object key, object value)
    {
        ((Hashtable)ht)[key] = value;
        return Unspecified;
    }

    [Primitive("$hashtable-remove!", Flags = PrimitiveFlags.Unsafe)]
    public static object HashtableRemove(object ht, object key)
    {
        ((Hashtable)ht).Remove(key);
        return Unspecified;
    }

    [Primitive("$hashtable-contains?", Flags = PrimitiveFlags.Unsafe)]
    public static object HashtableContains(object ht, object key) => RT.Bool(((Hashtable)ht).ContainsKey(key));

    [Primitive("$hashtable-clear!", Flags = PrimitiveFlags.Unsafe)]
    public static object HashtableClear(object ht)
    {
        ((Hashtable)ht).Clear();
        return Unspecified;
    }

    [Primitive("$string-hash", Flags = PrimitiveFlags.Unsafe | PrimitiveFlags.Pure)]
    public static object StringHash(object s) => RT.Box(Builtins.StringHash(s));

    [Primitive("$string-ci-hash", Flags = PrimitiveFlags.Unsafe | PrimitiveFlags.Pure)]
    public static object StringCiHash(object s) => RT.Box(Builtins.StringCiHash(s));

    #endregion

    #region 字节向量（boot/bytevectors.sls）

    [Primitive("$bytevector-length", Flags = PrimitiveFlags.Unsafe | PrimitiveFlags.Pure)]
    public static object BytevectorLength(object bv) => RT.Box(((byte[])bv).Length);

    [Primitive("$make-bytevector", Flags = PrimitiveFlags.Unsafe)]
    public static object MakeBytevector(object k) => new byte[(int)k];

    [Primitive("$bytevector-copy!", Flags = PrimitiveFlags.Unsafe)]
    public static object BytevectorCopy(object src, object s1, object dst, object s2, object len)
    {
        Buffer.BlockCopy((byte[])src, (int)s1, (byte[])dst, (int)s2, (int)len);
        return Unspecified;
    }

    [Primitive("$bytevector-clone", Flags = PrimitiveFlags.Unsafe)]
    public static object BytevectorClone(object bv) => ((byte[])bv).Clone();

    [Primitive("$bytevector-reverse!", Flags = PrimitiveFlags.Unsafe)]
    public static object BytevectorReverse(object bv)
    {
        Array.Reverse((byte[])bv);
        return Unspecified;
    }

    [Primitive("$make-utf16-codec", Flags = PrimitiveFlags.Unsafe)]
    public static object MakeUtf16Codec(object bigEndian) => new UnicodeEncoding(RT.IsTrue(bigEndian), false);

    [Primitive("$make-utf32-codec", Flags = PrimitiveFlags.Unsafe)]
    public static object MakeUtf32Codec(object bigEndian) => new UTF32Encoding(RT.IsTrue(bigEndian), false);

    [Primitive("$codec-get-bytes", Flags = PrimitiveFlags.Unsafe)]
    public static object CodecGetBytes(object codec, object s) => ((Encoding)codec).GetBytes(s.ToString());

    [Primitive("$codec-get-string", Flags = PrimitiveFlags.Unsafe)]
    public static object CodecGetString(object codec, object bv) => SchemeString.Normalize(((Encoding)codec).GetString((byte[])bv));

    /// <summary>小端字节 → 无符号整数，size 为 2 / 4 / 8。</summary>
    [Primitive("$bytes->uint", Flags = PrimitiveFlags.Unsafe | PrimitiveFlags.Pure)]
    public static object BytesToUint(object bv, object size)
    {
        var b = (byte[])bv;
        return (int)size switch
        {
            2 => RT.Box(BitConverter.ToUInt16(b, 0)),
            4 => Builtins.ToIntegerIfPossible(BitConverter.ToUInt32(b, 0)),
            _ => Builtins.ToIntegerIfPossible(BitConverter.ToUInt64(b, 0)),
        };
    }

    [Primitive("$bytes->sint", Flags = PrimitiveFlags.Unsafe | PrimitiveFlags.Pure)]
    public static object BytesToSint(object bv, object size)
    {
        var b = (byte[])bv;
        return (int)size switch
        {
            2 => RT.Box(BitConverter.ToInt16(b, 0)),
            4 => RT.Box(BitConverter.ToInt32(b, 0)),
            _ => Builtins.ToIntegerIfPossible(BitConverter.ToInt64(b, 0)),
        };
    }

    /// <summary>无符号整数 → 小端字节，size 为 2 / 4 / 8；放不下时宿主抛出溢出异常。</summary>
    [Primitive("$uint->bytes", Flags = PrimitiveFlags.Unsafe | PrimitiveFlags.Pure)]
    public static object UintToBytes(object n, object size) => (int)size switch
    {
        2 => BitConverter.GetBytes(checked((ushort)ToUInt64(n))),
        4 => BitConverter.GetBytes(checked((uint)ToUInt64(n))),
        _ => BitConverter.GetBytes(ToUInt64(n)),
    };

    [Primitive("$sint->bytes", Flags = PrimitiveFlags.Unsafe | PrimitiveFlags.Pure)]
    public static object SintToBytes(object n, object size) => (int)size switch
    {
        2 => BitConverter.GetBytes(checked((short)ToInt64(n))),
        4 => BitConverter.GetBytes(checked((int)ToInt64(n))),
        _ => BitConverter.GetBytes(ToInt64(n)),
    };

    // 超出范围时宿主抛出溢出异常
    private static ulong ToUInt64(object n) => n is BigInteger b ? (ulong)b : checked((ulong)(int)n);
    private static long ToInt64(object n) => n is BigInteger b ? (long)b : (int)n;

    /// <summary>大整数的小端补码字节（最短）。</summary>
    [Primitive("$bignum->bytes", Flags = PrimitiveFlags.Unsafe | PrimitiveFlags.Pure)]
    public static object BignumToBytes(object b) => ToBignum(b).ToByteArray();

    [Primitive("$bytes->bignum", Flags = PrimitiveFlags.Unsafe | PrimitiveFlags.Pure)]
    public static object BytesToBignum(object bv) => Builtins.ToIntegerIfPossible(new BigInteger((byte[])bv));

    [Primitive("$bytes->single", Flags = PrimitiveFlags.Unsafe | PrimitiveFlags.Pure)]
    public static object BytesToSingle(object bv) => (double)BitConverter.ToSingle((byte[])bv, 0);

    [Primitive("$bytes->double", Flags = PrimitiveFlags.Unsafe | PrimitiveFlags.Pure)]
    public static object BytesToDouble(object bv) => BitConverter.ToDouble((byte[])bv, 0);

    [Primitive("$single->bytes", Flags = PrimitiveFlags.Unsafe | PrimitiveFlags.Pure)]
    public static object SingleToBytes(object x) => BitConverter.GetBytes(Convert.ToSingle(ToDouble(x)));

    [Primitive("$double->bytes", Flags = PrimitiveFlags.Unsafe | PrimitiveFlags.Pure)]
    public static object DoubleToBytes(object x) => BitConverter.GetBytes(ToDouble(x));

    #endregion

    #region 数（boot/numbers.sls、arithmetic/*.sls）

    private static BigInteger ToBignum(object o) => o is BigInteger b ? b : new BigInteger((int)o);
    private static Fraction ToFraction(object o) => o switch
    {
        Fraction f => f,
        int i => i,
        BigInteger b => b,
        double d => d,
        _ => throw new InvalidCastException("not a rational: " + o),
    };
    private static double ToDouble(object o) => o switch
    {
        double d => d,
        int i => i,
        BigInteger b => b.ToFloat64(),
        Fraction f => f.ToDouble(null),
        _ => Convert.ToDouble(o, CultureInfo.InvariantCulture),
    };

    [Primitive("$make-rectnum", Flags = PrimitiveFlags.Unsafe | PrimitiveFlags.Pure)]
    public static object MakeRectnum(object real, object imag) => ComplexFraction.Make(ToFraction(real), ToFraction(imag));

    [Primitive("$rectnum-real", Flags = PrimitiveFlags.Unsafe | PrimitiveFlags.Pure)]
    public static object RectnumReal(object c) => Interop.ConvertFrom(((ComplexFraction)c).Real, typeof(Fraction));

    [Primitive("$rectnum-imag", Flags = PrimitiveFlags.Unsafe | PrimitiveFlags.Pure)]
    public static object RectnumImag(object c) => Interop.ConvertFrom(((ComplexFraction)c).Imag, typeof(Fraction));

    [Primitive("$ratnum-numerator", Flags = PrimitiveFlags.Unsafe | PrimitiveFlags.Pure)]
    public static object RatnumNumerator(object r) => Builtins.ToIntegerIfPossible(((Fraction)r).Numerator);

    [Primitive("$ratnum-denominator", Flags = PrimitiveFlags.Unsafe | PrimitiveFlags.Pure)]
    public static object RatnumDenominator(object r) => Builtins.ToIntegerIfPossible(((Fraction)r).Denominator);

    [Primitive("$make-complexnum", Flags = PrimitiveFlags.Unsafe | PrimitiveFlags.Pure)]
    public static object MakeComplexnum(object real, object imag) => Complex64.Make(ToDouble(real), ToDouble(imag));

    [Primitive("$complexnum-real", Flags = PrimitiveFlags.Unsafe | PrimitiveFlags.Pure)]
    public static object ComplexnumReal(object c) => ((Complex64)c).Real;

    [Primitive("$complexnum-imag", Flags = PrimitiveFlags.Unsafe | PrimitiveFlags.Pure)]
    public static object ComplexnumImag(object c) => ((Complex64)c).Imag;

    [Primitive("$bignum-quotient", Flags = PrimitiveFlags.Unsafe | PrimitiveFlags.Pure)]
    public static object BignumQuotient(object a, object b) => Builtins.ToIntegerIfPossible(ToBignum(a) / ToBignum(b));

    [Primitive("$bignum-remainder", Flags = PrimitiveFlags.Unsafe | PrimitiveFlags.Pure)]
    public static object BignumRemainder(object a, object b) => Builtins.ToIntegerIfPossible(ToBignum(a) % ToBignum(b));

    [Primitive("$bignum->fixnum", Flags = PrimitiveFlags.Unsafe | PrimitiveFlags.Pure)]
    public static object BignumToFixnum(object b) => RT.Box((int)(BigInteger)b);

    [Primitive("$bignum->flonum", Flags = PrimitiveFlags.Unsafe | PrimitiveFlags.Pure)]
    public static object BignumToFlonum(object b) => ((BigInteger)b).ToFloat64();

    [Primitive("$bignum-fits-fixnum?", Flags = PrimitiveFlags.Unsafe | PrimitiveFlags.Pure)]
    public static object BignumFitsFixnum(object b) => RT.Bool(((BigInteger)b).IsInt32());

    [Primitive("$fixnum->bignum", Flags = PrimitiveFlags.Unsafe | PrimitiveFlags.Pure)]
    public static object FixnumToBignum(object n) => new BigInteger((int)n);

    /// <summary>
    /// 有限 flonum 的精确值（iron-064）：整数值时直接得 fixnum / bignum，否则为分数。
    /// 原来经 <c>$flonum->ratnum</c> 构造分数再化简，整数值也要求最大公约数（一次 181ns）。
    /// </summary>
    [Primitive("$flonum->exact", Flags = PrimitiveFlags.Unsafe | PrimitiveFlags.Pure)]
    public static object FlonumToExact(object d)
    {
        double x = (double)d;
        if (Math.Floor(x) == x)
            return x >= int.MinValue && x <= int.MaxValue ? RT.Box((int)x) : Builtins.ToIntegerIfPossible(new BigInteger(x));
        return (Fraction)x;   // 非整数值：分母不为 1
    }

    [Primitive("$flonum->ratnum", Flags = PrimitiveFlags.Unsafe | PrimitiveFlags.Pure)]
    public static object FlonumToRatnum(object d) => (Fraction)(double)d;

    [Primitive("$ratnum->flonum", Flags = PrimitiveFlags.Unsafe | PrimitiveFlags.Pure)]
    public static object RatnumToFlonum(object r) => ((Fraction)r).ToDouble(null);

    [Primitive("$fixnum->flonum", Flags = PrimitiveFlags.Unsafe | PrimitiveFlags.Pure)]
    public static object FixnumToFlonum(object n) => (double)(int)n;

    [Primitive("$complexnum->rectnum", Flags = PrimitiveFlags.Unsafe | PrimitiveFlags.Pure)]
    public static object ComplexnumToRectnum(object c) => (ComplexFraction)(Complex64)c;

    [Primitive("$rectnum->complexnum", Flags = PrimitiveFlags.Unsafe | PrimitiveFlags.Pure)]
    public static object RectnumToComplexnum(object c) => ((ComplexFraction)c).ToComplex64();

    /// <summary>大整数按 2、8、10、16 进制写出（iron-059；原来在 Scheme 中逐位 div/mod，O(n²)）。</summary>
    [Primitive("$bignum->string", Flags = PrimitiveFlags.Unsafe | PrimitiveFlags.Pure)]
    public static object BignumToString(object b, object radix) => BigIntegers.ToString(ToBignum(b), (int)radix);

    [Primitive("$fixnum->string", Flags = PrimitiveFlags.Unsafe | PrimitiveFlags.Pure)]
    public static object FixnumToString(object n, object radix) => Convert.ToString((int)n, (int)radix);

    /// <summary>两个实数的比较：-1 / 0 / 1，有 NaN 为 2，有非实数为 #f（见 Builtins.RealCompare）。</summary>
    [Primitive("$real-compare", Flags = PrimitiveFlags.Unsafe | PrimitiveFlags.Pure)]
    public static object RealCompare(object a, object b)
    {
        int c = Builtins.RealCompare(a, b);
        return c == Builtins.NotReal ? RT.False : RT.Box(c);
    }

    [Primitive("$exact-compare", Flags = PrimitiveFlags.Unsafe | PrimitiveFlags.Pure)]
    public static object ExactCompare(object a, object b) => RT.Box(Builtins.ExactCompare(a, b));

    [Primitive("$inexact-compare", Flags = PrimitiveFlags.Unsafe | PrimitiveFlags.Pure)]
    public static object InexactCompare(object a, object b) => RT.Box(Builtins.InexactCompare(a, b));

    [Primitive("$bignum-expt", Flags = PrimitiveFlags.Unsafe | PrimitiveFlags.Pure)]
    public static object BignumExpt(object b, object n) => Builtins.ToIntegerIfPossible(BigInteger.Pow(ToBignum(b), (int)n));

    [Primitive("$bignum-not", Flags = PrimitiveFlags.Unsafe | PrimitiveFlags.Pure)]
    public static object BignumNot(object b) => Builtins.ToIntegerIfPossible(~ToBignum(b));

    [Primitive("$bignum-and", Flags = PrimitiveFlags.Unsafe | PrimitiveFlags.Pure)]
    public static object BignumAnd(object a, object b) => Builtins.ToIntegerIfPossible(ToBignum(a) & ToBignum(b));

    [Primitive("$bignum-ior", Flags = PrimitiveFlags.Unsafe | PrimitiveFlags.Pure)]
    public static object BignumIor(object a, object b) => Builtins.ToIntegerIfPossible(ToBignum(a) | ToBignum(b));

    [Primitive("$bignum-xor", Flags = PrimitiveFlags.Unsafe | PrimitiveFlags.Pure)]
    public static object BignumXor(object a, object b) => Builtins.ToIntegerIfPossible(ToBignum(a) ^ ToBignum(b));

    [Primitive("$bignum-negative?", Flags = PrimitiveFlags.Unsafe | PrimitiveFlags.Pure)]
    public static object IsBignumNegative(object b) => RT.Bool(ToBignum(b).Sign < 0);

    /// <summary>R6RS bitwise-length：负数为其补数的位数（GetBitLength 不含符号位，正好如此）。</summary>
    [Primitive("$bignum-bit-length", Flags = PrimitiveFlags.Unsafe | PrimitiveFlags.Pure)]
    public static object BignumBitLength(object b) => RT.Box((int)ToBignum(b).GetBitLength());

    /// <summary>R6RS bitwise-bit-count：非负数为 1 的个数，负数为 (bitwise-not (bitwise-bit-count (bitwise-not ei)))。</summary>
    [Primitive("$bignum-bit-count", Flags = PrimitiveFlags.Unsafe | PrimitiveFlags.Pure)]
    public static object BignumBitCount(object b)
    {
        var x = ToBignum(b);
        return x.Sign >= 0 ? Builtins.ToIntegerIfPossible(BigInteger.PopCount(x)) : Builtins.ToIntegerIfPossible(-BigInteger.PopCount(~x) - 1);
    }

    /// <summary>最低的 1 位的下标；0 为 -1（补码下负数与其绝对值相同）。</summary>
    [Primitive("$bignum-first-bit-set", Flags = PrimitiveFlags.Unsafe | PrimitiveFlags.Pure)]
    public static object BignumFirstBitSet(object b)
    {
        var x = ToBignum(b);
        return RT.Box(x.IsZero ? -1 : (int)BigInteger.TrailingZeroCount(x));
    }

    /// <summary>第 k 位（补码，k ≥ 0）是否为 1。</summary>
    [Primitive("$bignum-bit-set?", Flags = PrimitiveFlags.Unsafe | PrimitiveFlags.Pure)]
    public static object IsBignumBitSet(object b, object k) => RT.Bool(!(ToBignum(b) >> (int)k).IsEven);

    /// <summary>非负数 b 的低 width 位逆序（bitwise-reverse-bit-field 用；原来逐位拼接，O(n²)）。</summary>
    [Primitive("$bignum-reverse-bits", Flags = PrimitiveFlags.Unsafe | PrimitiveFlags.Pure)]
    public static object BignumReverseBits(object b, object width)
    {
        int w = (int)width;
        byte[] src = ToBignum(b).ToByteArray(isUnsigned: true, isBigEndian: false);
        var dst = new byte[w / 8 + 2];   // 末尾留 0 字节，结果非负
        for (int i = 0; i < w; i++)
        {
            int j = w - 1 - i;
            if ((i >> 3) < src.Length && (src[i >> 3] & (1 << (i & 7))) != 0) dst[j >> 3] |= (byte)(1 << (j & 7));
        }
        return Builtins.ToIntegerIfPossible(new BigInteger(dst));
    }

    [Primitive("$bignum-shift-left", Flags = PrimitiveFlags.Unsafe | PrimitiveFlags.Pure)]
    public static object BignumShiftLeft(object b, object k) => Builtins.ToIntegerIfPossible(ToBignum(b) << (int)k);

    [Primitive("$bignum-shift-right", Flags = PrimitiveFlags.Unsafe | PrimitiveFlags.Pure)]
    public static object BignumShiftRight(object b, object k) => Builtins.ToIntegerIfPossible(ToBignum(b) >> (int)k);

    [Primitive("$fxabs", Flags = PrimitiveFlags.Unsafe | PrimitiveFlags.Pure)]
    public static object FxAbs(object n) => RT.Box(Math.Abs((int)n));

    [Primitive("$flinfinite?", Flags = PrimitiveFlags.Unsafe | PrimitiveFlags.Pure)]
    public static object IsFlInfinite(object d) => RT.Bool(double.IsInfinity((double)d));

    [Primitive("$flnan?", Flags = PrimitiveFlags.Unsafe | PrimitiveFlags.Pure)]
    public static object IsFlNan(object d) => RT.Bool(double.IsNaN((double)d));

    [Primitive("$flsin", Flags = PrimitiveFlags.Unsafe | PrimitiveFlags.Pure)] public static object FlSin(object d) => Math.Sin(ToDouble(d));
    [Primitive("$flcos", Flags = PrimitiveFlags.Unsafe | PrimitiveFlags.Pure)] public static object FlCos(object d) => Math.Cos(ToDouble(d));
    [Primitive("$fltan", Flags = PrimitiveFlags.Unsafe | PrimitiveFlags.Pure)] public static object FlTan(object d) => Math.Tan(ToDouble(d));
    [Primitive("$flasin", Flags = PrimitiveFlags.Unsafe | PrimitiveFlags.Pure)] public static object FlAsin(object d) => Math.Asin(ToDouble(d));
    [Primitive("$flacos", Flags = PrimitiveFlags.Unsafe | PrimitiveFlags.Pure)] public static object FlAcos(object d) => Math.Acos(ToDouble(d));
    [Primitive("$flatan", Flags = PrimitiveFlags.Unsafe | PrimitiveFlags.Pure)] public static object FlAtan(object d) => Math.Atan(ToDouble(d));
    [Primitive("$flatan2", Flags = PrimitiveFlags.Unsafe | PrimitiveFlags.Pure)] public static object FlAtan2(object y, object x) => Math.Atan2(ToDouble(y), ToDouble(x));
    [Primitive("$flsinh", Flags = PrimitiveFlags.Unsafe | PrimitiveFlags.Pure)] public static object FlSinh(object d) => Math.Sinh(ToDouble(d));
    [Primitive("$flcosh", Flags = PrimitiveFlags.Unsafe | PrimitiveFlags.Pure)] public static object FlCosh(object d) => Math.Cosh(ToDouble(d));
    [Primitive("$fltanh", Flags = PrimitiveFlags.Unsafe | PrimitiveFlags.Pure)] public static object FlTanh(object d) => Math.Tanh(ToDouble(d));
    [Primitive("$fllog", Flags = PrimitiveFlags.Unsafe | PrimitiveFlags.Pure)] public static object FlLog(object d) => Math.Log(ToDouble(d));
    [Primitive("$fllog2", Flags = PrimitiveFlags.Unsafe | PrimitiveFlags.Pure)] public static object FlLog2(object d, object b) => Math.Log(ToDouble(d), ToDouble(b));
    [Primitive("$flsqrt", Flags = PrimitiveFlags.Unsafe | PrimitiveFlags.Pure)] public static object FlSqrt(object d) => Math.Sqrt(ToDouble(d));
    [Primitive("$flexp", Flags = PrimitiveFlags.Unsafe | PrimitiveFlags.Pure)] public static object FlExp(object d) => Math.Exp(ToDouble(d));
    [Primitive("$flexpt", Flags = PrimitiveFlags.Unsafe | PrimitiveFlags.Pure)] public static object FlExpt(object d, object n) => Math.Pow(ToDouble(d), ToDouble(n));
    [Primitive("$flceiling", Flags = PrimitiveFlags.Unsafe | PrimitiveFlags.Pure)] public static object FlCeiling(object d) => Math.Ceiling(ToDouble(d));
    [Primitive("$flfloor", Flags = PrimitiveFlags.Unsafe | PrimitiveFlags.Pure)] public static object FlFloor(object d) => Math.Floor(ToDouble(d));
    [Primitive("$fltruncate", Flags = PrimitiveFlags.Unsafe | PrimitiveFlags.Pure)] public static object FlTruncate(object d) => Math.Truncate(ToDouble(d));
    [Primitive("$flround", Flags = PrimitiveFlags.Unsafe | PrimitiveFlags.Pure)] public static object FlRound(object d) => Math.Round(ToDouble(d));
    [Primitive("$flabs", Flags = PrimitiveFlags.Unsafe | PrimitiveFlags.Pure)] public static object FlAbs(object d) => Math.Abs(ToDouble(d));

    private static Complex64 ToComplex(object o) => o is Complex64 c ? c : Complex64.Make(ToDouble(o), 0);

    [Primitive("$complex-exp", Flags = PrimitiveFlags.Unsafe | PrimitiveFlags.Pure)] public static object ComplexExp(object z) => Complex64.Exp(ToComplex(z));
    [Primitive("$complex-sin", Flags = PrimitiveFlags.Unsafe | PrimitiveFlags.Pure)] public static object ComplexSin(object z) => Complex64.Sin(ToComplex(z));
    [Primitive("$complex-cos", Flags = PrimitiveFlags.Unsafe | PrimitiveFlags.Pure)] public static object ComplexCos(object z) => Complex64.Cos(ToComplex(z));
    [Primitive("$complex-tan", Flags = PrimitiveFlags.Unsafe | PrimitiveFlags.Pure)] public static object ComplexTan(object z) => Complex64.Tan(ToComplex(z));
    [Primitive("$complex-asin", Flags = PrimitiveFlags.Unsafe | PrimitiveFlags.Pure)] public static object ComplexAsin(object z) => Complex64.Asin(ToComplex(z));
    [Primitive("$complex-acos", Flags = PrimitiveFlags.Unsafe | PrimitiveFlags.Pure)] public static object ComplexAcos(object z) => Complex64.Acos(ToComplex(z));
    [Primitive("$complex-atan", Flags = PrimitiveFlags.Unsafe | PrimitiveFlags.Pure)] public static object ComplexAtan(object z) => Complex64.Atan(ToComplex(z));
    [Primitive("$complex-sinh", Flags = PrimitiveFlags.Unsafe | PrimitiveFlags.Pure)] public static object ComplexSinh(object z) => Complex64.Sinh(ToComplex(z));
    [Primitive("$complex-cosh", Flags = PrimitiveFlags.Unsafe | PrimitiveFlags.Pure)] public static object ComplexCosh(object z) => Complex64.Cosh(ToComplex(z));
    [Primitive("$complex-tanh", Flags = PrimitiveFlags.Unsafe | PrimitiveFlags.Pure)] public static object ComplexTanh(object z) => Complex64.Tanh(ToComplex(z));
    [Primitive("$complex-log", Flags = PrimitiveFlags.Unsafe | PrimitiveFlags.Pure)] public static object ComplexLog(object z) => Complex64.Log(ToComplex(z));
    [Primitive("$complex-sqrt", Flags = PrimitiveFlags.Unsafe | PrimitiveFlags.Pure)] public static object ComplexSqrt(object z) => Complex64.Sqrt(ToComplex(z));
    [Primitive("$complex-expt", Flags = PrimitiveFlags.Unsafe | PrimitiveFlags.Pure)] public static object ComplexExpt(object z, object w) => Complex64.Pow(ToComplex(z), ToComplex(w));

    #endregion

    #region 记录与过程（boot/records、misc.sls）

    [Primitive("$rtd?", Flags = PrimitiveFlags.Unsafe | PrimitiveFlags.Pure)]
    public static object IsRtd(object o) => RT.Bool(o is RecordTypeDescriptor);

    [Primitive("$rcd?", Flags = PrimitiveFlags.Unsafe | PrimitiveFlags.Pure)]
    public static object IsRcd(object o) => RT.Bool(o is RecordConstructorDescriptor);

    [Primitive("$rtd-name", Flags = PrimitiveFlags.Unsafe | PrimitiveFlags.Pure)]
    public static object RtdName(object rtd) => Symbol.Intern(((RecordTypeDescriptor)rtd).Name);

    [Primitive("$rtd-parent", Flags = PrimitiveFlags.Unsafe | PrimitiveFlags.Pure)]
    public static object RtdParent(object rtd) => (object)((RecordTypeDescriptor)rtd).Parent ?? RT.False;

    [Primitive("$rtd-uid", Flags = PrimitiveFlags.Unsafe | PrimitiveFlags.Pure)]
    public static object RtdUid(object rtd) => ((RecordTypeDescriptor)rtd).uid;

    [Primitive("$rtd-generative?", Flags = PrimitiveFlags.Unsafe | PrimitiveFlags.Pure)]
    public static object IsRtdGenerative(object rtd) => ((RecordTypeDescriptor)rtd).Generative;

    [Primitive("$rtd-sealed?", Flags = PrimitiveFlags.Unsafe | PrimitiveFlags.Pure)]
    public static object IsRtdSealed(object rtd) => ((RecordTypeDescriptor)rtd).Sealed;

    [Primitive("$rtd-opaque?", Flags = PrimitiveFlags.Unsafe | PrimitiveFlags.Pure)]
    public static object IsRtdOpaque(object rtd) => ((RecordTypeDescriptor)rtd).Opaque;

    /// <summary>字段名的向量（符号）。</summary>
    [Primitive("$rtd-field-names", Flags = PrimitiveFlags.Unsafe | PrimitiveFlags.Pure)]
    public static object RtdFieldNames(object rtd)
    {
        var fields = ((RecordTypeDescriptor)rtd).Fields;
        var r = new object[fields.Length];
        for (int i = 0; i < fields.Length; i++) r[i] = Symbol.Intern(fields[i].Name);
        return r;
    }

    [Primitive("$rtd-field-mutable?", Flags = PrimitiveFlags.Unsafe | PrimitiveFlags.Pure)]
    public static object IsRtdFieldMutable(object rtd, object k) => ((RecordTypeDescriptor)rtd).Fields[(int)k].Mutable;

    [Primitive("$procedure-arity", Flags = PrimitiveFlags.Unsafe | PrimitiveFlags.Pure)]
    public static object ProcedureArity(object p) => ((Procedure)p).Arity;

    [Primitive("$procedure-name", Flags = PrimitiveFlags.Unsafe | PrimitiveFlags.Pure)]
    public static object ProcedureName(object p) => ((Procedure)p).Name is string s ? Symbol.Intern(s) : RT.False;

    [Primitive("$procedure-form", Flags = PrimitiveFlags.Unsafe | PrimitiveFlags.Pure)]
    public static object ProcedureForm(object p) => ((Procedure)p).Form;

    #endregion

    #region 文件与路径（boot/files.sls、expander）

    [Primitive("$file-exists?", Flags = PrimitiveFlags.Unsafe)]
    public static object FileExists(object name) => RT.Bool(File.Exists(name.ToString()));

    [Primitive("$delete-file", Flags = PrimitiveFlags.Unsafe)]
    public static object DeleteFile(object name)
    {
        File.Delete(name.ToString());
        return Unspecified;
    }

    /// <summary>文件的最后修改时间：宿主的时间对象（只用于比较）。</summary>
    [Primitive("$file-last-write-time", Flags = PrimitiveFlags.Unsafe)]
    public static object FileLastWriteTime(object name) => File.GetLastWriteTime(name.ToString());

    /// <summary>文件的最后修改时间，精确整数（100 ns 刻度）。</summary>
    [Primitive("$file-mtime", Flags = PrimitiveFlags.Unsafe)]
    public static object FileMtime(object name) => Int(File.GetLastWriteTime(name.ToString()).Ticks);

    [Primitive("$compare-time", Flags = PrimitiveFlags.Unsafe | PrimitiveFlags.Pure)]
    public static object CompareTime(object a, object b) => RT.Box(((IComparable)a).CompareTo(b));

    private static object Str(string s) => SchemeString.Normalize(s);

    [Primitive("$path-directory-name", Flags = PrimitiveFlags.Unsafe | PrimitiveFlags.Pure)]
    public static object PathDirectoryName(object p) => Path.GetDirectoryName(p.ToString()) is string s ? Str(s) : RT.False;

    [Primitive("$path-file-name", Flags = PrimitiveFlags.Unsafe | PrimitiveFlags.Pure)]
    public static object PathFileName(object p) => Str(Path.GetFileName(p.ToString()));

    [Primitive("$path-extension", Flags = PrimitiveFlags.Unsafe | PrimitiveFlags.Pure)]
    public static object PathExtension(object p) => Str(Path.GetExtension(p.ToString()));

    [Primitive("$path-change-extension", Flags = PrimitiveFlags.Unsafe | PrimitiveFlags.Pure)]
    public static object PathChangeExtension(object p, object ext) => Str(Path.ChangeExtension(p.ToString(), ext.ToString()));

    [Primitive("$path-combine", Flags = PrimitiveFlags.Unsafe | PrimitiveFlags.Pure)]
    public static object PathCombine(object a, object b) => Str(Path.Combine(a.ToString(), b.ToString()));

    [Primitive("$path-full", Flags = PrimitiveFlags.Unsafe)]
    public static object PathFull(object p) => Str(Path.GetFullPath(p.ToString()));

    [Primitive("$path-trim-separator", Flags = PrimitiveFlags.Unsafe | PrimitiveFlags.Pure)]
    public static object PathTrimSeparator(object p) => Str(Path.TrimEndingDirectorySeparator(p.ToString()));

    [Primitive("$directory-exists?", Flags = PrimitiveFlags.Unsafe)]
    public static object DirectoryExists(object p) => RT.Bool(Directory.Exists(p.ToString()));

    [Primitive("$directory-create", Flags = PrimitiveFlags.Unsafe)]
    public static object DirectoryCreate(object p)
    {
        Directory.CreateDirectory(p.ToString());
        return Unspecified;
    }

    /// <summary>目录下（含子目录）匹配模式的全部文件的完整路径列表。</summary>
    [Primitive("$directory-files", Flags = PrimitiveFlags.Unsafe)]
    public static object DirectoryFiles(object dir, object pattern)
    {
        var files = Directory.GetFiles(dir.ToString(), pattern.ToString(), SearchOption.AllDirectories);
        var r = new object[files.Length];
        for (int i = 0; i < files.Length; i++) r[i] = Str(files[i]);
        return Cons.FromArray(r);
    }

    [Primitive("$application-directory", Flags = PrimitiveFlags.Unsafe)]
    public static object ApplicationDirectory() => Builtins.ApplicationDirectory;

    [Primitive("$current-directory", Flags = PrimitiveFlags.Unsafe)]
    public static object CurrentDirectory() => Str(Environment.CurrentDirectory);

    [Primitive("$set-current-directory!", Flags = PrimitiveFlags.Unsafe)]
    public static object SetCurrentDirectory(object p)
    {
        Environment.CurrentDirectory = p.ToString();
        return Unspecified;
    }

    /// <summary>控制台前景色：符号（ConsoleColor 的名字），不是终端时为 #f。</summary>
    [Primitive("$console-foreground-color", Flags = PrimitiveFlags.Unsafe)]
    public static object ConsoleForegroundColor() => Symbol.Intern(Console.ForegroundColor.ToString());

    [Primitive("$set-console-foreground-color!", Flags = PrimitiveFlags.Unsafe)]
    public static object SetConsoleForegroundColor(object color)
    {
        Console.ForegroundColor = (ConsoleColor)Enum.Parse(typeof(ConsoleColor), ((Symbol)color).Name, true);
        return Unspecified;
    }

    #endregion

    #region 线程、时间与区域（(cleat scheme std …)）

    [Primitive("$make-semaphore", Flags = PrimitiveFlags.Unsafe)]
    public static object MakeSemaphore(object initial) => new SemaphoreSlim((int)initial);

    [Primitive("$semaphore-release!", Flags = PrimitiveFlags.Unsafe)]
    public static object SemaphoreRelease(object s)
    {
        ((SemaphoreSlim)s).Release();
        return Unspecified;
    }

    [Primitive("$semaphore-wait!", Flags = PrimitiveFlags.Unsafe)]
    public static object SemaphoreWait(object s)
    {
        ((SemaphoreSlim)s).Wait();
        return Unspecified;
    }

    /// <summary>在后台线程上运行 thunk（不阻止进程退出）。</summary>
    [Primitive("$start-thread", Flags = PrimitiveFlags.Unsafe)]
    public static object StartThread(object thunk)
    {
        var p = (Procedure)thunk;
        var t = new Thread(() => p.Invoke0()) { IsBackground = true };
        t.Start();
        return t;
    }

    /// <summary>本地时区当前相对 UTC 的偏移（秒）。</summary>
    [Primitive("$timezone-offset-seconds", Flags = PrimitiveFlags.Unsafe)]
    public static object TimezoneOffsetSeconds() => Int((long)Math.Round(TimeZoneInfo.Local.GetUtcOffset(DateTime.Now).TotalSeconds));

    /// <summary>当前区域的名字（不变区域为空串）。</summary>
    [Primitive("$culture-name", Flags = PrimitiveFlags.Unsafe)]
    public static object CultureName() => Str(CultureInfo.CurrentCulture.Name);

    [Primitive("$culture-language", Flags = PrimitiveFlags.Unsafe)]
    public static object CultureLanguage() => Str(CultureInfo.CurrentCulture.TwoLetterISOLanguageName);

    /// <summary>当前区域的两字母国家码（不变区域没有国家时为 #f）。</summary>
    [Primitive("$culture-country", Flags = PrimitiveFlags.Unsafe)]
    public static object CultureCountry()
    {
        var name = CultureInfo.CurrentCulture.Name;
        if (string.IsNullOrEmpty(name)) return RT.False;
        try { return Str(new RegionInfo(name).TwoLetterISORegionName); }
        catch (ArgumentException) { return RT.False; }
    }

    #endregion
}
