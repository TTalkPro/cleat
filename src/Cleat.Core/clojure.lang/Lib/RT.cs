/**
 *   Copyright (c) Rich Hickey. All rights reserved.
 *   The use and distribution terms for this software are covered by the
 *   Eclipse Public License 1.0 (http://opensource.org/licenses/eclipse-1.0.php)
 *   which can be found in the file epl-v10.html at the root of this distribution.
 *   By using this software in any fashion, you are agreeing to be bound by
 * 	 the terms of this license.
 *   You must not remove this notice, or any other, from this software.
 **/

// CLEAT-005: removed usings clojure.lang.CljCompiler.Context (compiler), clojure.lang.Runtime (DLR ClojureContext),
// clojure.lang.TypeName2 (ClrTypeSpec2, interop group), Microsoft.Scripting.Hosting (DLR)
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using RTProperties = clojure.runtime.Properties;


namespace clojure.lang
{
#pragma warning disable IDE1006 // Naming Styles 
    public static class RT
    {
        #region Default symbol-to-class map

        //simple-symbol->class
        public static readonly IPersistentMap DefaultImports = PersistentHashMap.create((IDictionary)CreateDefaultImportDictionary());

        static Dictionary<Symbol, Type> CreateDefaultImportDictionary()
        {
            var q = GetAllTypesInNamespace("System");
            // Handle duplicates by taking the first occurrence of each type name
            // This is necessary because in .NET Core/5+ the same type name can appear
            // in multiple assemblies (e.g., "Casing" appears in both System.Text.Json 
            // and System.Private.CoreLib)
            var d = new Dictionary<Symbol, Type>();
            foreach (var type in q)
            {
                var symbol = Symbol.intern(type.Name);
                if (!d.ContainsKey(symbol))
                {
                    d.Add(symbol, type);
                }
            }

            // ADDED THESE TO SUPPORT THE BOOTSTRAPPING IN THE JAVA CORE.CLJ
            d.Add(Symbol.intern("StringBuilder"), typeof(StringBuilder));
            d.Add(Symbol.intern("BigInteger"), typeof(clojure.lang.BigInteger));
            d.Add(Symbol.intern("BigDecimal"), typeof(clojure.lang.BigDecimal));

            // Per CLJ-2915, 2026-09-29
            d.Add(Symbol.intern("ExceptionInfo"), typeof(clojure.lang.ExceptionInfo));

            // CLEAT-032: JVM exception class names as implicit imports, mapped to the CLR types Cleat.Core throws
            Cleat.Core.Runtime.JvmNames.AddImports(d);

            return d;
        }

        static IEnumerable<Type> GetAllTypesInNamespace(string nspace)
        {
            static IEnumerable<Type> getTypes(Assembly a)
            {
                try { return a.GetTypes(); } catch (Exception) { return []; }
            }
            var q = AppDomain.CurrentDomain.GetAssemblies()
                       .SelectMany(t => getTypes(t))
                       .Where(t => (t.IsClass || t.IsInterface || t.IsValueType) &&
                                    t.Namespace == nspace &&
                                    (t.IsPublic || t.IsNestedPublic) &&
                                    !t.IsGenericTypeDefinition &&
                                    !t.Name.StartsWith("_", StringComparison.Ordinal) &&
                                    !t.Name.StartsWith("<", StringComparison.Ordinal));

            return q;
        }


        #endregion

        #region Some misc. goodies

        public static readonly object[] EmptyObjectArray = [];

        // cleat-143：String 的 first / seq / next / more 直读快路径。默认开；CLEAT_NO_OPT=strseq 时保留原来的
        // RT.seq → StringSeq 路径（包括装箱方式），使开 / 关成为完整的分配与时间对照。
        private static readonly bool s_strseq = !(Environment.GetEnvironmentVariable("CLEAT_NO_OPT") ?? "").Contains("strseq");

        static readonly RTProperties _versionProperties = [];

        public static RTProperties GetVersionProperties() { return _versionProperties; }

        // Note that we name the environment variable such that it can be
        // directly manipulated as a POSIX shell identifier, which is useful
        // for folks using Cygwin and its ilk.
        public const string ClojureLoadPathString = "CLOJURE_LOAD_PATH";

        static public readonly Object REQUIRE_LOCK = new();

        // cleat-146：str 整数快路径与 Keyword intern 快路径共用 CLEAT_NO_OPT=intern 开关；
        // static readonly 保证热路径只读字段，不重复读环境变量。
        internal static readonly bool s_internEnabled =
            !(Environment.GetEnvironmentVariable("CLEAT_NO_OPT") ?? "").Contains("intern");

        // cleat-147：nth 的 ASeq / LazySeq / StringSeq 快路径。默认开；CLEAT_NO_OPT=nthfast 时
        // NthFromRef 完整回到 NthFrom 的旧判断链，开 / 关是同一份运行时的完整对照。
        private static readonly bool s_nthfast =
            !(Environment.GetEnvironmentVariable("CLEAT_NO_OPT") ?? "").Contains("nthfast");

        #endregion

        #region  It's true (or not)

        // The JVM version has these to provide standard boxed values for true/false.
        // I've tried it in the CLR version, two ways: with the types of the as bool and the types as Object.
        // Very little difference between those.
        // However, getting rid of them entirely speeds up compilation by about 25%.
        // Thus, I'm thinking boxing bools is less expensive than a static field lookup.

        //public static readonly Object T = true;//Keyword.intern(Symbol.intern(null, "t"));
        //public static readonly Object F = false;//Keyword.intern(Symbol.intern(null, "t"));

        public static bool IsTrue(object o)
        {
            if (o == null)
                return false;
            if (o is Boolean boolean)
                return boolean;
            else
                return true;
        }

        #endregion

        #region Predefined namespaces

        // We need this initialization to happen earlier than most of the Var inits.
        public static readonly Namespace ClojureNamespace
            = Namespace.findOrCreate(Symbol.intern("clojure.core"));

        #endregion

        #region Useful Keywords

        public static readonly Keyword TagKey
            = Keyword.intern(null, "tag");

        public static readonly Keyword ParamTagsKey
            = Keyword.intern(null, "param-tags");

        public static readonly Keyword ConstKey
            = Keyword.intern(null, "const");

        public static readonly Keyword EvalFileKey
            = Keyword.intern("clojure.core", "eval-file");

        public static readonly Keyword LineKey
            = Keyword.intern(null, "line");

        public static readonly Keyword ColumnKey
            = Keyword.intern(null, "column");

        public static readonly Keyword FileKey
            = Keyword.intern(null, "file");

        public static readonly Keyword SourceSpanKey
            = Keyword.intern(null, "source-span");

        public static readonly Keyword StartLineKey
            = Keyword.intern(null, "start-line");

        public static readonly Keyword StartColumnKey
            = Keyword.intern(null, "start-column");

        public static readonly Keyword EndLineKey
            = Keyword.intern(null, "end-line");

        public static readonly Keyword EndColumnKey
            = Keyword.intern(null, "end-column");

        public static readonly Keyword DeclaredKey
            = Keyword.intern(null, "declared");

        public static readonly Keyword DocKey
            = Keyword.intern(null, "doc");

        #endregion

        #region Vars (namespace-related)

        public static readonly Var CurrentNSVar
            = Var.intern(ClojureNamespace, Symbol.intern("*ns*"), ClojureNamespace).setDynamic();


        public static readonly Var InNSVar
            //= Var.intern(CLOJURE_NS, Symbol.intern("in-ns"), RT.F);
            = Var.intern(ClojureNamespace, Symbol.intern("in-ns"), false);

        public static readonly Var NSVar
            //= Var.intern(CLOJURE_NS, Symbol.intern("ns"), RT.F);
            = Var.intern(ClojureNamespace, Symbol.intern("ns"), false);

        #endregion

        #region Vars (I/O-related)

        public static readonly Var OutVar
            = Var.intern(ClojureNamespace, Symbol.intern("*out*"), new StreamWriter(System.Console.OpenStandardOutput())).setDynamic();

        public static readonly Var ErrVar
            = Var.intern(ClojureNamespace, Symbol.intern("*err*"), new StreamWriter(System.Console.OpenStandardError())).setDynamic();

        public static readonly Var InVar =
            Var.intern(ClojureNamespace, Symbol.intern("*in*"),
            new clojure.lang.LineNumberingTextReader(new StreamReader(System.Console.OpenStandardInput()))).setDynamic();

        static readonly Var PrintReadablyVar
            //= Var.intern(CLOJURE_NS, Symbol.intern("*print-readably*"), RT.T);
            = Var.intern(ClojureNamespace, Symbol.intern("*print-readably*"), true).setDynamic();

        public static readonly Var PrintMetaVar
            //= Var.intern(CLOJURE_NS, Symbol.intern("*print-meta*"), RT.F);
            = Var.intern(ClojureNamespace, Symbol.intern("*print-meta*"), false).setDynamic();

        public static readonly Var PrintDupVar
            //= Var.intern(CLOJURE_NS, Symbol.intern("*print-dup*"), RT.F);
            = Var.intern(ClojureNamespace, Symbol.intern("*print-dup*"), false).setDynamic();

        // We need this Var initializaed early on, I'm willing to waste the local init overhead to keep the code here with the others.
        static readonly Var FlushOnNewlineVar
            //= Var.intern(CLOJURE_NS, Symbol.intern("*flush-on-newline*"), RT.T);
            = Var.intern(ClojureNamespace, Symbol.intern("*flush-on-newline*"), true).setDynamic();

        static readonly Var PrintInitializedVar
            = Var.intern(ClojureNamespace, Symbol.intern("print-initialized")).setDynamic();

        static readonly Var PrOnVar
            = Var.intern(ClojureNamespace, Symbol.intern("pr-on"));

        public static readonly Var AllowSymbolEscapeVar
            = Var.intern(ClojureNamespace, Symbol.intern("*allow-symbol-escape*"), true).setDynamic();

        #endregion

        #region Vars (miscellaneous)

        public static readonly Var AllowUnresolvedVarsVar
            //= Var.intern(CLOJURE_NS, Symbol.intern("*allow-unresolved-vars*"), RT.F);
            = Var.intern(ClojureNamespace, Symbol.intern("*allow-unresolved-vars*"), false).setDynamic();

        public static readonly Var ReaderResolverVar
            = Var.intern(ClojureNamespace, Symbol.intern("*reader-resolver*"), null).setDynamic();

        public static readonly Var WarnOnReflectionVar
            //= Var.intern(CLOJURE_NS, Symbol.intern("*warn-on-reflection*"), RT.F);
            = Var.intern(ClojureNamespace, Symbol.intern("*warn-on-reflection*"), false).setDynamic();

        //public static readonly Var MACRO_META 
        //    = Var.intern(CLOJURE_NS, Symbol.intern("*macro-meta*"), null);

        public static readonly Var MathContextVar
            = Var.intern(ClojureNamespace, Symbol.intern("*math-context*"), null).setDynamic();

        public static readonly Var AgentVar
            = Var.intern(ClojureNamespace, Symbol.intern("*agent*"), null).setDynamic();

        static readonly Object _readeval = ReadTrueFalseUnknown(Environment.GetEnvironmentVariable("CLOJURE_READ_EVAL") ?? Environment.GetEnvironmentVariable("clojure.read.eval") ?? "true");

        public static readonly Var ReadEvalVar
            = Var.intern(ClojureNamespace, Symbol.intern("*read-eval*"), _readeval).setDynamic();

        public static readonly Var DataReadersVar
            = Var.intern(ClojureNamespace, Symbol.intern("*data-readers*"), RT.map()).setDynamic();

        public static readonly Var DefaultDataReaderFnVar
            = Var.intern(ClojureNamespace, Symbol.intern("*default-data-reader-fn*"), RT.map());

        public static readonly Var DefaultDataReadersVar
           = Var.intern(ClojureNamespace, Symbol.intern("default-data-readers"), RT.map());

        public static readonly Var SuppressReadVar
            = Var.intern(ClojureNamespace, Symbol.intern("*suppress-read*"), null).setDynamic();

        public static readonly Var AssertVar
            //= Var.intern(CLOJURE_NS, Symbol.intern("*assert*"), RT.T);
            = Var.intern(ClojureNamespace, Symbol.intern("*assert*"), true).setDynamic();

        public static readonly Var CmdLineArgsVar
            = Var.intern(ClojureNamespace, Symbol.intern("*command-line-args*"), null).setDynamic();

        public static readonly Var UseContextClassloaderVar
            //= Var.intern(CLOJURE_NS, Symbol.intern("*use-context-classloader*"), RT.T);
            = Var.intern(ClojureNamespace, Symbol.intern("*use-context-classloader*"), true).setDynamic();

        // boolean
        public static readonly Var UncheckedMathVar = Var.intern(Namespace.findOrCreate(Symbol.intern("clojure.core")),
            Symbol.intern("*unchecked-math*"), false).setDynamic();

        // CLEAT-012: compiler-side Vars that lived in Compiler.cs (core.clj expects them in clojure.core).
        // *loaded-libs*, *print-length*, *print-level*, *print-namespace-maps* are def'd by core.clj itself.
        public static readonly Var CompileFilesVar
            = Var.intern(ClojureNamespace, Symbol.intern("*compile-files*"), false).setDynamic();

        public static readonly Var CompilePathVar
            = Var.intern(ClojureNamespace, Symbol.intern("*compile-path*"), null).setDynamic();

        public static readonly Var CompilerOptionsVar
            = Var.intern(ClojureNamespace, Symbol.intern("*compiler-options*"), null).setDynamic();

        // *source-path* = file name, *file* = full path (ClojureCLR's SourceVar / SourcePathVar)
        public static readonly Var SourceVar
            = Var.intern(ClojureNamespace, Symbol.intern("*source-path*"), "NO_SOURCE_FILE").setDynamic();

