using System.Text;

namespace Pez.Sim
{
    /// <summary>
    /// Cleans player-supplied text (names, chat) before it reaches other players, who may be LLMs.
    /// Strips control and bidi-override characters and caps the length. Chat is additionally labelled as
    /// untrusted wherever it's shown to an agent, so it can't pose as instructions.
    /// </summary>
    public static class Text
    {
        public static string Clean(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new StringBuilder(System.Math.Min(s.Length, max));
            foreach (var c in s)
            {
                if (sb.Length >= max) break;
                if (char.IsControl(c)) { if (c == '\n' || c == '\t') sb.Append(' '); continue; }
                if (c >= '\u202A' && c <= '\u202E' || c >= '\u2066' && c <= '\u2069' || c == '\u200B' || c == '\uFEFF') continue; // bidi overrides, zero-width
                sb.Append(c);
            }
            return sb.ToString().Trim();
        }

        /// <summary>Player names: letters, digits, space and a little punctuation, at most 24 characters.</summary>
        public static string Name(string s)
        {
            var sb = new StringBuilder();
            foreach (var c in Clean(s, 64))
                if (char.IsLetterOrDigit(c) || c == ' ' || c == '-' || c == '_' || c == '.') sb.Append(c);
            var n = sb.ToString().Trim();
            if (n.Length > 24) n = n.Substring(0, 24).Trim();
            return n.Length == 0 ? "Player" : n;
        }
    }
}
