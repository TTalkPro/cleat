/**
 *   Copyright (c) Rich Hickey. All rights reserved.
 *   The use and distribution terms for this software are covered by the
 *   Eclipse Public License 1.0 (http://opensource.org/licenses/eclipse-1.0.php)
 *   which can be found in the file epl-v10.html at the root of this distribution.
 *   By using this software in any fashion, you are agreeing to be bound by
 * 	 the terms of this license.
 *   You must not remove this notice, or any other, from this software.
 **/


using System;
using System.Runtime.Serialization;
using System.Security.Permissions;

namespace clojure.lang
{
    /// <summary>
    /// Represents a keyword
    /// </summary>
    [Serializable]
    public sealed class Keyword: IFn, Named, IComparable, ISerializable, IHashEq
    {

        // Originally, ClojureJVM had this implementing IFn, while I had it based on AFn.
        // I changed it to match when updating to fix for CLJ-2350 (commit bd4c42d, 2021.09.14) in order to get consistency in arity error messages.
        // Sigh.


        #region Data

        /// <summary>
        /// The symbol giving the namespace/name (without :) of the keyword.
        /// </summary>
        private readonly Symbol _sym;

        /// <summary>
        /// Caches the hasheq value for the keyword.
        /// </summary>
        readonly int _hasheq;

        /// <summary>
        /// Map from symbol to keyword to uniquify keywords.
        /// </summary>
        /// <remarks>Why introduce the JavaConcurrentDictionary?  
        /// We really only need a synchronized hash table with one operation: PutIfAbsent.</remarks>
        private static readonly JavaConcurrentDictionary<Symbol,WeakReference> _symKeyMap
            = new JavaConcurrentDictionary<Symbol, WeakReference>();

        // cleat-146：热路径先持有 ns / name 字符串，旧实现要先分配 Symbol 才能查表。
        // 用字符串键先行查找，命中时完全不再分配；组合键是 struct，查找本身也不装箱。
        private readonly struct StringKey(string ns, string name)
        {
            public string Ns { get; } = ns;
            public string Name { get; } = name;
        }

        // 两表必须在同一把锁下更新：字符串表负责热路径命中，_symKeyMap 仍是 intern(Symbol) /
        // All() / 反序列化的权威表；分开加锁会让弱引用清理出现一表新一表旧。
        private static readonly object _keyMapSync = new();
        private static readonly JavaConcurrentDictionary<StringKey, WeakReference> _stringKeyMap
            = new JavaConcurrentDictionary<StringKey, WeakReference>();

        // cleat-146：intern(nsname) 命中时不做 Substring。规范化 ns/name 之外再留一个原文键；
        // 只在第一次见到这个原文时登记，命中路径只做一次字符串表查找。
        private static readonly JavaConcurrentDictionary<string, WeakReference> _fullNameKeyMap
            = new JavaConcurrentDictionary<string, WeakReference>();

        // cleat-146：CLEAT_NO_OPT=intern 完整退回“先建 Symbol 再查表”的旧路径。
        private static readonly bool s_internEnabled =
            !(Environment.GetEnvironmentVariable("CLEAT_NO_OPT") ?? "").Contains("intern");

        public Symbol Symbol
        {
          get { return _sym; }
        }

        // cache ToString if called
        [NonSerialized]
        string _str;

        #endregion

        #region C-tors and factory methods