        public static readonly Var SourcePathVar
            = Var.intern(ClojureNamespace, Symbol.intern("*file*"), "NO_SOURCE_PATH").setDynamic();

        #endregion

        #region  Clojure-environment IFns needing support

        static readonly Symbol InNsSymbol = Symbol.intern("in-ns");

        sealed class InNamespaceFn : AFn
        {
            public override object invoke(object arg1)
            {
                Symbol nsname = (Symbol)arg1;
                Namespace ns = Namespace.findOrCreate(nsname);
                if (Cleat.Core.Runtime.StartupTimings.InBootEntry) Cleat.Core.Runtime.StartupTimings.MarkNamespace(nsname.Name);   // cleat-159：镜像条目的边界
                CurrentNSVar.set(ns);
                return ns;
            }
        }
        static readonly Symbol NsSymbol = Symbol.intern("ns");

        sealed class BootNamespaceFN : AFn
        {
            public override object invoke(object __form, object __env, object arg1)
            {
                Symbol nsname = (Symbol)arg1;
                Namespace ns = Namespace.findOrCreate(nsname);
                if (Cleat.Core.Runtime.StartupTimings.InBootEntry) Cleat.Core.Runtime.StartupTimings.MarkNamespace(nsname.Name);   // cleat-159
                CurrentNSVar.set(ns);
                return ns;
            }
        }


        //static readonly Symbol IDENTICAL = Symbol.intern("identical?");

        //sealed class IdenticalFn : AFn
        //{
        //    public override object invoke(object arg1, object arg2)
        //    {
        //        //return Object.ReferenceEquals(arg1, arg2) ? RT.T : RT.F;
        //        if ( arg1 is ValueType )
        //            //return arg1.Equals(arg2) ? RT.T : RT.F;
        //            return arg1.Equals(arg2);
        //        else
        //            //return arg1 == arg2 ? RT.T : RT.F;
        //            return arg1 == arg2;
        //    }
        //}

        static readonly Symbol LoadFileSymbol = Symbol.intern("load-file");

        sealed class LoadFileFn : AFn
        {
            public override object invoke(object arg1)
            {
                // CLEAT-012: was Compiler.loadFile(arg1.ToString()); the Loader opens the file and calls the load-source hook
                return Cleat.Core.Runtime.Loader.LoadFile(arg1.ToString());
            }
        }

        #endregion

        #region Initialization

        [ThreadStatic]
        static bool _isResolving;

        static Assembly ResolveAssembly(object sender, ResolveEventArgs args)
        {
            if (_isResolving)
                return null;

            _isResolving = true;
            try
            {
                AssemblyName asmName = new(args.Name);
                var name = asmName.Name;

                // 1. Embedded resources (existing behavior)
                var stream = GetEmbeddedResourceStream(name, out _);
                if (stream == null)
                    stream = GetEmbeddedResourceStream(name + ".dll", out _);
                if (stream != null)
                    return Assembly.Load(ReadStreamBytes(stream));

                // 2. Check _runtimeAssemblyNames (DependencyContext + TRUSTED_PLATFORM_ASSEMBLIES)
                var runtimeNames = _runtimeAssemblyNames.Value;
                if (runtimeNames.TryGetValue(asmName, out var runtimePath)
                    && !string.IsNullOrWhiteSpace(runtimePath))
                {
                    try { return Assembly.LoadFrom(runtimePath); }
                    catch { /* Assembly may be corrupt or version-mismatched; fall through */ }
                }

                // 3. Probe the app's base directory
                var probePath = Path.Combine(AppContext.BaseDirectory, name + ".dll");
                if (File.Exists(probePath))
                {
                    try { return Assembly.LoadFrom(probePath); }
                    catch { /* File exists but isn't loadable; give up */ }
                }

                return null;
            }
            finally
            {
                _isResolving = false;
            }
        }

        // CLEAT-012: removed checkSpecAsserts / instrumentMacros / CHECK_SPECS (clojure.spec is not part of cleat)

        public static string SystemRuntimeDirectory = RuntimeEnvironment.GetRuntimeDirectory();

        static RT()
        {
            AppDomain.CurrentDomain.AssemblyResolve += ResolveAssembly;

            // CLEAT-005: removed DLR ScriptRuntimeSetup/LanguageSetup/ScriptRuntime/ClojureContext bootstrap (Microsoft.Scripting.Hosting)

            // CLEAT-005: was _versionProperties.LoadFromString(clojure.lang.Properties.Resources.version) (resx -> Bootstrap/version.properties)
            // CLEAT: the Clojure language level cleat implements (Cleat.Core.Runtime.CoreInfo.ClojureVersion); cleat's own version
            // is CoreInfo.Version (*cleat-version*)
            _versionProperties.LoadFromString("version=" + Cleat.Core.Runtime.CoreInfo.ClojureVersion);

            Keyword arglistskw = Keyword.intern(null, "arglists");
            Symbol namesym = Symbol.intern("name");

            OutVar.Tag = Symbol.intern("System.IO.TextWriter");

            CurrentNSVar.Tag = Symbol.intern("clojure.lang.Namespace");

            AgentVar.setMeta(map(DocKey, "The agent currently running an action on this thread, else nil."));
            AgentVar.Tag = Symbol.intern("clojure.lang.Agent");

            // We don't have MathContext (yet)
            //MATH_CONTEXT.Tag = Symbol.intern("java.math.MathContext");

            //// during bootstrap, ns same as in-ns
            //Var nv = Var.intern(CLOJURE_NS, NAMESPACE, new InNamespaceFn());
            Var nv = Var.intern(ClojureNamespace, NsSymbol, new BootNamespaceFN());
            nv.setMacro();

            Var v;
            v = Var.intern(ClojureNamespace, InNsSymbol, new InNamespaceFn());
            v.setMeta(map(DocKey, "Sets *ns* to the namespace named by the symbol, creating it if needed.",
                arglistskw, list(vector(namesym))));

            v = Var.intern(ClojureNamespace, LoadFileSymbol, new LoadFileFn());
            v.setMeta(map(DocKey, "Sequentially read and evaluate the set of forms contained in the file.",
                arglistskw, list(vector(namesym))));

            //v = Var.intern(CLOJURE_NS, IDENTICAL, new IdenticalFn());
            //v.setMeta(map(dockw, "tests if 2 arguments are the same object",
            //    arglistskw, list(vector(Symbol.intern("x"), Symbol.intern("y")))));

            // CLEAT-012: removed the Assembly.LoadFile(baseDir/"Clojure.Source.dll") probe and the
            // Compiler.InitializeCompilerOptions() + RuntimeBootstrapFlag._doRTBootstrap block that called load("clojure/core").
            // Startup is now explicit: register primitives -> RT.Init() -> Loader.LoadModule(boot image) or stage0 (see Loader.cs).
        }

        // CLEAT-012: removed LoadSpecCode (clojure.spec / clojure.clr.api) and PostBootstrapInit (user.clj, clojure.core.server);
        // the host does those explicitly (cleat-030). Init is idempotent and needs no compiler.

        private static bool INIT = false; // init guard
        private static readonly object InitLock = new();

        /// <summary>
        /// Idempotent runtime initialization: makes sure clojure.core and user exist, points the root of *ns* at user,
        /// touches the Loader (assembly resolution) and runs RuntimeHooks.OnInit when registered.
        /// Loading clojure.core itself is the boot image's / stage0's job and happens after this.
        /// </summary>
        public static void Init()
        {
            lock (InitLock)
            {
                if (INIT) { return; }
                INIT = true;

                Namespace.findOrCreate(Symbol.intern("clojure.core"));
                Namespace user = Namespace.findOrCreate(Symbol.intern("user"));
                CurrentNSVar.bindRoot(user);
                Cleat.Core.Runtime.Loader.EnsureInitialized();
                Cleat.Core.Runtime.RuntimeHooks.OnInit?.Invoke();
            }
        }

        public static bool IsInitialized => INIT;

        #endregion

        #region Id generation

        // This is AtomicInteger in the JVM version.
        // The only place accessed is in nextID, so seems unnecessary.
        private static int _id;

        // initial-lowercase name, used in core.clj

        // CLEAT-053: the Clojure boot-image build resets the counter at every top-level form (designs/08 §7), so gensyms and
        // fn class names do not depend on how many ids the building process consumed before (image fixpoint B == C).
        static public void setNextID(int value)
        {
            Interlocked.Exchange(ref _id, value);
        }

        static public int nextID()
        {
            return Interlocked.Increment(ref _id);
        }

        #endregion

        #region Keyword support


        public static Keyword keyword(string ns, string name)
        {
            return Keyword.intern((Symbol.intern(ns, name)));
        }

        #endregion

        #region Var support


        static public Var var(String ns, String name)
        {
            return Var.intern(Namespace.findOrCreate(Symbol.intern(null, ns)), Symbol.intern(null, name));
        }


        static public Var var(String ns, String name, Object init)
        {
            return Var.intern(Namespace.findOrCreate(Symbol.intern(null, ns)), Symbol.intern(null, name), init);
        }

        #endregion

        #region Collections support

        private const int CHUNK_SIZE = 32;

        // Because of the need to look before you leap (make sure one element exists)
        // this is more complicated than the JVM version:  In JVM-land, you can hasNext before you move.


        public static ISeq chunkEnumeratorSeq(IEnumerator iter)
        {
            if (!iter.MoveNext())
                return null;

            return PrimedChunkEnumeratorSeq(iter);
        }

        private static ISeq PrimedChunkEnumeratorSeq(IEnumerator iter)
        {
            return new LazySeq(new ChunkEnumeratorSeqHelper(iter));
        }

        private class ChunkEnumeratorSeqHelper : AFn
        {
            readonly IEnumerator _iter;

            public ChunkEnumeratorSeqHelper(IEnumerator iter)
            {
                _iter = iter;
            }

            // Assumes MoveNext has already been called on _iter.
            public override object invoke()
            {
                object[] arr = new object[CHUNK_SIZE];
                bool more = true;
                int n = 0;
                for (; n < CHUNK_SIZE && more; ++n)
                {
                    arr[n] = _iter.Current;
                    more = _iter.MoveNext();
                }

                return new ChunkedCons(new ArrayChunk(arr, 0, n), more ? PrimedChunkEnumeratorSeq(_iter) : null);
            }
        }



        public static ISeq seq(object coll)
        {
            if (coll is ASeq aseq)
                return aseq;

            // cleat-143：字符串不走 seqFrom 的长类型链；仍每次新建，保持返回对象 identity 语义。
            // 位置在 ASeq 之后，避免列表密集的项每次多一次 string 类型检查（远端 lattice A/B 的回退来源）。
            if (s_strseq && coll is string str)
                return StringSeq.create(str);

            return coll is LazySeq lseq ? lseq.seq() : seqFrom(coll);
        }

        // N.B. canSeq must be kept in sync with this!

