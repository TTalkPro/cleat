/**
 *   Copyright (c) Rich Hickey. All rights reserved.
 *   The use and distribution terms for this software are covered by the
 *   Eclipse Public License 1.0 (http://opensource.org/licenses/eclipse-1.0.php)
 *   which can be found in the file epl-v10.html at the root of this distribution.
 *   By using this software in any fashion, you are agreeing to be bound by
 * 	 the terms of this license.
 *   You must not remove this notice, or any other, from this software.
 **/

// CLEAT-005: new file. Replaces LispReader.Resolver (JVM LispReader.Resolver) and the reader's direct calls into Compiler
// (CurrentNamespace, resolveSymbol, IsSpecial). The expander/compiler binds an implementation in *reader-resolver*;
// when nothing is bound, DefaultReaderResolver reproduces Compiler.resolveSymbol's namespace-mapping rules.

using System;

namespace clojure.lang
{
    public interface IReaderResolver
    {
        Symbol CurrentNS();
        Symbol ResolveClass(Symbol sym);
        Symbol ResolveAlias(Symbol sym);
        Symbol ResolveVar(Symbol sym);
        /// <summary>True for special-form symbols, which syntax-quote must not namespace-qualify. Was Compiler.IsSpecial.</summary>
        bool IsSpecial(object sym);
    }

    /// <summary>
    /// Resolver used when *reader-resolver* is unbound: resolves against *ns* (RT.CurrentNSVar) mappings and aliases,
    /// exactly as Compiler.resolveSymbol / Compiler.namespaceFor did, minus array-type symbols (HostExpr.MaybeArrayType).
    /// </summary>
    public sealed class DefaultReaderResolver : IReaderResolver
    {
        public static readonly DefaultReaderResolver Instance = new();

        // CLEAT-005: the special-form table lives in the compiler (Compiler.specials); the spike hard-codes the JVM/CLR list.
        static readonly IPersistentSet _specials = PersistentHashSet.create(
            Symbol.intern("def"), Symbol.intern("loop*"), Symbol.intern("recur"), Symbol.intern("if"), Symbol.intern("case*"),
            Symbol.intern("let*"), Symbol.intern("letfn*"), Symbol.intern("do"), Symbol.intern("fn*"), Symbol.intern("quote"),
            Symbol.intern("var"), Symbol.intern("clojure.core", "import*"), Symbol.intern("."), Symbol.intern("set!"),
            Symbol.intern("deftype*"), Symbol.intern("reify*"), Symbol.intern("proxy-class*"), Symbol.intern("gen-class*"), Symbol.intern("await*"), Symbol.intern("syntax-case*"), Symbol.intern("syntax*"), Symbol.intern("try"), Symbol.intern("throw"),
            Symbol.intern("monitor-enter"), Symbol.intern("monitor-exit"), Symbol.intern("catch"), Symbol.intern("finally"),
            Symbol.intern("new"), Symbol.intern("&"));

        static Namespace CurrentNamespace => (Namespace)RT.CurrentNSVar.deref();

        public bool IsSpecial(object sym) => _specials.contains(sym);

        public Symbol CurrentNS() => CurrentNamespace.Name;

        public Symbol ResolveClass(Symbol sym)
        {
            if (sym.Namespace != null)
                return null;
            return CurrentNamespace.GetMapping(sym) is Type t ? Symbol.intern(null, t.FullName) : null;
        }

        public Symbol ResolveAlias(Symbol sym)
        {
            Namespace ns = CurrentNamespace.LookupAlias(sym) ?? Namespace.find(sym);
            return ns?.Name;
        }

        public Symbol ResolveVar(Symbol sym)
        {
            if (sym.Namespace != null)
                return null;
            return CurrentNamespace.GetMapping(sym) is Var v ? Symbol.intern(v.Namespace.Name.Name, v.Symbol.Name) : null;
        }
    }
}