        /// <summary>
        /// Create (or find existing) keyword with given symbol's namespace/name.
        /// </summary>
        /// <param name="sym">The symbol giving the keyword's namespace/name.</param>
        /// <returns>A keyword</returns>
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Style", "IDE1006:Naming Styles", Justification = "ClojureJVM name match")]
        public static Keyword intern(Symbol sym)
        {
            // cleat-146：Symbol 输入（含带元数据的 symbol）仍走这条慢路径。这是有意保留现状：
            // Symbol 不做唯一化，带元数据副本也不能当作身份可丢弃的新键；只有 ns/name 字符串入口
            // 才能安全地先查字符串表。字符串命中时同样不会新建 Symbol，身份/元数据语义不变。
            lock (_keyMapSync)
            {
                Keyword k = null;
                WeakReference existingRef = _symKeyMap.Get(sym);
                if (existingRef == null)
                {
                    if (sym.meta() != null)
                        sym = (Symbol)sym.withMeta(null);
                    k = new Keyword(sym);

                    WeakReference wr = new WeakReference(k)
                    {
                        Target = k
                    };
                    existingRef = _symKeyMap.PutIfAbsent(sym, wr);
                    // 同一关键字可能刚由字符串路径登记过；只有真正成为胜者时才补字符串表，
                    // 并用胜者的 Symbol 作为键，避免元数据副本进入两张表。
                    if (existingRef == null)
                        _stringKeyMap.PutIfAbsent(new StringKey(sym.Namespace, sym.Name), wr);
                }
                if (existingRef == null)
                    return k;
                Keyword existingk = (Keyword)existingRef.Target;
                if (existingk != null)
                    return existingk;
                // entry died in the interim, do over
                // let's not get confused, remove it.  (else infinite loop).
                RemoveDiedEntry(sym, existingRef);
                return intern(sym);
            }
        }

        /// <summary>
        /// Create (or find existing) keyword with given namespace/name.
        /// </summary>
        /// <param name="ns">The keyword's namespace name.</param>
        /// <param name="name">The keyword's name.</param>
        /// <returns>A keyword</returns>
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Style", "IDE1006:Naming Styles", Justification = "ClojureJVM name match")]
        public static Keyword intern(string ns, string name)
        {
            // cleat-146：开关保留先建 Symbol 的旧路径，便于优化收益和语义做同窗口对照。
            if (!s_internEnabled)
                return intern(Symbol.intern(ns, name));

            var key = new StringKey(ns, name);
            lock (_keyMapSync)
            {
                var existing = FindStringEntry(key);
                if (existing != null)
                    return existing;

                var sym = Symbol.intern(ns, name);
                var k = new Keyword(sym);
                var wr = new WeakReference(k)
                {
                    Target = k
                };
                _stringKeyMap.PutIfAbsent(key, wr);
                _symKeyMap.PutIfAbsent(sym, wr);
                return k;
            }
        }

        /// <summary>读字符串表并顺带清理两端都可见的死项：弱引用目标被 GC 后不能留下假命中。</summary>
        private static Keyword FindStringEntry(StringKey key)
        {
            var wr = _stringKeyMap.Get(key);
            if (wr == null)
                return null;
            var k = (Keyword)wr.Target;
            if (k != null)
                return k;

            // 只移除当前死引用，避免并发复活时误删新关键字；sym 分配只发生在罕见清理路径。
            if (ReferenceEquals(_stringKeyMap.Remove(key), wr))
                _symKeyMap.Remove(Symbol.intern(key.Ns, key.Name));
            // 原文缓存也可能仍持有同一个死引用；拼键只在清理路径发生。
            var fullName = key.Ns == null ? key.Name : key.Ns + "/" + key.Name;
            _fullNameKeyMap.Remove(fullName);
            return null;
        }

        private static void RemoveDiedEntry(Symbol sym, WeakReference diedRef)
        {
            if (ReferenceEquals(_symKeyMap.Remove(sym), diedRef))
            {
                var key = new StringKey(sym.Namespace, sym.Name);
                _stringKeyMap.Remove(key);
                // intern(nsname) 的原文缓存同样不能留下死引用。
                _fullNameKeyMap.Remove(sym.Namespace == null ? sym.Name : sym.Namespace + "/" + sym.Name);
            }
        }

        /// <summary>
        /// Create (or find existing) keyword with the given name.
        /// </summary>
        /// <param name="nsname">The keyword's name</param>
        /// <returns>A keyword</returns>
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Style", "IDE1006:Naming Styles", Justification = "ClojureJVM name match")]
        /// <summary>所有仍存活的关键字（REPL 的 Tab 补全用，cleat-034）。顺序不确定。</summary>
        public static System.Collections.Generic.IEnumerable<Keyword> All()
        {
            WeakReference[] snapshot;
            lock (_keyMapSync)
                snapshot = _symKeyMap.Values;
            // Values 已复制快照；枚举放到锁外，调用方慢慢消费也不会阻塞 intern。
            foreach (var wr in snapshot)
                if (wr?.Target is Keyword k) yield return k;
        }