        private static ISeq seqFrom(object coll)
        {
            if (coll == null)
                return null;

            if (coll is Seqable seq)
                return seq.seq();

            if (s_strseq && coll is string text)
                return StringSeq.create(text);   // cleat-143：类型检查比 GetType().IsArray 便宜，字符串先判

            if (coll.GetType().IsArray)
                return ArraySeq.createFromObject(coll);

            if (coll is string str)
                return StringSeq.create(str);

            if (coll is IEnumerable ie)  // java: Iterable  -- reordered clauses so others take precedence.
                return chunkEnumeratorSeq(ie.GetEnumerator());            // chunkIteratorSeq

            // The equivalent for Java:Map is IDictionary.  IDictionary is IEnumerable, so is handled above.
            //else if(coll isntanceof Map)  
            //     return seq(((Map) coll).entrySet());
            // Used to be in the java version:
            //else if (coll is IEnumerator)  // java: Iterator
            //    return EnumeratorSeq.create((IEnumerator)coll);

            throw new ArgumentException("Don't know how to create ISeq from: " + coll.GetType().FullName);
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Style", "IDE1006:Naming Styles", Justification = "ClojureJVM name match")]
        static public bool canSeq(object coll)
        {
            return coll == null
                || coll is ISeq
                || coll is Seqable
                || coll.GetType().IsArray
                || coll is String
                || coll is IEnumerable;
        }
        static IEnumerable NullIterator()
        {
            yield break;
        }

        static IEnumerable StringIterator(string str)
        {
            for (int i = 0; i < str.Length; i++)
                yield return str[i];
        }


        public static IEnumerator iter(Object coll)
        {
            if (coll == null)
                return NullIterator().GetEnumerator();

            // handled by IEnumerable case above
            if (coll is IDictionary dict)
                return dict.GetEnumerator();

            // reordered
            if (coll is IEnumerable ie)
                return ie.GetEnumerator();

            if (coll is string str)
                return StringIterator(str).GetEnumerator();

            if (coll.GetType().IsArray)
                return ArrayIter.createFromObject(coll);

            return iter(seq(coll));
        }


        public static object seqOrElse(object o)
        {
            return seq(o) == null ? null : o;
        }


        public static ISeq keys(object coll)
        {
            if (coll is IPersistentMap ipm)
                return APersistentMap.KeySeq.createFromMap(ipm);
            return APersistentMap.KeySeq.create(seq(coll));
        }


        public static ISeq vals(object coll)
        {
            if (coll is IPersistentMap ipm)
                return APersistentMap.ValSeq.createFromMap(ipm);
            return APersistentMap.ValSeq.create(seq(coll));
        }


        public static IPersistentMap meta(object x)
        {
            return x is IMeta m ? m.meta() : null;
        }


        public static int count(object o)
        {
            return o is Counted c ? c.count() : CountFrom(Util.Ret1(o, o = null));
        }

        static int CountFrom(object o)
        {
            if (o == null)
                return 0;

            if (o is IPersistentCollection)
            {
                ISeq s = seq(o);
                o = null;
                int i = 0;
                for (; s != null; s = s.next())
                {
                    if (s is Counted)
                        return i + s.count();
                    i++;
                }
                return i;
            }

            if (o is string str)
                return str.Length;

            if (o is ICollection c)
                return c.Count;

            if (o is IDictionary d)
                return d.Count;

            if (o is DictionaryEntry)
                return 2;

            if (o.GetType().IsGenericType && o.GetType().Name == "KeyValuePair`2")
                return 2;

            if (o is Array a)
                return a.GetLength(0);

            throw new InvalidOperationException("count not supported on this type: " + Util.NameForType(o.GetType()));
        }


        public static IPersistentCollection conj(IPersistentCollection coll, Object x)
        {
            if (coll == null)
                return new PersistentList(x);
            return coll.cons(x);
        }


        public static ISeq cons(object x, object coll)
        {
            if (coll == null)
                return new PersistentList(x);


            if (coll is ISeq s)
                return new Cons(x, s);

            return new Cons(x, seq(coll));
        }


        public static object first(object x)
        {
            // cleat-111：ASeq（Cons、PersistentList……）先判——类检查加虚调用，避开接口转换与接口分派的存根
            // Cons 是 sealed、PersistentList 按精确类型：一次方法表比较，first() 直接调用可内联；ASeq 不是 sealed，isinst 要走 CastHelpers
            if (x is Cons c) return c.first();
            if (x != null && x.GetType() == typeof(PersistentList)) return Unsafe.As<PersistentList>(x).first();
            if (x is ASeq a) return a.first();
            // cleat-143：不建 StringSeq。放在列表分支之后 —— lattice 的远端 A/B 显示把它放在最前面时
            // 列表密集的项每次多一次 string 类型检查，回退约 6%；字符串侧的收益来自免建 StringSeq，与这里的顺序无关。
            if (s_strseq && x is string s)
                return s.Length == 0 ? null : Cleat.Core.Runtime.CharBox.Get(s[0]);
            ISeq seq = x as ISeq ?? RT.seq(x);

            if (seq == null)
                return null;

            return seq.first();
        }


        public static object second(object x)
        {
            return first(next(x));
        }


        public static object third(object x)
        {
            return first(next(next(x)));
        }


        public static object fourth(object x)
        {
            return first(next(next(next(x))));
        }


        public static ISeq next(object x)
        {
            // Cons 是 sealed、PersistentList 按精确类型：一次方法表比较，next() 直接调用可内联；ASeq 不是 sealed，isinst 要走 CastHelpers
            if (x is Cons c) return c.next();
            if (x != null && x.GetType() == typeof(PersistentList)) return Unsafe.As<PersistentList>(x).next();
            if (x is ASeq a) return a.next();   // cleat-111
            // cleat-143：直接给剩余串建同一个 StringSeq 类型；空串 / 单字符仍返回 null（分支位置理由同 first）
            if (s_strseq && x is string s)
                return StringSeq.create(s, 1);
            ISeq seq = (x as ISeq) ?? RT.seq(x);

            if (seq == null)
                return null;

            return seq.next();
        }


        public static ISeq more(object x)
        {
            // Cons 是 sealed：一次方法表比较，more() 直接调用可内联；ASeq 不是 sealed，isinst 要走 CastHelpers
            if (x is Cons c) return c.more();
            if (x is ASeq a) return a.more();   // cleat-111
            // cleat-143：与 seq(s).more() 同类型同边界（分支位置理由同 first）
            if (s_strseq && x is string s)
                return s.Length <= 1 ? PersistentList.EMPTY : StringSeq.create(s, 1);
            ISeq seq = x as ISeq ?? RT.seq(x);

            if (seq == null)
                return PersistentList.EMPTY;

            return seq.more();
        }




        public static object peek(object x)
        {
            return x == null
                ? null
                : ((IPersistentStack)x).peek();
        }


        public static object pop(object x)
        {
            return x == null
                ? null
                : ((IPersistentStack)x).pop();
        }


        private static readonly Object ReqNotFound = new();

        static public String reqmsg(Object key)
        {
            String msg = "Missing required key: ";
            if (key is String s)
                return msg + "\"" + s + "\"";
            else
                return msg + key;
        }

        static public Object req(Object coll, Object key)
        {
            Object v = get(coll, key, ReqNotFound);
            if (v == ReqNotFound)
                throw new ArgumentException(reqmsg(key));
            else
                return v;
        }

        static public Object get(Object coll, Object key)
        {

            if (coll is ILookup ilu)
                return ilu.valAt(key);

            return GetFrom(coll, key);
        }

        static object GetFrom(object coll, object key)
        {
            if (coll == null)
                return null;

            if (coll is IDictionary m)
                return m[key];

            if (Util.IsNumeric(key) && (coll is string || coll.GetType().IsArray))
            {
                int n = Util.ConvertToInt(key);
                return n >= 0 && n < count(coll) ? nth(coll, n) : null;
            }

            return null;
        }


        static public Object get(Object coll, Object key, Object notFound)
        {
            if (coll is ILookup ilu)
                return ilu.valAt(key, notFound);

            return GetFrom(coll, key, notFound);
        }

        static object GetFrom(object coll, object key, object notFound)
        {
            if (coll == null)
                return notFound;


            if (coll is IDictionary m)
            {
                if (m.Contains(key))
                    return m[key];
                return notFound;
            }

            if (Util.IsNumeric(key) && (coll is string || coll.GetType().IsArray))
            {
                int n = Util.ConvertToInt(key);
                return n >= 0 && n < count(coll) ? nth(coll, n) : notFound;
            }

            return notFound;
        }


        public static Associative assoc(object coll, object key, Object val)
        {
            if (coll == null)
                return new PersistentArrayMap([key, val]);
            return ((Associative)coll).assoc(key, val);
        }


        public static object contains(object coll, object key)
        {
            if (coll == null)
                //return RT.F;
                return false;

            if (coll is Associative assoc)
                //return ((Associative)coll).containsKey(key) ? RT.T : RT.F;
                return assoc.containsKey(key);

            if (coll is IPersistentSet set)
                //return ((IPersistentSet)coll).contains(key) ? RT.T : RT.F;
                return set.contains(key);


            if (coll is IDictionary m)
            {
                //return m.Contains(key) ? RT.T : RT.F;
                return key != null && m.Contains(key);
            }

            // TODO: Make this work for ISet<T> no matter the T
            if (coll is ISet<object> iso)
            {
                // return  iso.Contains(key) ? RT.T : RT.F;
                return iso.Contains(key);
            }

            if (Util.IsNumeric(key) && (coll is String || coll.GetType().IsArray))
            {
                int n = Util.ConvertToInt(key);
                return n >= 0 && n < count(coll);
            }

            if (coll is ITransientSet tset)
                return tset.contains(key);  // RT.T, RT.F

            if (coll is ITransientAssociative2 ta2)
                return ta2.containsKey(key);    // RT.T, RT.F

            throw new ArgumentException("contains? not supported on type: " + coll.GetType().Name);
        }


        public static object find(object coll, object key)
        {
            if (coll == null)
                return null;

            if (coll is Associative assoc)
                return assoc.entryAt(key);

            if (coll is IDictionary m)
            {
                if (m.Contains(key))
                    return MapEntry.create(key, m[key]);
                return null;
            }

            if (coll is ITransientAssociative2 ta2)
                return ta2.entryAt(key);

            // CLEAT-032: like the JVM's RT.find
            throw new ArgumentException("find not supported on type: " + Util.NameForType(coll.GetType()));
        }


        public static object dissoc(object coll, object key)
        {
            return coll == null
                ? null
                : ((IPersistentMap)coll).without(key);
        }

        //static public Object nth(object coll, long n)
        //{
        //    return nth(coll, (int)n);
        //}

        static public bool SupportsRandomAccess(object coll)
        {
            return coll is Indexed ||
                coll == null ||
                coll is String ||
                coll.GetType().IsArray ||
                coll is IList ||
                coll is DictionaryEntry ||
                coll is IMapEntry ||
                coll is JReMatcher ||
                coll is Match ||
                (coll.GetType().IsGenericType && coll.GetType().Name == "KeyValuePair`2");
        }


        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static public Object nth(object coll, int n)
        {
            // 向量按精确类型先判（Indexed 是接口，isinst 走 CastHelpers）；不是 Indexed 时形参经 ref 交出并清空：
            // tier-0 把形参与 JIT 临时变量报告为整个方法内活着，按值传会在遍历期间抓住惰性序列的头（cleat-117）
            if (coll != null && coll.GetType() == typeof(PersistentVector))
                return Unsafe.As<PersistentVector>(coll).nth(n);
            if (coll is Indexed indexed)
                return indexed.nth((int)n);

            return NthFromRef(ref coll, n);
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private static object NthFromRef(ref object slot, int n)
        {
            object coll = slot;
            slot = null;
            if (s_nthfast)
            {
                if (coll == null)
                    return null;

                // 既有热分支（string / array）仍在 ISeq 前面；ISeq 换掉旧 NthFrom 二次调用与 RT.seq。
                if (coll is string str)
                    return Cleat.Core.Runtime.CharBox.Get(str[n]);

                if (coll.GetType().IsArray)
                    return ((Array)coll).GetValue(n);

                if (coll is ISeq iseq)
                {
                    // 必须过 RT.seq 归一：PersistentList+EmptyList 也实现 ISeq 但 first() 恒为 nil，
                    // 不经 seq() 会把「空列表越界」读成「元素是 nil」（cleat-150 的 CTS 回归）。
                    // ASeq 分支是 isinst 直接返回、LazySeq 只多一次 seq()，非空遍历仍是引用直走。
                    ISeq seq = RT.seq(iseq);
                    iseq = null;

                    if (seq is StringSeq ss)
                    {
                        int i = ss.I + n;
                        string text = ss.S;
                        if (n < 0 || i >= text.Length)
                            throw new ArgumentOutOfRangeException("n");
                        return Cleat.Core.Runtime.CharBox.Get(text[i]);
                    }

                    // 直接在 ref 入口遍历：去掉旧 NthFrom → RT.seq 的第二层调用；coll 已清空。
                    coll = null;
                    for (int i = 0; i <= n && seq != null; ++i, seq = seq.next())
                    {
                        if (i == n)
                            return seq.first();
                    }
                    throw new ArgumentOutOfRangeException("n");
                }
            }

            return NthFrom(coll, n);
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]   // 遍历传入的序列：tier-0 把形参报告为整个方法内活着，会抓住惰性序列的头（cleat-117）
        static object NthFrom(object coll, int n)
        {
            if (coll == null)
                return null;

            if (coll is string str)
                return Cleat.Core.Runtime.CharBox.Get(str[n]);   // JVM Character cache 对齐：nth 反复读同几个字符时不重复装箱

            if (coll.GetType().IsArray)
                return ((Array)coll).GetValue(n); // CLEAT-005: was Reflector.prepRet(elemType, value); prepRet is the identity in ClojureCLR

            // Java has RandomAccess here.  CLR has no equiv.
            // Trying to replace it with IList caused some real problems,  See the fix in ASeq.

            // 序列（LazySeq、ASeq 也实现 IList）走下面的 Sequential 分支（同 JVM：它们不是 RandomAccess）：IList 的索引器抓着序列的头，
            // (nth (map inc (range 3e7)) 29999999) 在 512 MB 堆下 OutOfMemory（cleat-117）
            if (coll is ISeq || coll is LazySeq)
                return NthFromSeq(coll, n);

            if (coll is IList ilist)
                return ilist[n];

            if (coll is JReMatcher jrem)
                return jrem.group(n);

            if (coll is Match match)
                return match.Groups[n];

            if (coll is DictionaryEntry e)
            {
                if (n == 0)
                    return e.Key;
                else if (n == 1)
                    return e.Value;
                throw new ArgumentOutOfRangeException("n");
            }

            if (coll.GetType().IsGenericType && coll.GetType().Name == "KeyValuePair`2")
            {
                if (n == 0)
                    return coll.GetType().InvokeMember("Key", BindingFlags.GetProperty, null, coll, null);
                else if (n == 1)
                    return coll.GetType().InvokeMember("Value", BindingFlags.GetProperty, null, coll, null);
                throw new ArgumentOutOfRangeException("n");
            }

            if (coll is IMapEntry me)
            {
                if (n == 0)
                    return me.key();
                else if (n == 1)
                    return me.val();
                throw new ArgumentOutOfRangeException("n");
            }

            if (coll is Sequential)
                return NthFromSeq(coll, n);

            throw new InvalidOperationException("nth not supported on this type: " + Util.NameForType(coll.GetType()));
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]   // 遍历传入的序列：tier-0 把形参报告为整个方法内活着，会抓住惰性序列的头（cleat-117）
        private static object NthFromSeq(object coll, int n)
        {
            ISeq seq = RT.seq(coll);
            coll = null;
            for (int i = 0; i <= n && seq != null; ++i, seq = seq.next())
            {
                if (i == n)
                    return seq.first();
            }
            throw new ArgumentOutOfRangeException("n");
        }

        private static object NthFromSeq(ISeq seq, int n, object notFound)
        {
            ISeq current = seq;
            seq = null;
            for (int i = 0; i <= n && current != null; ++i, current = current.next())
            {
                if (i == n)
                    return current.first();
            }
            return notFound;
        }


        //static public Object nth(Object coll, long n, Object notFound)
        //{
        //    return nth(coll, (int)n, notFound);
        //}


        static public Object nth(Object coll, int n, Object notFound)
        {

            if (coll is Indexed v)
                return v.nth(n, notFound);

            return NthFrom(coll, n, notFound);
        }

        static object NthFrom(object coll, int n, object notFound)
        {
            if (coll == null)
                return notFound;

            if (n < 0)
                return notFound;

            if (coll is string s)
            {
                if (n < s.Length)
                    return Cleat.Core.Runtime.CharBox.Get(s[n]);
                return notFound;
            }

            if (coll.GetType().IsArray)
            {
                Array a = (Array)coll;
                if (n < a.Length)
                    return a.GetValue(n); // CLEAT-005: was Reflector.prepRet(elemType, value); prepRet is the identity in ClojureCLR
                return notFound;
            }

            if (s_nthfast)
            {
                if (coll is ISeq iseq)
                {
                    if (iseq is StringSeq ss)
                    {
                        int i = ss.I + n;
                        string text = ss.S;
                        return n >= 0 && i < text.Length ? Cleat.Core.Runtime.CharBox.Get(text[i]) : notFound;
                    }

                    // Counted 只对 O(1) count 的持久 list 用来提前判 miss；一般 seq 的 count 可能是 O(n)，不能放热路径。
                    if (iseq is PersistentList plist && n >= 0 && n >= plist.count())
                        return notFound;

                    if (n < 0)
                        return notFound;

                    // 同 arity-2：先过 RT.seq 归一（PersistentList+EmptyList 是 ISeq 但 seq() 为 nil），
                    // ASeq 直接返回自身，LazySeq 的 realize 也已在 RT.seq 内完成。
                    ISeq seq = RT.seq(iseq);
                    iseq = null;
                    coll = null;
                    return NthFromSeq(seq, n, notFound);
                }
            }

            // Causes a problem with infinite LazySequences
            // Four years after the fact, I now know why the change was made in Java Rev 1218.
            // There is no RandomAccess equivalent in CLR.
            // So we don't blow off IList's completely, I put this after the code that catches LazySeqs.
            //IList list = coll as IList;
            //if (list != null)   // Changed to RandomAccess in Java Rev 1218.  
            //{
            //    if (n < list.Count)
            //        return list[n];
            //    return notFound;
            //}

            if (coll is JReMatcher jrem)
            {
                if (jrem.IsUnrealizedOrFailed)
                    return notFound;
                int groups = jrem.groupCount();
                if (groups > 0 && n <= jrem.groupCount())
                    return jrem.group(n);
                return notFound;
            }

            if (coll is Match m)
            {
                if (n < m.Groups.Count)
                    return m.Groups[n];
                return notFound;
            }


            if (coll is DictionaryEntry e)
            {
                if (n == 0)
                    return e.Key;
                else if (n == 1)
                    return e.Value;
                return notFound;
            }

            if (coll.GetType().IsGenericType && coll.GetType().Name == "KeyValuePair`2")
            {
                if (n == 0)
                    return coll.GetType().InvokeMember("Key", BindingFlags.GetProperty, null, coll, null);
                else if (n == 1)
                    return coll.GetType().InvokeMember("Value", BindingFlags.GetProperty, null, coll, null);
                return notFound;
            }

            if (coll is IMapEntry me)
            {
                if (n == 0)
                    return me.key();
                else if (n == 1)
                    return me.val();
                return notFound;
            }

            if (coll is Sequential)
            {
                ISeq seq = RT.seq(coll);
                coll = null;  // release in case GC
                for (int i = 0; i <= n && seq != null; ++i, seq = seq.next())
                {
                    if (i == n)
                        return seq.first();
                }
                return notFound;
            }

            if (coll is IList list)
            {
                if (n < list.Count)
                    return list[n];
                return notFound;
            }

            throw new InvalidOperationException("nth not supported on this type: " + Util.NameForType(coll.GetType()));
        }

        #endregion

        #region boxing/casts

        #region box

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Style", "IDE1006:Naming Styles", Justification = "ClojureJVM name match")]
        static public Object box(Object x)
        {
            return x;
        }

        #endregion

        #region char casting


        public static char charCast(object x)
        {
            if (x is char ch)
                return ch;

            long n = Util.ConvertToLong(x);
            if (n < Char.MinValue || n > char.MaxValue)
                throw new ArgumentException("Value out of range for char: " + x);

            return (char)n;
        }

        // JVM version has the following, but they really seem to mess things up for me.

        //public static char charCast(byte x)
        //{
        //    char i = (char)x;
        //    if (i != x)
        //        throw new ArgumentException("Value out of range for char: " + x);
        //    return i;
        //}

        //public static char charCast(sbyte x)
        //{
        //    char i = (char)x;
        //    if (i != x)
        //        throw new ArgumentException("Value out of range for char: " + x);
        //    return i;
        //}

        //public static char charCast(short x)
        //{
        //    char i = (char)x;
        //    if (i != x)
        //        throw new ArgumentException("Value out of range for char: " + x);
        //    return i;
        //}

        //public static char charCast(ushort x)
        //{
        //    char i = (char)x;
        //    if (i != x)
        //        throw new ArgumentException("Value out of range for char: " + x);
        //    return i;
        //}

        //public static char charCast(int x)
        //{
        //    char i = (char)x;
        //    if (i != x)
        //        throw new ArgumentException("Value out of range for char: " + x);
        //    return i;
        //}

        //public static char charCast(uint x)
        //{
        //    char i = (char)x;
        //    if (i != x)
        //        throw new ArgumentException("Value out of range for char: " + x);
        //    return i;
        //}


        public static char charCast(long x)
        {
            char i = (char)x;
            if (i != x)
                throw new ArgumentException("Value out of range for char: " + x);
            return i;
        }

        //public static char charCast(ulong x)
        //{
        //    char i = (char)x;
        //    if (i != x)
        //        throw new ArgumentException("Value out of range for char: " + x);
        //    return i;
        //}

        //public static char charCast(float x)
        //{
        //    if (x >= Char.MinValue && x <= char.MaxValue)
        //        return (char)x;
        //    throw new ArgumentException("Value out of range for char: " + x);
        //}


        public static char charCast(double x)
        {
            return x >= Char.MinValue && x <= char.MaxValue ? (char)x : throw new ArgumentException("Value out of range for char: " + x);
        }

        #endregion

        #region bool casting


        static public bool booleanCast(object x)
        {
            return x is Boolean b ? b : x != null;
        }


        static public bool booleanCast(bool x)
        {
            return x;
        }

        #endregion

        #region casting misc numeric types from object


        public static byte byteCast(object x)
        {
            if (x is byte b)
                return b;

            long n = Util.ConvertToLong(x);
            if (n < Byte.MinValue || n > Byte.MaxValue)
                throw new ArgumentException("Value out of range for byte: " + x);

            return (byte)n;
        }


        public static sbyte sbyteCast(object x)
        {
            if (x is sbyte b)
                return b;

            long n = Util.ConvertToLong(x);
            if (n < SByte.MinValue || n > SByte.MaxValue)
                throw new ArgumentException("Value out of range for byte: " + x);

            return (sbyte)n;
        }



        public static short shortCast(object x)
        {
            if (x is short s)
                return s;
            long n = Util.ConvertToLong(x);
            if (n < short.MinValue || n > short.MaxValue)
                throw new ArgumentException("Value out of range for short: " + x);

            return (short)n;
        }


        public static ushort ushortCast(object x)
        {
            if (x is ushort u)
                return u;
            long n = Util.ConvertToLong(x);
            if (n < ushort.MinValue || n > ushort.MaxValue)
                throw new ArgumentException("Value out of range for ushort: " + x);

            return (ushort)n;
        }


        public static uint uintCast(object x)
        {
            if (x is uint u)
                return u;
            long n = Util.ConvertToLong(x);
            if (n < uint.MinValue || n > uint.MaxValue)
                throw new ArgumentException("Value out of range for uint: " + x);

            return (uint)n;
        }


        public static ulong ulongCast(object x)
        {
            return Convert.ToUInt64(x);
        }


        public static decimal decimalCast(object x)
        {
            return Convert.ToDecimal(x);
        }

        #endregion

        #region int casting

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public static int intCast(object x)
        {
            if (x is long l)
                return intCast(l);
            if (x is int i)
                return i;
            return intCast(LongCastSlow(x));
        }


        static public int intCast(char x)
        {
            return x;
        }


        static public int intCast(byte x)
        {
            return x;
        }


        static public int intCast(sbyte x)
        {
            return x;
        }


        static public int intCast(short x)
        {
            return x;
        }


        static public int intCast(ushort x)
        {
            return x;
        }


        static public int intCast(int x)
        {
            return x;
        }


        static public int intCast(uint x)
        {
            if (x > int.MaxValue)
                throw new ArgumentException("Value out of range for int: " + x);

            return (int)x;
        }


        static public int intCast(float x)
        {
            if (x < int.MinValue || x > int.MaxValue)
                throw new ArgumentException("Value out of range for int: " + x);

            return (int)x;
        }


        // cleat-113：每次数组下标都调用（array1 里占 17%）：抛出部分放到不内联的方法里，本身内联进生成的代码
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        static public int intCast(long x)
        {
            int i = (int)x;
            if (i != x)
                return ThrowIntRange(x);
            return i;
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        static int ThrowIntRange(long x) => throw new ArgumentException("Value out of range for int: " + x);


        static public int intCast(ulong x)
        {
            if (x > int.MaxValue)
                throw new ArgumentException("Value out of range for int: " + x);

            return (int)x;
        }


        static public int intCast(double x)
        {
            if (x < int.MinValue || x > int.MaxValue)
                throw new ArgumentException("Value out of range for int: " + x);

            return (int)x;
        }

        #endregion

        #region long casting


        // long 的快路径内联进调用方（数组下标、^long 转换都经过这里），其余类型在 LongCastSlow
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static long longCast(object x) => x is long lng ? lng : LongCastSlow(x);

        private static long LongCastSlow(object x)
        {
            if (x is int i)
                return (long)i;

            if (x is BigInt bi)
            {
                if (bi.Bipart == null)
                    return bi.Lpart;
                else
                    throw new ArgumentException("Value out of range for long: " + x);
            }

            BigInteger big = x as BigInteger;
            if (big != null)
            {
                if (big.AsInt64(out long n))
                    return n;
                else
                    throw new ArgumentException("Value out of range for long: " + x);
            }

            if (x is ulong ux)
            {
                if (ux > long.MaxValue)
                    throw new ArgumentException("Value out of range for long: " + x);
                return (long)x;
            }

            if (x is byte || x is short || x is uint || x is sbyte || x is ushort)
                return Util.ConvertToLong(x);


            if (x is Ratio r)
                return longCast(r.BigIntegerValue());

            if (x is Char)
                return longCast((char)x);

            return longCast(Util.ConvertToDouble(x));
        }



        public static long longCast(char x) { return x; }

        public static long longCast(byte x) { return x; }

        public static long longCast(sbyte x) { return x; }

        public static long longCast(short x) { return x; }

        public static long longCast(ushort x) { return x; }

        public static long longCast(uint x) { return x; }


        public static long longCast(int x) { return x; }


        static public long longCast(float x)
        {
            if (x < long.MinValue || x > long.MaxValue)
                throw new ArgumentException("Value out of range for long: " + x);

            return (long)x;
        }


        static public long longCast(long x)
        {
            return x;
        }


        static public long longCast(ulong x)
        {
            if (x > long.MaxValue)
                throw new ArgumentException("Value out of range for long: " + x);

            return (long)x;
        }


        static public long longCast(double x)
        {
            if (x < long.MinValue || x > long.MaxValue)
                throw new ArgumentException("Value out of range for long: " + x);

            return (long)x;
        }

        #endregion

        #region float casting


        public static float floatCast(object x)
        {
            if (x is float f)
                return f;

            double n = Util.ConvertToDouble(x);
            if (n < float.MinValue || n > float.MaxValue)
                throw new ArgumentException("Value out of range for float: " + x);

            return (float)n;
        }


        public static float floatCast(char x) { return x; }

        public static float floatCast(byte x) { return x; }

        public static float floatCast(sbyte x) { return x; }

        public static float floatCast(short x) { return x; }

        public static float floatCast(ushort x) { return x; }

        public static float floatCast(int x) { return x; }

        public static float floatCast(uint x) { return x; }

        public static float floatCast(long x) { return x; }

        public static float floatCast(ulong x) { return x; }

        public static float floatCast(float x) { return x; }



        public static float floatCast(decimal x)
        {
            return floatCast((double)x);
        }


        public static float floatCast(double x)
        {
            if (x < float.MinValue || x > float.MaxValue)
                throw new ArgumentException("Valueout of range for float: " + x);
            return (float)x;
        }

        #endregion

        #region double casting


        public static double doubleCast(object x)
        {
            return Util.ConvertToDouble(x);
        }


        public static double doubleCast(char x) { return x; }

        public static double doubleCast(byte x) { return x; }

        public static double doubleCast(sbyte x) { return x; }

        public static double doubleCast(short x) { return x; }

        public static double doubleCast(ushort x) { return x; }

        public static double doubleCast(int x) { return x; }

        public static double doubleCast(uint x) { return x; }

        public static double doubleCast(long x) { return x; }

        public static double doubleCast(ulong x) { return x; }

        public static double doubleCast(float x) { return x; }

        public static double doubleCast(double x) { return x; }

        public static double doubleCast(decimal x) { return (double)x; }

        #endregion

        #region unchecked byte casting


        static public byte uncheckedByteCast(Object x)
        {
            return Util.ConvertToByte(x);
        }


        static public byte uncheckedByteCast(char x) { return (byte)x; }

        static public byte uncheckedByteCast(byte x) { return (byte)x; }

        static public byte uncheckedByteCast(sbyte x) { return (byte)x; }

        static public byte uncheckedByteCast(short x) { return (byte)x; }

        static public byte uncheckedByteCast(ushort x) { return (byte)x; }

        static public byte uncheckedByteCast(int x) { return (byte)x; }

        static public byte uncheckedByteCast(uint x) { return (byte)x; }

        static public byte uncheckedByteCast(long x) { return (byte)x; }

        static public byte uncheckedByteCast(ulong x) { return (byte)x; }

        static public byte uncheckedByteCast(float x) { return (byte)x; }

        static public byte uncheckedByteCast(double x) { return (byte)x; }

        static public byte uncheckedByteCast(decimal x) { return (byte)x; }

        #endregion

        #region unchecked sbyte casting


        static public sbyte uncheckedSByteCast(Object x)
        {
            return Util.ConvertToSByte(x);
        }


        static public sbyte uncheckedSByteCast(char x) { return (sbyte)x; }

        static public sbyte uncheckedSByteCast(byte x) { return (sbyte)x; }

        static public sbyte uncheckedSByteCast(sbyte x) { return (sbyte)x; }

        static public sbyte uncheckedSByteCast(short x) { return (sbyte)x; }

        static public sbyte uncheckedSByteCast(ushort x) { return (sbyte)x; }

        static public sbyte uncheckedSByteCast(int x) { return (sbyte)x; }

        static public sbyte uncheckedSByteCast(uint x) { return (sbyte)x; }

        static public sbyte uncheckedSByteCast(long x) { return (sbyte)x; }

        static public sbyte uncheckedSByteCast(ulong x) { return (sbyte)x; }

        static public sbyte uncheckedSByteCast(float x) { return (sbyte)x; }

        static public sbyte uncheckedSByteCast(double x) { return (sbyte)x; }

        static public sbyte uncheckedSByteCast(decimal x) { return (sbyte)x; }

        #endregion

        #region unchecked short casting


        static public short uncheckedShortCast(Object x)
        {
            return Util.ConvertToShort(x);
        }


        static public short uncheckedShortCast(char x) { return (short)x; }

        static public short uncheckedShortCast(byte x) { return (short)x; }

        static public short uncheckedShortCast(sbyte x) { return (short)x; }

        static public short uncheckedShortCast(short x) { return (short)x; }


        static public short uncheckedShortCast(ushort x) { return (short)x; }

        static public short uncheckedShortCast(int x) { return (short)x; }

        static public short uncheckedShortCast(uint x) { return (short)x; }

        static public short uncheckedShortCast(long x) { return (short)x; }

        static public short uncheckedShortCast(ulong x) { return (short)x; }

        static public short uncheckedShortCast(float x) { return (short)x; }

        static public short uncheckedShortCast(double x) { return (short)x; }

        static public short uncheckedShortCast(decimal x) { return (short)x; }

        #endregion

        #region unchecked ushort casting


        static public ushort uncheckedUShortCast(Object x)
        {
            return Util.ConvertToUShort(x);
        }


        static public ushort uncheckedUShortCast(char x) { return (ushort)x; }
        static public ushort uncheckedUShortCast(byte x) { return (ushort)x; }
        static public ushort uncheckedUShortCast(sbyte x) { return (ushort)x; }
        static public ushort uncheckedUShortCast(short x) { return (ushort)x; }
        static public ushort uncheckedUShortCast(ushort x) { return (ushort)x; }
        static public ushort uncheckedUShortCast(int x) { return (ushort)x; }
        static public ushort uncheckedUShortCast(uint x) { return (ushort)x; }
        static public ushort uncheckedUShortCast(long x) { return (ushort)x; }
        static public ushort uncheckedUShortCast(ulong x) { return (ushort)x; }
        static public ushort uncheckedUShortCast(float x) { return (ushort)x; }
        static public ushort uncheckedUShortCast(double x) { return (ushort)x; }
        static public ushort uncheckedUShortCast(decimal x) { return (ushort)x; }

        #endregion

        #region unchecked int casting


        static public int uncheckedIntCast(Object x)
        {
            return Util.ConvertToInt(x);
        }


        static public int uncheckedIntCast(char x) { return (int)x; }

        static public int uncheckedIntCast(byte x) { return (int)x; }

        static public int uncheckedIntCast(sbyte x) { return (int)x; }

        static public int uncheckedIntCast(short x) { return (int)x; }

        static public int uncheckedIntCast(ushort x) { return (int)x; }

        static public int uncheckedIntCast(int x) { return (int)x; }

        static public int uncheckedIntCast(uint x) { return (int)x; }

        static public int uncheckedIntCast(long x) { return (int)x; }

        static public int uncheckedIntCast(ulong x) { return (int)x; }

        static public int uncheckedIntCast(float x) { return (int)x; }

        static public int uncheckedIntCast(double x) { return (int)x; }

        static public int uncheckedIntCast(decimal x) { return (int)x; }

        #endregion

        #region unchecked uint casting


        static public uint uncheckedUIntCast(Object x)
        {
            return Util.ConvertToUInt(x);
        }


        static public uint uncheckedUIntCast(char x) { return (uint)x; }

        static public uint uncheckedUIntCast(byte x) { return (uint)x; }

        static public uint uncheckedUIntCast(sbyte x) { return (uint)x; }

        static public uint uncheckedUIntCast(short x) { return (uint)x; }

        static public uint uncheckedUIntCast(ushort x) { return (uint)x; }

        static public uint uncheckedUIntCast(int x) { return (uint)x; }

        static public uint uncheckedUIntCast(uint x) { return (uint)x; }

        static public uint uncheckedUIntCast(long x) { return (uint)x; }

        static public uint uncheckedUIntCast(ulong x) { return (uint)x; }

        static public uint uncheckedUIntCast(float x) { return (uint)x; }

        static public uint uncheckedUIntCast(double x) { return (uint)x; }

        static public uint uncheckedUIntCast(decimal x) { return (uint)x; }

        #endregion

        #region unchecked long casting


        static public long uncheckedLongCast(Object x)
        {
            return Util.ConvertToLong(x);
        }


        static public long uncheckedLongCast(char x) { return (long)x; }

        static public long uncheckedLongCast(byte x) { return (long)x; }

        static public long uncheckedLongCast(sbyte x) { return (long)x; }

        static public long uncheckedLongCast(short x) { return (long)x; }

        static public long uncheckedLongCast(ushort x) { return (long)x; }

        static public long uncheckedLongCast(int x) { return (long)x; }

        static public long uncheckedLongCast(uint x) { return (long)x; }

        static public long uncheckedLongCast(long x) { return (long)x; }

        static public long uncheckedLongCast(ulong x) { return (long)x; }

        static public long uncheckedLongCast(float x) { return (long)x; }

        static public long uncheckedLongCast(double x) { return (long)x; }

        static public long uncheckedLongCast(decimal x) { return (long)x; }

        #endregion

        #region unchecked ulong casting


        static public ulong uncheckedULongCast(Object x)
        {
            return Util.ConvertToULong(x);
        }


        static public ulong uncheckedULongCast(char x) { return (ulong)x; }

        static public ulong uncheckedULongCast(byte x) { return (ulong)x; }

        static public ulong uncheckedULongCast(sbyte x) { return (ulong)x; }

        static public ulong uncheckedULongCast(short x) { return (ulong)x; }

        static public ulong uncheckedULongCast(ushort x) { return (ulong)x; }

        static public ulong uncheckedULongCast(int x) { return (ulong)x; }

        static public ulong uncheckedULongCast(uint x) { return (ulong)x; }

        static public ulong uncheckedULongCast(long x) { return (ulong)x; }

        static public ulong uncheckedULongCast(ulong x) { return (ulong)x; }

        static public ulong uncheckedULongCast(float x) { return (ulong)x; }

        static public ulong uncheckedULongCast(double x) { return (ulong)x; }

        static public ulong uncheckedULongCast(decimal x) { return (ulong)x; }

        #endregion

        #region unchecked float casting


        static public float uncheckedFloatCast(Object x)
        {
            return Util.ConvertToFloat(x);
        }


        static public float uncheckedFloatCast(char x) { return (float)x; }

        static public float uncheckedFloatCast(byte x) { return (float)x; }

        static public float uncheckedFloatCast(sbyte x) { return (float)x; }

        static public float uncheckedFloatCast(short x) { return (float)x; }

        static public float uncheckedFloatCast(ushort x) { return (float)x; }

        static public float uncheckedFloatCast(int x) { return (float)x; }

        static public float uncheckedFloatCast(uint x) { return (float)x; }

        static public float uncheckedFloatCast(long x) { return (float)x; }

        static public float uncheckedFloatCast(ulong x) { return (float)x; }

        static public float uncheckedFloatCast(float x) { return (float)x; }

        static public float uncheckedFloatCast(double x) { return (float)x; }

        static public float uncheckedFloatCast(decimal x) { return (float)x; }

        #endregion

        #region unchecked double casting


        static public double uncheckedDoubleCast(Object x)
        {
            return Util.ConvertToDouble(x);
        }


        static public double uncheckedDoubleCast(char x) { return (double)x; }

        static public double uncheckedDoubleCast(byte x) { return (double)x; }

        static public double uncheckedDoubleCast(sbyte x) { return (double)x; }

        static public double uncheckedDoubleCast(short x) { return (double)x; }

        static public double uncheckedDoubleCast(ushort x) { return (double)x; }

        static public double uncheckedDoubleCast(int x) { return (double)x; }

        static public double uncheckedDoubleCast(uint x) { return (double)x; }

        static public double uncheckedDoubleCast(long x) { return (double)x; }

        static public double uncheckedDoubleCast(ulong x) { return (double)x; }

        static public double uncheckedDoubleCast(float x) { return (double)x; }

        static public double uncheckedDoubleCast(double x) { return (double)x; }

        static public double uncheckedDoubleCast(decimal x) { return (double)x; }

        #endregion

        #region unchecked decimal casting


        static public decimal uncheckedDecimalCast(Object x)
        {
            return Util.ConvertToDecimal(x);
        }


        static public decimal uncheckedDecimalCast(char x) { return (decimal)x; }

        static public decimal uncheckedDecimalCast(byte x) { return (decimal)x; }

        static public decimal uncheckedDecimalCast(sbyte x) { return (decimal)x; }

        static public decimal uncheckedDecimalCast(short x) { return (decimal)x; }

        static public decimal uncheckedDecimalCast(ushort x) { return (decimal)x; }

        static public decimal uncheckedDecimalCast(int x) { return (decimal)x; }

        static public decimal uncheckedDecimalCast(uint x) { return (decimal)x; }

        static public decimal uncheckedDecimalCast(long x) { return (decimal)x; }

        static public decimal uncheckedDecimalCast(ulong x) { return (decimal)x; }

        static public decimal uncheckedDecimalCast(float x) { return (decimal)x; }

        static public decimal uncheckedDecimalCast(double x) { return (decimal)x; }

        static public decimal uncheckedDecimalCast(decimal x) { return (decimal)x; }

        #endregion

        #region unchecked char casting


        static public char uncheckedCharCast(Object x)
        {
            return x is char c ? c : (char)Util.ConvertToLong(x);
        }


        static public char uncheckedCharCast(char x) { return (char)x; }

        static public char uncheckedCharCast(byte x) { return (char)x; }

        static public char uncheckedCharCast(sbyte x) { return (char)x; }

        static public char uncheckedCharCast(short x) { return (char)x; }

        static public char uncheckedCharCast(ushort x) { return (char)x; }

        static public char uncheckedCharCast(int x) { return (char)x; }

        static public char uncheckedCharCast(uint x) { return (char)x; }

        static public char uncheckedCharCast(long x) { return (char)x; }

        static public char uncheckedCharCast(ulong x) { return (char)x; }

        static public char uncheckedCharCast(float x) { return (char)x; }

        static public char uncheckedCharCast(double x) { return (char)x; }

        static public char uncheckedCharCast(decimal x) { return (char)x; }

        #endregion

        #region IntPtr casting


        static public IntPtr intPtrCast(object x)
        {
            if (x is IntPtr ptr)
                return ptr;
            return IntPtr.Zero;
        }
        #endregion

        #region UIntPtr casting


        static public UIntPtr uintPtrCast(object x)
        {
            if (x is UIntPtr ptr)
                return ptr;
            return UIntPtr.Zero;
        }
        #endregion

        #endregion

        #region  More collection support


        public static IPersistentMap map(params object[] init)
        {
            if (init == null || init.Length == 0)
                return PersistentArrayMap.EMPTY;
            else if (PersistentArrayMap.CanBePAM(init))
                return PersistentArrayMap.createWithCheck(init);
            else
                return PersistentHashMap.createWithCheck(init);
        }


        public static IPersistentMap mapUniqueKeys(params object[] init)
        {
            if (init == null)
                return PersistentArrayMap.EMPTY;
            else if (PersistentArrayMap.CanBePAM(init))
                return new PersistentArrayMap(init);
            return PersistentHashMap.create(init);
        }


        public static IPersistentSet set(params object[] init)
        {
            return PersistentHashSet.createWithCheck(init);
        }


        public static IPersistentVector vector(params object[] init)
        {
            return LazilyPersistentVector.createOwning(init);
        }


        public static IPersistentVector subvec(IPersistentVector v, int start, int end)
        {
            if (start < 0)
                throw new ArgumentOutOfRangeException("start", "cannot be less than zero");

            if (end < start)
                throw new ArgumentOutOfRangeException("end", "cannot be less than start");

            if (end > v.count())
                throw new ArgumentOutOfRangeException("end", "cannot be past the end of the vector");

            if (start == end)
                return PersistentVector.EMPTY;
            return new APersistentVector.SubVector(null, v, start, end);
        }


        #endregion

        #region List support


        public static ISeq list()
        {
            return null;
        }


        public static ISeq list(object arg1)
        {
            return new PersistentList(arg1);
        }


        public static ISeq list(object arg1, object arg2)
        {
            return listStar(arg1, arg2, null);
        }


        public static ISeq list(object arg1, object arg2, object arg3)
        {
            return listStar(arg1, arg2, arg3, null);
        }


        public static ISeq list(object arg1, object arg2, object arg3, object arg4)
        {
            return listStar(arg1, arg2, arg3, arg4, null);
        }


        public static ISeq list(object arg1, object arg2, object arg3, object arg4, object arg5)
        {
            return listStar(arg1, arg2, arg3, arg4, arg5, null);
        }




        public static ISeq listStar(object arg1, ISeq rest)
        {
            return cons(arg1, rest);
        }


        public static ISeq listStar(object arg1, object arg2, ISeq rest)
        {
            return cons(arg1, cons(arg2, rest));
        }


        public static ISeq listStar(object arg1, object arg2, object arg3, ISeq rest)
        {
            return cons(arg1, cons(arg2, cons(arg3, rest)));
        }


        public static ISeq listStar(object arg1, object arg2, object arg3, object arg4, ISeq rest)
        {
            return cons(arg1, cons(arg2, cons(arg3, cons(arg4, rest))));
        }


        public static ISeq listStar(object arg1, object arg2, object arg3, object arg4, object arg5, ISeq rest)
        {
            return cons(arg1, cons(arg2, cons(arg3, cons(arg4, cons(arg5, rest)))));
        }


        public static ISeq arrayToList(object[] items)
        {
            ISeq ret = null;
            for (int i = items.Length - 1; i >= 0; --i)
                ret = (ISeq)cons(items[i], ret);
            return ret;
        }

        public static Object[] object_array(Object sizeOrSeq)
        {
            if (Util.IsNumeric(sizeOrSeq))
                return new Object[Util.ConvertToInt(sizeOrSeq)];
            else
            {
                ISeq s = RT.seq(sizeOrSeq);
                int size = RT.count(s);
                Object[] ret = new Object[size];
                for (int i = 0; i < size && s != null; i++, s = s.next())
                    ret[i] = s.first();
                return ret;
            }
        }


        public static object[] toArray(object coll)
        {
            if (coll == null)
                return EmptyObjectArray;

            if (coll is object[] objArray)
                return objArray;

            // In CLR, ICollection does not have a toArray.  
            // ICollection derives from IEnumerable, so the IEnumerable clause will take care of it.
            //if (coll instanceof Collection)
            //  return ((Collection)coll).toArray();
            //  TODO: List has a toArray -- generic -- need type. 

            if (coll is IEnumerable ie)
                return IEnumToArray(ie);

            // Java has Map here, but IDictionary is IEnumerable, so it will be handled by previous clause.

            if (coll is string s)
            {
                char[] chars = s.ToCharArray();
                // TODO: Determine if we need to make a copy (Java version does, not sure if CLR requires it)
                object[] ret = new object[chars.Length];
                for (int i = 0; i < chars.Length; i++)
                    ret[i] = chars[i];
                return ret;
            }

            // This used to be in the java version.  No longer.  Do we need?
            //else if (coll is ISeq)
            //    return toArray((ISeq)coll);
            //else if (coll is IPersistentCollection)
            //    return toArray(((IPersistentCollection)coll).seq());

            if (coll.GetType().IsArray)
            {
                ISeq iseq = (seq(coll));
                object[] ret = new object[count(iseq)];
                for (int i = 0; i < ret.Length; i++, iseq = iseq.next())
                    ret[i] = iseq.first();
                return ret;
            }

            throw new InvalidOperationException("Unable to convert: " + coll.GetType() + " to Object[]");
        }

        private static object[] IEnumToArray(IEnumerable e)
        {
            return [.. e];
        }

        //private static object[] toArray(ISeq seq)
        //{
        //    object[] array = new object[seq.count()];
        //    int i = 0;
        //    for (ISeq s = seq; s != null; s = s.rest(), i++)
        //        array[i] = s.first();
        //    return array;
        //}

        public static T[] SeqToArray<T>(ISeq x)
        {
            if (x == null)
                return [];

            T[] array = new T[RT.Length(x)];
            int i = 0;
            for (ISeq s = x; s != null; s = s.next(), i++)
                array[i] = (T)s.first();
            return array;
        }



        static public object seqToTypedArray(ISeq seq)
        {
            Type type = (seq != null && seq.first() != null) ? seq.first().GetType() : typeof(Object);
            return seqToTypedArray(type, seq);
        }


        static public object seqToTypedArray(Type type, ISeq seq)
        {
            Array ret = Array.CreateInstance(type, Length(seq));
            if (type == typeof(int))
                for (int i = 0; seq != null; ++i, seq = seq.next())
                    ret.SetValue(intCast(seq.first()), i);
            else if (type == typeof(byte))
                for (int i = 0; seq != null; ++i, seq = seq.next())
                    ret.SetValue(byteCast(seq.first()), i);
            else if (type == typeof(float))
                for (int i = 0; seq != null; ++i, seq = seq.next())
                    ret.SetValue(floatCast(seq.first()), i);
            else if (type == typeof(short))
                for (int i = 0; seq != null; ++i, seq = seq.next())
                    ret.SetValue(shortCast(seq.first()), i);
            else if (type == typeof(char))
                for (int i = 0; seq != null; ++i, seq = seq.next())
                    ret.SetValue(charCast(seq.first()), i);
            else if (type == typeof(sbyte))
                for (int i = 0; seq != null; ++i, seq = seq.next())
                    ret.SetValue(sbyteCast(seq.first()), i);
            else if (type == typeof(uint))
                for (int i = 0; seq != null; ++i, seq = seq.next())
                    ret.SetValue(uintCast(seq.first()), i);
            else if (type == typeof(ushort))
                for (int i = 0; seq != null; ++i, seq = seq.next())
                    ret.SetValue(ushortCast(seq.first()), i);
            else if (type == typeof(ulong))
                for (int i = 0; seq != null; ++i, seq = seq.next())
                    ret.SetValue(ulongCast(seq.first()), i);
            else
                for (int i = 0; seq != null; ++i, seq = seq.next())
                    ret.SetValue(seq.first(), i);
            return ret;
        }

        static public int Length(ISeq list)
        {
            int i = 0;
            for (ISeq c = list; c != null; c = c.next())
                i++;
            return i;
        }

        public static int BoundedLength(ISeq list, int limit)
        {
            int i = 0;
            for (ISeq c = list; c != null && i <= limit; c = c.next())
            {
                i++;
            }
            return i;
        }

        #endregion

        #region Reader support

        static bool ReadTrueFalseDefault(string s, bool def)
        {
            if ("true".Equals(s))
                return true;
            else if ("false".Equals(s))
                return false;
            return def;
        }

        static Object ReadTrueFalseUnknown(String s)
        {
            if (s.Equals("true"))
                return true;
            else if (s.Equals("false"))
                return false;
            return Keyword.intern(null, "unknown");
        }


        public static bool isReduced(Object r)
        {
            return r is Reduced;
        }


        public static bool suppressRead()
        {
            return booleanCast(SuppressReadVar.deref());
        }


        static public string printString(object x)
        {
            using StringWriter sw = new();
            print(x, sw);
            return sw.ToString();
        }


        static public Object readString(String s)
        {
            return readString(s, null);
        }


        static public Object readString(String s, Object opts)
        {
            using PushbackTextReader r = new(new StringReader(s));
            return LispReader.read(r, opts);
        }


        static public void print(Object x, TextWriter w)
        {
            //call multimethod
            if (PrintInitializedVar.isBound && RT.booleanCast(PrintInitializedVar.deref()))
            {
                PrOnVar.invoke(x, w);
                return;
            }

            bool readably = booleanCast(PrintReadablyVar.deref());

            // Print meta, if exists & should be printed
            if (x is Obj)
            {
                Obj o = x as Obj;
                if (RT.count(o.meta()) > 0 &&
                     ((readably && booleanCast(PrintMetaVar.deref()))
                    || booleanCast(PrintDupVar.deref())))
                {
                    IPersistentMap meta = o.meta();
                    w.Write("#^");
                    if (meta.count() == 1 && meta.containsKey(TagKey))
                        print(meta.valAt(TagKey), w);
                    else
                        print(meta, w);
                    w.Write(' ');
                }
            }

            if (x == null)
                w.Write("nil");
            else if (x is ISeq || x is IPersistentList)
            {
                w.Write('(');
                printInnerSeq(seq(x), w);
                w.Write(')');
            }
            else if (x is string)
            {
                string s = x as string;
                if (!readably)
                    w.Write(s);
                else
                {
                    w.Write('"');
                    foreach (char c in s)
                    {
                        switch (c)
                        {
                            case '\n':
                                w.Write("\\n");
                                break;
                            case '\t':
                                w.Write("\\t");
                                break;
                            case '\r':
                                w.Write("\\r");
                                break;
                            case '"':
                                w.Write("\\\"");
                                break;
                            case '\\':
                                w.Write("\\\\");
                                break;
                            case '\f':
                                w.Write("\\f");
                                break;
                            case '\b':
                                w.Write("\\b");
                                break;
                            default:
                                w.Write(c);
                                break;
                        }
                    }
                    w.Write('"');
                }
            }
            else if (x is IPersistentMap)
            {
                w.Write('{');
                for (ISeq s = seq(x); s != null; s = s.next())
                {
                    IMapEntry e = (IMapEntry)s.first();
                    print(e.key(), w);
                    w.Write(' ');
                    print(e.val(), w);
                    if (s.next() != null)
                        w.Write(", ");
                }
                w.Write('}');
            }
            else if (x is IPersistentVector)
            {
                IPersistentVector v = x as IPersistentVector;
                int n = v.count();
                w.Write('[');
                for (int i = 0; i < n; i++)
                {
                    print(v.nth(i), w);
                    if (i < n - 1)
                        w.Write(" ");
                }
                w.Write(']');
            }
            else if (x is IPersistentSet)
            {
                w.Write("#{");
                for (ISeq s = seq(x); s != null; s = s.next())
                {
                    print(s.first(), w);
                    if (s.next() != null)
                        w.Write(" ");
                }
                w.Write('}');
            }
            else if (x is Char)
            {
                char c = (char)x;
                if (!readably)
                    w.Write(c);
                else
                {
                    w.Write('\\');
                    switch (c)
                    {
                        case '\n':
                            w.Write("newline");
                            break;
                        case '\t':
                            w.Write("tab");
                            break;
                        case ' ':
                            w.Write("space");
                            break;
                        case '\b':
                            w.Write("backspace");
                            break;
                        case '\f':
                            w.Write("formfeed");
                            break;
                        case '\r':
                            w.Write("return");
                            break;
                        default:
                            w.Write(c);
                            break;
                    }
                }
            }
            else if (x is Type type)
            {
                string tName = type.AssemblyQualifiedName;
                if (LispReader.NameRequiresEscaping(tName))
                    tName = LispReader.VbarEscape(tName);
                w.Write("#=");
                w.Write(tName);
            }
            else if (x is BigDecimal && readably)
            {
                w.Write(x.ToString());
                w.Write("M");
            }
            else if (x is BigInt && readably)
            {
                w.Write(x.ToString());
                w.Write("N");
            }
            else if (x is BigInteger && readably)
            {
                w.Write(x.ToString());
                w.Write("BIGINT");
            }
            else if (x is Var)
            {
                Var v = x as Var;
                w.Write("#=(var {0}/{1})", v.Namespace.Name, v.Symbol);
            }
            else if (x is Regex r)
            {
                w.Write("#\"{0}\"", r.ToString());
            }
            //else
            //    w.Write(x.ToString());
            // The clause above is what Java has, and would have been nice.
            // Doesn't work for me, for one reason:  
            // When generating initializations for static variables in the classes representing IFns,
            //    let's say the value is the double 7.0.
            //    we generate code that says   (double)RT.readFromString("7")
            //    so we get a boxed int, which CLR won't cast to double.  Sigh.
            //    So I need double/float to print a trailing .0 even when integer-valued.
            else if (x is double || x is float)
            {
                // CLEAT-032: JVM text (Cleat.Core.Runtime.JvmText), always with a '.' or an 'E'
                w.Write(Cleat.Core.Runtime.JvmText.ToStr(x));
            }
            // CLEAT-006: C# 的 bool.ToString() 是 "True"/"False"；Clojure 打印 true/false（ClojureCLR 靠 core_print 的 print-method 覆盖）
            else if (x is bool b)
                w.Write(b ? "true" : "false");
            else
                w.Write(x.ToString());
        }


        private static void printInnerSeq(ISeq x, TextWriter w)
        {
            for (ISeq s = x; s != null; s = s.next())
            {
                print(s.first(), w);
                if (s.next() != null)
                    w.Write(' ');
            }
        }

        public static string PrintToConsole(object x)
        {
            string ret = printString(x);
            Console.WriteLine(ret);
            return ret;
        }



        #endregion

        #region Locating types


        class CaseInsensitiveAssemblyNameComparer : IEqualityComparer<AssemblyName>
        {
            public bool Equals(AssemblyName x, AssemblyName y)
            {
                return string.Equals(x.Name, y.Name, StringComparison.OrdinalIgnoreCase);
            }

            public int GetHashCode(AssemblyName obj)
            {
                return obj.Name.ToLowerInvariant().GetHashCode();
            }
        }


        // Cache for all runtime library assembly names, loaded once on demand.
        // Key is the assembly name.  Value is either the path or empty string.
        // If non-empty can use Assembly.LoadFrom(path) to load.
        // If empty, must use Assembly.Load(name).


        private static readonly Lazy<Dictionary<AssemblyName, string>> _runtimeAssemblyNames = new(() =>
        {
            Dictionary<AssemblyName, string> names = new(new CaseInsensitiveAssemblyNameComparer());

            try
            {
                // DependencyContext.Default can be null in some scenarios (like unit tests or static initializers).
                // Loading the context from a known assembly is more robust.
                var entryAssembly = Assembly.GetEntryAssembly() ?? typeof(RT).Assembly;
                var context = Microsoft.Extensions.DependencyModel.DependencyContext.Load(entryAssembly);
                if (context != null)
                {
                    foreach (var lib in context.RuntimeLibraries)
                    {
                        foreach (var assembly in lib.RuntimeAssemblyGroups.SelectMany(g => g.AssetPaths))
                        {
                            try
                            {
                                var asmName = new AssemblyName(Path.GetFileNameWithoutExtension(assembly));

                                // Resolve to actual file path so Assembly.LoadFrom works later.
                                // Published apps copy all dependency DLLs to the base directory.
                                var fullPath = Path.Combine(AppContext.BaseDirectory, Path.GetFileName(assembly));
                                if (File.Exists(fullPath))
                                    names[asmName] = fullPath;
                                else if (!names.ContainsKey(asmName))
                                    names[asmName] = string.Empty;  // fallback: Assembly.Load will be tried
                            }
                            catch { }
                        }
                    }
                }
            }
            catch { }


#if (NET48_OR_GREATER || !NETFRAMEWORK)
            var trustedAssemblies = AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string;
#else
            var trustedAssemblies = "";
#endif
            if (!string.IsNullOrEmpty(trustedAssemblies))
            {
                var paths = trustedAssemblies.Split(Path.PathSeparator);

                foreach (var path in paths)
                {
                    names[new AssemblyName(Path.GetFileNameWithoutExtension(path))] = path;
                }
            }

            return names;
        });

        internal static readonly char[] _triggerTypeChars = ['`', ',', '[', '&', '*'];

        // cleat-144：deftype / definterface 生成类可以按命名空间前缀在包程序集里决议。默认开；关侧保留旧的
        // “类型必须已在已载入程序集里”的决议路径，用于编译产物对照。
        internal static readonly bool s_crossNsTypes =
            !(Environment.GetEnvironmentVariable("CLEAT_NO_OPT") ?? "").Contains("crossnstype");

        public static Type classForName(string p)
        {
            return classForName(p, CurrentNSVar.deref() as Namespace, true);
        }

        // Leaving in this metering code for now.
        // Remove after we're done debugging.

        public static int NumDuplicates { get; set; } = 0;
        public static int NumDirects { get; set; } = 0;
        public static int NumLoadedAssemblyFinds { get; set; } = 0;
        public static int NumRuntimeAssemblyLoads { get; set; } = 0;
        public static int NumRuntimeAssemblyFinds { get; set; } = 0;
        public static int NumParsing { get; set; } = 0;
        public static int NumFails { get; set; } = 0;

        public static void ClassForNameReport()
        {
            Console.WriteLine($"NumDuplicates:           {NumDuplicates}");
            Console.WriteLine($"NumDirects:              {NumDirects}");
            Console.WriteLine($"NumLoadedAssemblyFinds:  {NumLoadedAssemblyFinds}");
            Console.WriteLine($"NumRuntimeAssemblyLoads: {NumRuntimeAssemblyLoads}");
            Console.WriteLine($"NumRuntimeAssemblyFinds: {NumRuntimeAssemblyFinds}");
            Console.WriteLine($"NumParsing:              {NumParsing}");
            Console.WriteLine($"NumFails:                {NumFails}");
        }

        internal static Type classForName(string p, Namespace ns, bool canCallClrTypeSpec)
        {
            // CLEAT-012: was Compiler.FindDuplicateType(p); module assemblies (boot image, AOT package assemblies, in-memory eval
            // assemblies) register with the Loader and are searched first.
            Type t = Cleat.Core.Runtime.Loader.FindType(p);
            if (t != null)
            {
                NumDuplicates++;
                return t;
            }

            // CLEAT-032: java.lang.IllegalArgumentException etc. (Cleat.Core.Runtime.JvmNames)
            t = Cleat.Core.Runtime.JvmNames.FindFull(p);
            if (t != null)
                return t;

            // Try using Type.GetType which can find types with assembly-qualified names.
            t = Type.GetType(p, false);
            if (t != null)
            {
                NumDirects++;
                return t;
            }

            // Search through currently loaded assemblies.
            AppDomain domain = AppDomain.CurrentDomain;
            Assembly[] loadedAssemblies = domain.GetAssemblies();
            var loadedAssemblyNames = new HashSet<string>(
                loadedAssemblies.Select(a => a.GetName().Name),
                StringComparer.OrdinalIgnoreCase);

            // Search by namespace-qualified name in loaded assemblies.
            foreach (Assembly assy in loadedAssemblies)
            {
                Type t1 = assy.GetType(p, false);
                if (t1 != null)
                {
                    NumLoadedAssemblyFinds++;
                    return t1;
                }
            }


            // Try to load from runtime libraries if we have DependencyContext.
            // We might use the parsed name again, so save it here for use now and below.

            // CLEAT-005: was `var parsedTypename = ClrTypeSpec.Parse(p);` (ClrTypeSpec.cs, interop group, not copied).
            // Minimal stand-in: only the namespace prefix of the raw name is needed below.
            string parsedTypename = p.IndexOfAny(['`', '[', ',']) >= 0 ? p.Substring(0, p.IndexOfAny(['`', '[', ','])) : p;

            if (parsedTypename is not null)
            {
                // Split the type name to identify the namespace and simple name.
                // If we get something like
                //     System.Collections.Concurrent.ConcurrentDictionary`2[System.Object,System.Object]
                // we want to extract System.Collections.Concurrent as the namespace.
                // Worst case: we do a ClrTypeSpec.Parse and extract the namespace from there.
                // Easier, but I won't guarantee it:  move forward as long as we don't run into back characters, then back up to the last dot.

                string assemblyNameString = null;

                // CLEAT-005: was parsedTypename.AssemblyName / parsedTypename.Name.DisplayName (ClrTypeSpec)
                {
                    var baseName = parsedTypename;   // Note: this does not include nested.
                    int lastDot = baseName.LastIndexOf('.');
                    if (lastDot > 0)
                        assemblyNameString = baseName.Substring(0, lastDot);
                    // If there is no dot, no point in moving forward. just leave assemblyNameString == null;].
                }

                if (!string.IsNullOrEmpty(assemblyNameString))
                {
                    var runtimeAssemblyNames = _runtimeAssemblyNames.Value;

                    // Try the full namespace as assembly name, then walk up the hierarchy.
                    // e.g., "Microsoft.AspNetCore.Builder" -> "Microsoft.AspNetCore" -> "Microsoft"
                    string probe = assemblyNameString;
                    while (probe != null)
                    {
                        try
                        {
                            var targetAssemblyName = new AssemblyName(probe);

                            if (runtimeAssemblyNames.TryGetValue(targetAssemblyName, out var path))
                            {
                                if (loadedAssemblyNames.Contains(probe))
                                {
                                    // Already loaded; earlier scan didn't find type. Keep walking.
                                }
                                else
                                {
                                    NumRuntimeAssemblyLoads++;
                                    var assy = string.IsNullOrWhiteSpace(path)
                                        ? Assembly.Load(targetAssemblyName)
                                        : Assembly.LoadFrom(path);
                                    var type = assy.GetType(p, false);
                                    if (type != null)
                                    {
                                        NumRuntimeAssemblyFinds++;
                                        return type;
                                    }
                                    // Assembly loaded but type not found. Continue walking up.
                                }
                            }
                        }
                        catch
                        {
                            // Ignore failures to load this particular assembly. Keep trying.
                        }

                        int lastDot = probe.LastIndexOf('.');
                        probe = lastDot > 0 ? probe.Substring(0, lastDot) : null;
                    }
                }
            }

            // CLEAT-066: public types of the plain assemblies in load path directories (dotnet build output, C# libraries
            // from NuGet packages on -I), like classes on the JVM classpath.
            t = Cleat.Core.Runtime.Loader.FindTypeOnLoadPath(p);
            if (t != null)
                return t;

            // CLEAT-144: a generated type in another package namespace may be used before that package's entry has run.
            // PackageIndex.FindType loads the defining assembly (without running its entry), like a class-path type lookup.
            if (s_crossNsTypes)
            {
                t = Cleat.Core.Runtime.Loader.LoadAssemblyDefining(p);
                if (t != null)
                    return t;
            }

            // Search by simple type name (slow path).
            // At some point this became a no-op unless running on Mono, so I restricted it to that.
            if (IsRunningOnMono)
            {
                List<Type> candidateTypes = [];
                foreach (Assembly assy1 in loadedAssemblies)
                {
                    Type t1 = null;

                    if (!assy1.IsDynamic)
                    {
                        try
                        {
                            foreach (Type tt in assy1.GetTypes())
                            {
                                if (tt.Name.Equals(p))
                                {
                                    t1 = tt;
                                    break;
                                }
                            }
                        }
                        catch (System.Reflection.ReflectionTypeLoadException)
                        {
                        }
                    }


                    if (t1 != null && !candidateTypes.Contains(t1))
                    {
                        candidateTypes.Add(t1);
                    }
                }

                if (candidateTypes.Count == 1)
                {
                    return candidateTypes[0];
                }
            }

            //// Handle generic types and array types.
            //if (p.IndexOfAny(_triggerTypeChars) != -1)
            //{
            //return ClrTypeSpec2.GetTypeFromName(p, CurrentNSVar.deref() as Namespace);
            //}

            // CLEAT-005: removed `ClrTypeSpec.GetTypeFromParsedName(parsedTypename, ns)` fallback (generic/array/ns-alias type
            // specs such as |System.Collections.Generic.List`1[System.String]|); Reflector rewrite (cleat-010) must provide it.
            if (canCallClrTypeSpec && p.IndexOfAny(['`', '[', ',', '+', '&', '*']) >= 0)
            {
                // CLEAT-053: the ClrTypeSpec fallback, via Cleat.Core.Runtime.TypeResolver (cleat-010; it calls back here with
                // canCallClrTypeSpec = false): generic / array / nested / assembly-qualified type specs such as
                // |System.Collections.Concurrent.BlockingCollection`1[System.Object]| (core.clj tapq)
                return Cleat.Core.Runtime.TypeResolver.Resolve(p, Array.Empty<string>());
            }
            //// Handle generic types and array types.
            //if (p.IndexOfAny(_triggerTypeChars) != -1)
            //{
            //    return ClrTypeSpec.GetTypeFromName(p);
            //}

            NumFails++;
            return null;
        }


        public static Type classForNameE(string p)
        {
            Type t = classForName(p);
            return t ?? throw new TypeNotFoundException(p);
        }

        #endregion

        #region Array interface


        public static int alength(Array a)
        {
            return a.Length;
        }


        //public static Array aclone(Array a)
        //{
        //    return (Array)a.Clone();
        //}

        public static object[] aclone(Object[] a)
        {
            return (Object[])a.Clone();
        }

        public static bool[] aclone(bool[] a)
        {
            return (bool[])a.Clone();
        }

        public static byte[] aclone(byte[] a)
        {
            return (byte[])a.Clone();
        }

        public static sbyte[] aclone(sbyte[] a)
        {
            return (sbyte[])a.Clone();
        }

        public static char[] aclone(char[] a)
        {
            return (char[])a.Clone();
        }

        public static short[] aclone(short[] a)
        {
            return (short[])a.Clone();
        }

        public static ushort[] aclone(ushort[] a)
        {
            return (ushort[])a.Clone();
        }

        public static int[] aclone(int[] a)
        {
            return (int[])a.Clone();
        }

        public static uint[] aclone(uint[] a)
        {
            return (uint[])a.Clone();
        }

        public static long[] aclone(long[] a)
        {
            return (long[])a.Clone();
        }

        public static ulong[] aclone(ulong[] a)
        {
            return (ulong[])a.Clone();
        }

        public static float[] aclone(float[] a)
        {
            return (float[])a.Clone();
        }

        public static double[] aclone(double[] a)
        {
            return (double[])a.Clone();
        }

        public static decimal[] aclone(decimal[] a)
        {
            return (decimal[])a.Clone();
        }




        public static object aget(Array a, int idx)
        {
            return a.GetValue(idx);
        }


        // Trying to avoid reflection in clojure.core aget inline.  Type-hinting didn't seem to do the trick.

        public static object agetOnArray(Array a, int idx)
        {
            return a.GetValue(idx);
        }

        //public static object aset(Array a, int idx, object val)
        //{
        //    a.SetValue(val, idx);
        //    return val;
        //}



        // overloads for array getters/setters


        public static object aget(Object[] xs, int i)
        {
            return xs[i];
        }


        public static object aset(Object[] xs, int i, object v)
        {
            xs[i] = v;
            return v;
        }


        public static bool aget(bool[] xs, int i)
        {
            return xs[i];
        }


        public static bool aset(bool[] xs, int i, bool v)
        {
            xs[i] = v;
            return v;
        }


        public static char aget(char[] xs, int i)
        {
            return xs[i];
        }


        public static char aset(char[] xs, int i, char v)
        {
            xs[i] = v;
            return v;
        }


        public static byte aget(byte[] xs, int i)
        {
            return xs[i];
        }


        public static byte aset(byte[] xs, int i, byte v)
        {
            xs[i] = v;
            return v;
        }


        public static sbyte aget(sbyte[] xs, int i)
        {
            return xs[i];
        }


        public static sbyte aset(sbyte[] xs, int i, sbyte v)
        {
            xs[i] = v;
            return v;
        }


        public static short aget(short[] xs, int i)
        {
            return xs[i];
        }


        public static short aset(short[] xs, int i, short v)
        {
            xs[i] = v;
            return v;
        }


        public static ushort aget(ushort[] xs, int i)
        {
            return xs[i];
        }


        public static ushort aset(ushort[] xs, int i, ushort v)
        {
            xs[i] = v;
            return v;
        }


        public static int aget(int[] xs, int i)
        {
            return xs[i];
        }


        public static int aset(int[] xs, int i, int v)
        {
            xs[i] = v;
            return v;
        }


        public static uint aget(uint[] xs, int i)
        {
            return xs[i];
        }


        public static uint aset(uint[] xs, int i, uint v)
        {
            xs[i] = v;
            return v;
        }


        public static long aget(long[] xs, int i)
        {
            return xs[i];
        }


        public static long aset(long[] xs, int i, long v)
        {
            xs[i] = v;
            return v;
        }


        public static ulong aget(ulong[] xs, int i)
        {
            return xs[i];
        }


        public static ulong aset(ulong[] xs, int i, ulong v)
        {
            xs[i] = v;
            return v;
        }


        public static float aget(float[] xs, int i)
        {
            return xs[i];
        }


        public static float aset(float[] xs, int i, float v)
        {
            xs[i] = v;
            return v;
        }


        public static double aget(double[] xs, int i)
        {
            return xs[i];
        }


        public static double aset(double[] xs, int i, double v)
        {
            xs[i] = v;
            return v;
        }


        public static decimal aget(decimal[] xs, int i)
        {
            return xs[i];
        }


        public static decimal aset(decimal[] xs, int i, decimal v)
        {
            xs[i] = v;
            return v;
        }

        #endregion

        #region Things not in the Java version

        [Serializable]
        class DefaultComparer : IComparer, ISerializable
        {
            #region IComparer Members

            public int Compare(object x, object y)
            {
                return Util.compare(x, y);  // was ((IComparable)x).CompareTo(y); -- changed in Java rev 1145
            }

            #endregion

            #region core.clj compatibility

            //  Somewhere, there is an explicit call to compare
            public int compare(object x, object y)
            {
                return Util.compare(x, y);  // was ((IComparable)x).CompareTo(y);-- changed in Java rev 1145
            }

            #endregion

            #region ISerializable Members

            [System.Security.SecurityCritical]
            public void GetObjectData(SerializationInfo info, StreamingContext context)
            {
                info.SetType(typeof(DefaultComparerSerializationHelper));
            }

            [Serializable]
            class DefaultComparerSerializationHelper : IObjectReference
            {
                #region IObjectReference Members

                public object GetRealObject(StreamingContext context)
                {
                    return DefaultComparerInstance;
                }

                #endregion
            }


            #endregion
        }

        static public readonly IComparer DefaultComparerInstance = new DefaultComparer();


        // do we need this?
        //static Boolean HasTag(object o, object tag)
        //{
        //    return Util.equals(tag,,RT.get(RT.meta(o),TAG_KEY);
        //}


        public static long nanoTime()
        {
            return DateTime.Now.Ticks * 100;
        }

        private static readonly Stopwatch _stopwatch = new();

        public static object StartStopwatch()
        {
            _stopwatch.Reset();
            _stopwatch.Start();
            return null;
        }

        public static double StopStopwatch()
        {
            _stopwatch.Stop();
            return _stopwatch.Elapsed.TotalMilliseconds;
        }



        // In core.clj, we see (cast Number x)  in a number of numeric methods.
        // There is no Number wrapper here.
        // The intent is:  if x is not a numeric value, throw a ClassCastException (Java),
        //                 else return x
        // And here it is:
        public static object NumberCast(object x)
        {
            if (!Util.IsNumeric(x))
                throw new InvalidCastException("Expected a number");
            return x;
        }

        // The Java guys use Class.cast to do casting.
        // We don't have that.
        // Perhaps this will work.
        // NOPE!
        //public static object Cast(Type t, object o)
        //{
        //    return Type.DefaultBinder.ChangeType(o, t, null);
        //}


        // The Java version goes through Array.sort to do this,
        // but I don't have a way to pass a comparator.

        class ComparerConverter : IComparer, IComparer<object>  // CLEAT-032: also generic, for the stable OrderBy
        {
            readonly IFn _fn;

            public ComparerConverter(IFn fn)
            {
                _fn = fn;
            }

            #region IComparer Members

            public int Compare(object x, object y)
            {
                // CLEAT-032: a boolean "less-than" fn works as a comparator (like AFunction.Compare / AFunction.compare on the JVM)
                object o = _fn.invoke(x, y);
                if (o is bool lt)
                {
                    if (lt)
                        return -1;
                    return RT.booleanCast(_fn.invoke(y, x)) ? 1 : 0;
                }
                return Util.ConvertToInt(o);
            }

            #endregion
        }

        public static void SortArray(Array a, IFn fn)
        {
            // CLEAT-032: sort is guaranteed stable (Array.Sort is not); comparator exceptions propagate unwrapped
            object[] sorted = a.Cast<object>().OrderBy(x => x, new ComparerConverter(fn)).ToArray();
            Array.Copy(sorted, a, sorted.Length);
        }



        static readonly Random _random = new();


        public static double random()
        {
            lock (_random)
            {
                return _random.NextDouble();
            }
        }


        public static string CultureToString(object x)
        {
            // CLEAT-032: doubles, floats and booleans read like the JVM's toString ("1.0", "1.0E100", "Infinity", "true")
            string jvm = Cleat.Core.Runtime.JvmText.ToStr(x);
            if (jvm != null)
                return jvm;
            // cleat-146：整数是 (str) 的热形状。直接不变区域性格式化，避开 IsNumeric 的类型检查与
            // 复合格式解析；long / int 的十进制文本与 String.Format("{0}") 完全一致。
            if (s_internEnabled)
            {
                if (x is long l)
                    return l.ToString(System.Globalization.CultureInfo.InvariantCulture);
                if (x is int i)
                    return i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
            if (Util.IsNumeric(x))
                return String.Format(System.Globalization.CultureInfo.InvariantCulture, "{0}", x);
            else
                return x.ToString();
        }


        // Surprisingly hard, due to non-BMP (multiple character) codes.
        // This solution from http://stackoverflow.com/questions/228038/best-way-to-reverse-a-string-in-c-2-0
        public static string StringReverse(string input)
        {
            if (input == null)
                throw new ArgumentNullException("input");

            // allocate a buffer to hold the output
            char[] output = new char[input.Length];
            for (int outputIndex = 0, inputIndex = input.Length - 1; outputIndex < input.Length; outputIndex++, inputIndex--)
            {
                // check for surrogate pair
                if (input[inputIndex] >= 0xDC00 && input[inputIndex] <= 0xDFFF &&
                        inputIndex > 0 && input[inputIndex - 1] >= 0xD800 && input[inputIndex - 1] <= 0xDBFF)
                {
                    // preserve the order of the surrogate pair code units
                    output[outputIndex + 1] = input[inputIndex];
                    output[outputIndex] = input[inputIndex - 1];
                    outputIndex++;
                    inputIndex--;
                }
                else
                {
                    output[outputIndex] = input[inputIndex];
                }
            }

            return new string(output);
        }

        private static readonly bool _isRunningOnMono = Type.GetType("Mono.Runtime") != null;

        public static bool IsRunningOnMono { get { return _isRunningOnMono; } }

        #endregion

        #region Loading/compiling


        public static void load(String relativePath)
        {
            load(relativePath, true);
        }

        // CLEAT-012: the search (package assemblies, source, embedded resources) moved to Cleat.Core.Runtime.Loader;
        // the compile/eval half goes through RuntimeHooks (load-source / compile).
        public static void load(String relativePath, Boolean failIfNotFound)
        {
            Cleat.Core.Runtime.Loader.TryLoad(relativePath, failIfNotFound);
        }

        public static void LoadCljScript(string cljname)
        {
            LoadCljScript(cljname, true);
        }

        public static void LoadCljScript(string cljname, bool failIfNotFound)
        {
            FileInfo cljInfo = FindFile(cljname);
            if (cljInfo != null)
                LoadScript(cljInfo, cljname);
            else if (failIfNotFound)
                throw new FileNotFoundException(String.Format("Could not locate Clojure resource {0} on {1}", cljname, ClojureLoadPathString));
        }

        public static void LoadScript(FileInfo cljInfo, string relativePath)
        {
            // CLEAT-012: was Compiler.load(rdr, fullName, name, relativePath)
            Cleat.Core.Runtime.Loader.LoadFile(cljInfo.FullName, relativePath);
        }

        static FileInfo FindFile(string path, string filename)
        {
            string probePath = ConvertPath(Path.Combine(path, filename));
            if (File.Exists(probePath))
                return new FileInfo(probePath);

            return null;
        }

        public static IEnumerable<string> GetFindFilePaths()
        {
            return GetFindFilePathsRaw().Where(p => !String.IsNullOrEmpty(p)).Distinct();
        }

        static IEnumerable<string> GetFindFilePathsRaw()
        {
            // CLEAT-012: the Loader's path (CLEAT_LOAD_PATH, -I) comes first
            foreach (string path in Cleat.Core.Runtime.Loader.LoadPath.ToArray())
                yield return path;

            yield return System.AppDomain.CurrentDomain.BaseDirectory;
            yield return Path.Combine(System.AppDomain.CurrentDomain.BaseDirectory, "bin");
            yield return Directory.GetCurrentDirectory();
            yield return Path.GetDirectoryName(typeof(RT).Assembly.Location);

            Assembly assy = Assembly.GetEntryAssembly();
            if (assy != null)
                yield return Path.GetDirectoryName(assy.Location);

            string rawpaths = (string)System.Environment.GetEnvironmentVariable(ClojureLoadPathString);
            if (rawpaths == null)
                yield break;

            string[] paths = rawpaths.Split(Path.PathSeparator);
            foreach (string path in paths)
                yield return path;
        }


        public static FileInfo FindFile(string fileName)
        {
            FileInfo fi;

            foreach (string path in GetFindFilePaths())
            {
                fi = FindFile(path, fileName);
                if (fi is not null) return fi;
            }

            return FindRemappedFile(fileName);
        }

        public static readonly Var NSLoadMappings
            = Var.intern(Namespace.findOrCreate(Symbol.intern("clojure.core")),
                                                 Symbol.intern("*ns-load-mappings*"), new Atom(PersistentVector.EMPTY)).setDynamic();

        public static FileInfo FindRemappedFile(string fileName)
        {
            if (!(NSLoadMappings.deref() is Atom nsLoadMappings)) return null;

            var nsLoadMappingsVal = nsLoadMappings.deref() as PersistentVector;
            foreach (var x in nsLoadMappingsVal)
            {
                if (!(x is PersistentVector mapping) || mapping.length() < 2) continue;
                if (!(mapping[0] is string nsRoot)) continue;
                nsRoot = nsRoot.Replace('.', '/');
                if (fileName.StartsWith(nsRoot, StringComparison.Ordinal))
                {
                    var fsRoot = mapping[1] as string;
                    var probePath = ConvertPath(fsRoot) + ConvertPath(fileName.Substring(nsRoot.Length));
                    if (File.Exists(probePath))
                        return new FileInfo(probePath);
                }
            }
            return null;
        }

        public static IEnumerable<FileInfo> FindFiles(string fileName)
        {
            FileInfo fi;

            foreach (string path in GetFindFilePaths())
                if ((fi = FindFile(path, fileName)) != null)
                    yield return fi;
        }

        static string ConvertPath(string path)
        {
            return path.Replace('/', Path.DirectorySeparatorChar);
        }

        internal static Stream GetEmbeddedResourceStream(string resourceName, out Assembly containingAssembly)  // CLEAT-012: internal (Loader)
        {
            containingAssembly = null;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (!asm.IsDynamic)
                {
                    try
                    {
                        var stream = asm.GetManifestResourceStream(resourceName);
                        if (stream != null)
                        {
                            containingAssembly = asm;
                            return stream;
                        }
                    }
                    catch (ArgumentException)
                    {
                    }
                    catch (IOException)
                    {
                    }
                    catch (NotImplementedException)
                    {
                    }
                    catch (BadImageFormatException)
                    {
                    }
                    catch (NotSupportedException)
                    {
                        // Hopefully only dynamic assemblies
                        // We catch them above because we know dynamic assemblies do not support this
                    }
                }
            }
            return null;
        }

        static byte[] ReadStreamBytes(Stream stream)
        {
            try
            {
                var len = stream.Length;
                var data = new byte[len];
                int numRead = stream.Read(data, 0, (int)len);
                if (numRead < len)
                {
                    throw new EndOfStreamException("Could not read entire stream");
                }

                return data;
            }
            finally
            {
                stream.Dispose();
            }
        }

        // duck typing stderr plays nice with e.g. swank 

        public static TextWriter errPrintWriter()
        {
            object w = ErrVar.deref();

            if (w is TextWriter tw)
                return tw;

            if (w is Stream s)
                return new StreamWriter(s);

            throw new ArgumentException("Unknown type for *err*");
        }

#pragma warning restore IDE1006 // Naming Styles
        #endregion

        #region Completely misc

        // I needed a ULP (unit of least precision) to implement in some tests.
        // JVM has this in java.lang.Math.  CLR's System.Math does not have this method.
        // So I'm defining it here.  Variations of this code all over StackOverflow.

        public static double Ulp(double x)
        {
            if (Double.IsNaN(x))
                return double.NaN;
            else if (Double.IsInfinity(x))
                return double.PositiveInfinity;
            else if (x == 0.0 || x == -0.0)
                return double.Epsilon;
            else
            {
                double absX = Math.Abs(x);
                var bits = BitConverter.DoubleToInt64Bits(absX);
                if (absX == Double.MaxValue)
                    return BitConverter.Int64BitsToDouble(bits) - BitConverter.Int64BitsToDouble(bits - 1);
                double nextValue = BitConverter.Int64BitsToDouble(bits + 1);
                return nextValue - absX;
            }
        }

        // I needed a way to remove empty strings/nulls from the end of an array.
        public static string[] TrimTrailingEmptyStrings(string[] arr)
        {
            int i = arr.Length - 1;
            while (i >= 0)
            {
                if (!String.IsNullOrEmpty(arr[i]))
                    break;
                i--;
            }
            Array.Resize(ref arr, i + 1);
            return arr;
        }

        #endregion
    }

    public static class RuntimeBootstrapFlag
    {
        public static bool _doRTBootstrap = true;

        /// <summary>
        /// Disable file loading
        /// </summary>
        /// <remarks>Prevent the load method from searching the file system for .clj and .clj.dll files.  
        /// Used in production systems when all namespaces are to be found in loaded assemblies.</remarks>
        public static bool DisableFileLoad = false;
    }

}
