/**
 *   Copyright (c) Rich Hickey. All rights reserved.
 *   The use and distribution terms for this software are covered by the
 *   Eclipse Public License 1.0 (http://opensource.org/licenses/eclipse-1.0.php)
 *   which can be found in the file epl-v10.html at the root of this distribution.
 *   By using this software in any fashion, you are agreeing to be bound by
 * 	 the terms of this license.
 *   You must not remove this notice, or any other, from this software.
 **/

// CLEAT-005: new file. Name munging (Compiler.CHAR_MAP / DEMUNGE_MAP / DEMUNGE_PATTERN / munge / demunge)
// and the primitive-type name table (Compiler._primTypeNamesMap / TryPrimTypeToName) are copied verbatim out of
// CljCompiler/Compiler.cs so that ArityException and Util no longer reference the compiler.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace clojure.lang
{
    public static class Munge
    {
        #region Name munging (from Compiler.cs)

        static readonly IPersistentMap _charMap = PersistentHashMap.create('-', "_",
             //		                         '.', "_DOT_",
             ':', "_COLON_",
             '+', "_PLUS_",
             '>', "_GT_",
             '<', "_LT_",
             '=', "_EQ_",
             '~', "_TILDE_",
             '!', "_BANG_",
             '@', "_CIRCA_",
             '#', "_SHARP_",
             '\'', "_SINGLEQUOTE_",
             '"', "_DOUBLEQUOTE_",
             '%', "_PERCENT_",
             '^', "_CARET_",
             '&', "_AMPERSAND_",
             '*', "_STAR_",
             '|', "_BAR_",
             '{', "_LBRACE_",
             '}', "_RBRACE_",
             '[', "_LBRACK_",
             ']', "_RBRACK_",
             '/', "_SLASH_",
             '\\', "_BSLASH_",
             '?', "_QMARK_"
             );

        public static IPersistentMap CHAR_MAP { get { return _charMap; } }

        static readonly public IPersistentMap DEMUNGE_MAP = CreateDemungeMap();

        private static IPersistentMap CreateDemungeMap()
        {
            IPersistentMap m = RT.map("$", '/');
            for (ISeq s = RT.seq(CHAR_MAP); s != null; s = s.next())
            {
                IMapEntry e = (IMapEntry)s.first();
                Char origch = (Char)e.key();
                String escapeStr = (String)e.val();
                m = m.assoc(escapeStr, origch);
            }
            return m;
        }

        private class LengthCmp : IComparer
        {
            public int Compare(object x, object y)
            {
                return ((String)y).Length - ((String)x).Length;
            }
        }

        static readonly public Regex DEMUNGE_PATTERN = CreateDemungePattern();

        private static Regex CreateDemungePattern()
        {
            object[] mungeStrs = RT.toArray(RT.keys(DEMUNGE_MAP));
            Array.Sort(mungeStrs, new LengthCmp());
            StringBuilder sb = new();
            bool first = true;
            foreach (Object s in mungeStrs)
            {
                String escapeStr = (String)s;
                if (!first)
                    sb.Append("|");
                first = false;
                sb.Append(Regex.Escape(escapeStr));
            }
            return new Regex(sb.ToString());
        }

        public static string munge(string name)
        {
            StringBuilder sb = new();
            foreach (char c in name)
            {
                string sub = (string)CHAR_MAP.valAt(c);
                if (sub == null)
                    sb.Append(c);
                else
                    sb.Append(sub);
            }
            return sb.ToString();
        }

        public static String demunge(string mungedNamed)
        {
            StringBuilder sb = new();
            int lastMatchEnd = 0;
            for (Match m = DEMUNGE_PATTERN.Match(mungedNamed); m.Success; m = m.NextMatch())
            {
                int start = m.Index;
                sb.Append(mungedNamed.Substring(lastMatchEnd, start - lastMatchEnd));
                lastMatchEnd = start + m.Length;
                Char origCh = (Char)DEMUNGE_MAP.valAt(m.Groups[0].Value);
                sb.Append(origCh);
            }
            sb.Append(mungedNamed.Substring(lastMatchEnd));
            return sb.ToString();
        }

        #endregion

        #region Primitive type names (from Compiler.cs)

        static readonly Dictionary<Type, string> _primTypeNamesMap = new()
        {
            { typeof(int), "int" },
            { typeof(long), "long" },
            { typeof(float), "float" },
            { typeof(double), "double" },
            { typeof(char), "char" },
            { typeof(short), "short" },
            { typeof(byte), "byte" },
            { typeof(bool), "bool" },
            { typeof(uint), "uint" },
            { typeof(ulong), "ulong" },
            { typeof(ushort), "ushort" },
            { typeof(sbyte), "sbyte" },
            { typeof(void), "void" }
        };

        public static bool TryPrimTypeToName(Type type, out string name) => _primTypeNamesMap.TryGetValue(type, out name);

        #endregion
    }
}