        public static Keyword intern(String nsname)
        {
            // cleat-146：原文键先命中，避免为切出 ns / name 分配 Substring；未命中才按旧规则切分。
            if (s_internEnabled)
            {
                lock (_keyMapSync)
                {
                    var wr = _fullNameKeyMap.Get(nsname);
                    if (wr != null && (Keyword)wr.Target is Keyword hit)
                        return hit;
                    if (wr != null)
                        _fullNameKeyMap.Remove(nsname);

                    int slash = nsname.IndexOf('/');
                    var k = slash == -1 || nsname.Equals("/")
                        ? intern(null, nsname)
                        : intern(nsname.Substring(0, slash), nsname.Substring(slash + 1));
                    if (wr == null)
                    {
                        wr = _symKeyMap.Get(k.Symbol);
                        if (wr != null)
                            _fullNameKeyMap.PutIfAbsent(nsname, wr);
                    }
                    return k;
                }
            }
            return intern(Symbol.intern(nsname));
        }

        /// <summary>
        /// Construct a keyword based on a symbol.
        /// </summary>
        /// <param name="sym">A symbol giving namespace/name.</param>
        private Keyword(Symbol sym)
        {
            _sym = sym;
            _hasheq = (int)(_sym.hasheq() + 0x9e3779b9);
        }


        #endregion

        #region Object overrides

        /// <summary>
        /// Returns a string representing the keyword.
        /// </summary>
        /// <returns>A string representing the keyword.</returns>
        public override string ToString()
        {
            if (_str == null)
                _str = ":" + _sym.ToString();
            return _str;
        }

        /// <summary>
        /// Determines if an object is equal to this keyword.  Value semantics.
        /// </summary>
        /// <param name="obj">The object to compare to.</param>
        /// <returns><value>true</value> if equal; <value>false</value> otherwise.</returns>
        public override bool Equals(object obj)
        {
            if ( ReferenceEquals(this,obj) ) 
                return true;

            return obj is Keyword keyword && _sym.Equals(keyword.Symbol);
        }

        /// <summary>
        /// Gets a hash code for the keyword.
        /// </summary>
        /// <returns>A hash code.</returns>
        public override int GetHashCode()
        {
            return _hasheq;
            //return (int)(_sym.GetHashCode() + 0x9e3779b9);
        }

        #endregion

        #region Named Members

        //  I prefer to use the following internally.

        /// <summary>
        /// The namespace name.
        /// </summary>
        public string Namespace
        {
            get { return _sym.Namespace; }
        }

        /// <summary>
        /// The name.
        /// </summary>
        public string Name
        {
            get { return _sym.Name; }
        }

        // the following are in the interface


        /// <summary>
        /// Gets the namespace name.
        /// </summary>
        /// <returns>The namespace name.</returns>
        public string getNamespace()
        {
            return _sym.Namespace;
        }

        /// <summary>
        /// Gets the name.
        /// </summary>
        /// <returns>The name.</returns>
        public string getName()
        {
            return _sym.Name;
        }

        #endregion

        #region IFn Members

        Object ThrowArity(int n)
        {
            throw new ArityException(n, ToString());
        }

        /// <summary>
        /// (:keyword arg)  => (get arg :keyword)
        /// </summary>
        /// <param name="arg1">The object to access.</param>
        /// <returns>The value mapped to the keyword.</returns>
        public object invoke(object arg1)
        {
            if (arg1 is ILookup ilu)
                return ilu.valAt(this);

            return RT.get(arg1, this);
        }


        /// <summary>
        /// (:keyword arg default) => (get arg :keyword default)
        /// </summary>
        /// <param name="arg1">The object to access.</param>
        /// <param name="arg2">Default value if not found.</param>
        /// <returns></returns>

        public object invoke(object arg1, object notFound)
        {
            if (arg1 is ILookup ilu)
                return ilu.valAt(this, notFound);

            return RT.get(arg1, this, notFound);
        }


        public object invoke()
        {
            return ThrowArity(0);
        }

        public object invoke(object arg1, object arg2, object arg3)
        {
            return ThrowArity(3);
        }

        public object invoke(object arg1, object arg2, object arg3, object arg4)
        {
            return ThrowArity(4);
        }

