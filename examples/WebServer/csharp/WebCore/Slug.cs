using System.Text;

namespace WebCore;

/// <summary>A C# helper the Clojure code calls as <c>(WebCore.Slug/Make title)</c>.</summary>
public static class Slug
{
    /// <summary>
    /// Lower-case ASCII letters and digits, words joined by '-'. (No Unicode normalization: the cleat
    /// host runs with invariant globalization, where string.Normalize does not decompose, and the
    /// result should be the same at the REPL and in the web host.)
    /// </summary>
    public static string Make(string text)
    {
        var sb = new StringBuilder();
        bool dash = false;
        foreach (char c in text)
        {
            if (char.IsAsciiLetterOrDigit(c))
            {
                if (dash && sb.Length > 0) sb.Append('-');
                sb.Append(char.ToLowerInvariant(c));
                dash = false;
            }
            else
            {
                dash = true;
            }
        }
        return sb.ToString();
    }
}