        public object invoke(object arg1, object arg2, object arg3, object arg4, object arg5)
        {
            return ThrowArity(5);
        }

        public object invoke(object arg1, object arg2, object arg3, object arg4, object arg5,
            object arg6)
        {
            return ThrowArity(6);
        }

        public object invoke(object arg1, object arg2, object arg3, object arg4, object arg5,
            object arg6, object arg7)
        {
            return ThrowArity(7);
        }

        public object invoke(object arg1, object arg2, object arg3, object arg4, object arg5,
            object arg6, object arg7, object arg8)
        {
            return ThrowArity(8);
        }

        public object invoke(object arg1, object arg2, object arg3, object arg4, object arg5,
            object arg6, object arg7, object arg8, object arg9)
        {
            return ThrowArity(9);
        }

        public object invoke(object arg1, object arg2, object arg3, object arg4, object arg5,
            object arg6, object arg7, object arg8, object arg9, object arg10)
        {
            return ThrowArity(10);
        }

        public object invoke(object arg1, object arg2, object arg3, object arg4, object arg5,
            object arg6, object arg7, object arg8, object arg9, object arg10, object arg11)
        {
            return ThrowArity(11);
        }

        public object invoke(object arg1, object arg2, object arg3, object arg4, object arg5,
            object arg6, object arg7, object arg8, object arg9, object arg10, object arg11,
            object arg12)
        {
            return ThrowArity(12);
        }

        public object invoke(object arg1, object arg2, object arg3, object arg4, object arg5,
            object arg6, object arg7, object arg8, object arg9, object arg10, object arg11,
            object arg12, object arg13)
        {
            return ThrowArity(13);
        }

        public object invoke(object arg1, object arg2, object arg3, object arg4, object arg5,
            object arg6, object arg7, object arg8, object arg9, object arg10, object arg11,
            object arg12, object arg13, object arg14)
        {
            return ThrowArity(14);
        }

        public object invoke(object arg1, object arg2, object arg3, object arg4, object arg5,
            object arg6, object arg7, object arg8, object arg9, object arg10, object arg11,
            object arg12, object arg13, object arg14, object arg15)
        {
            return ThrowArity(15);
        }

        public object invoke(object arg1, object arg2, object arg3, object arg4, object arg5,
            object arg6, object arg7, object arg8, object arg9, object arg10, object arg11,
            object arg12, object arg13, object arg14, object arg15, object arg16)
        {
            return ThrowArity(16);
        }

        public object invoke(object arg1, object arg2, object arg3, object arg4, object arg5,
            object arg6, object arg7, object arg8, object arg9, object arg10, object arg11,
            object arg12, object arg13, object arg14, object arg15, object arg16, object arg17)
        {
            return ThrowArity(17);
        }

        public object invoke(object arg1, object arg2, object arg3, object arg4, object arg5,
            object arg6, object arg7, object arg8, object arg9, object arg10, object arg11,
            object arg12, object arg13, object arg14, object arg15, object arg16, object arg17,
            object arg18)
        {
            return ThrowArity(18);
        }

        public object invoke(object arg1, object arg2, object arg3, object arg4, object arg5,
            object arg6, object arg7, object arg8, object arg9, object arg10, object arg11,
            object arg12, object arg13, object arg14, object arg15, object arg16, object arg17,
            object arg18, object arg19)
        {
            return ThrowArity(19);
        }

        public object invoke(object arg1, object arg2, object arg3, object arg4, object arg5,
            object arg6, object arg7, object arg8, object arg9, object arg10, object arg11,
            object arg12, object arg13, object arg14, object arg15, object arg16, object arg17,
            object arg18, object arg19, object arg20)
        {
            return ThrowArity(20);
        }

        public object invoke(object arg1, object arg2, object arg3, object arg4, object arg5,
            object arg6, object arg7, object arg8, object arg9, object arg10, object arg11,
            object arg12, object arg13, object arg14, object arg15, object arg16, object arg17,
            object arg18, object arg19, object arg20, params object[] args)
        {
            return ThrowArity(20 + args.Length);
        }

        public Object applyTo(ISeq arglist)
        {
            return AFn.ApplyToHelper(this, arglist);
        }

        #endregion

        #region IComparable Members

        /// <summary>
        /// Compare this to another object.
        /// </summary>
        /// <param name="obj">The object to compare to.</param>
        /// <returns>neg,zero,pos for &lt; = &gt;</returns>
        public int CompareTo(object obj)
        {
            return obj is Keyword k
                ? _sym.CompareTo(k._sym)
                : throw new ArgumentException("Cannot compare to null or non-Keyword", nameof(obj));
        }

        #endregion

        #region Operator overloads

        public static bool operator ==(Keyword k1, Keyword k2)
        {
            if (ReferenceEquals(k1, k2))
                return true;

            if (k1 is null || k2 is null)
                return false;

            return k1.CompareTo(k2) == 0;
        }

        public static bool operator !=(Keyword k1, Keyword k2)
        {
            if (ReferenceEquals(k1, k2))
                return false;

            if (k1 is null || k2 is null)
                return true;

            return k1.CompareTo(k2) != 0;
        }

        public static bool operator <(Keyword k1, Keyword k2)
        {
            if (ReferenceEquals(k1, k2))
                return false;

            if (k1 is null)
                throw new ArgumentNullException(nameof(k1));

            return k1.CompareTo(k2) < 0;
        }

        public static bool operator >(Keyword k1, Keyword k2)
        {
            if (ReferenceEquals(k1, k2))
                return false;

            if (k1 is null)
                throw new ArgumentNullException(nameof(k1));

            return k1.CompareTo(k2) > 0;
        }

        #endregion

        #region ISerializable Members

        [System.Security.SecurityCritical]
        public void GetObjectData(SerializationInfo info, StreamingContext context)
        {
            // Instead of serializing the keyword,
            // serialize a KeywordSerializationHelper instead
            info.SetType(typeof(KeywordSerializationHelper));
            info.AddValue("_sym", _sym);
        }

        #endregion

        #region other

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Style", "IDE1006:Naming Styles", Justification = "ClojureJVM name match")]
        public static Keyword find(Symbol sym)
        {
            lock (_keyMapSync)
            {
                WeakReference wr = _symKeyMap.Get(sym);
                if (wr != null)
                    return (Keyword)wr.Target;
                else
                    return null;
            }
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Style", "IDE1006:Naming Styles", Justification = "ClojureJVM name match")]
        public static Keyword find(String ns, String name)
        {
            // cleat-146：find 不建立新关键字，所以字符串键查找在这里尤其重要。
            if (s_internEnabled)
            {
                lock (_keyMapSync)
                    return FindStringEntry(new StringKey(ns, name));
            }
            return find(Symbol.intern(ns, name));
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Style", "IDE1006:Naming Styles", Justification = "ClojureJVM name match")]
        public static Keyword find(String nsname)
        {
            if (s_internEnabled)
            {
                lock (_keyMapSync)
                {
                    var wr = _fullNameKeyMap.Get(nsname);
                    if (wr != null)
                    {
                        var k = (Keyword)wr.Target;
                        if (k != null)
                            return k;
                        _fullNameKeyMap.Remove(nsname);
                    }

                    int slash = nsname.IndexOf('/');
                    return slash == -1 || nsname.Equals("/")
                        ? find(null, nsname)
                        : find(nsname.Substring(0, slash), nsname.Substring(slash + 1));
                }
            }
            return find(Symbol.intern(nsname));
        }

        #endregion

        #region IHashEq members

        public int hasheq()
        {
            return _hasheq;
        }

        #endregion
    }

    [Serializable]
    sealed class KeywordSerializationHelper : IObjectReference
    {

        #region Data

        readonly Symbol _sym;

        #endregion

        #region c-tors

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Style", "IDE0060:Remove unused parameter", Justification = "Standard API")]
        [System.Diagnostics.CodeAnalysis.SuppressMessage("CodeQuality", "IDE0051:Remove unused private members", Justification = "Standard API")]
        KeywordSerializationHelper(SerializationInfo info, StreamingContext context)
        {
            _sym = (Symbol)info.GetValue("_sym", typeof(Symbol));
        }

        #endregion

        #region IObjectReference Members

        public object GetRealObject(StreamingContext context)
        {
            return Keyword.intern(_sym);
        }

        #endregion
    }
}
