using System.Text;
using System.Text.RegularExpressions;

namespace SamModern.Core
{
    /// <summary>
    /// Node of a Valve text file (VDF), e.g. loginusers.vdf, libraryfolders.vdf, localconfig.vdf. A node is either a
    /// block (<see cref="Value"/> is null, children below it) or a plain key/value pair. Keys are case-insensitive.
    /// </summary>
    public sealed class VdfNode
    {
        public string Name;
        public string Value;
        public readonly Dictionary<string, VdfNode> Children = new(StringComparer.OrdinalIgnoreCase);

        public bool IsBlock => Value == null;

        /// <summary>Direct child by key, or null.</summary>
        public VdfNode this[string key] => Children.TryGetValue(key, out var n) ? n : null;

        /// <summary>Text value of a direct child, or <paramref name="fallback"/> if the key is missing.</summary>
        public string Str(string key, string fallback = null) => this[key]?.Value ?? fallback;

        /// <summary>Path separated by '/', case-insensitive.</summary>
        public VdfNode Path(string path)
        {
            var node = this;
            foreach (var part in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                node = node?[part];
                if (node == null) return null;
            }
            return node;
        }
    }

    /// <summary>
    /// A small, forgiving parser for Valve's text KeyValues format:
    /// <code>
    /// "key" "value"
    /// "block" { "key" "value" ... }
    /// </code>
    /// Handles quoted and unquoted tokens, backslash escapes, // comments and a BOM. It never throws on malformed input;
    /// it returns what it understood (callers treat missing keys as "not there").
    /// </summary>
    public static class Vdf
    {
        /// <summary>Parses a file Steam may be writing at the same moment (shared read). Returns null if it cannot be read.</summary>
        public static VdfNode ParseFile(string path)
        {
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(fs, new UTF8Encoding(false), true);
                return Parse(reader.ReadToEnd());
            }
            catch (Exception)
            {
                return null;
            }
        }

        public static VdfNode Parse(string text)
        {
            var root = new VdfNode { Name = "<root>" };
            int pos = 0;
            ParseBlock(text, ref pos, root, true);
            return root;
        }

        /// <summary>Reads "key value" and "key { ... }" entries into <paramref name="parent"/> until the closing brace.</summary>
        private static void ParseBlock(string s, ref int pos, VdfNode parent, bool isRoot)
        {
            while (true)
            {
                var key = NextToken(s, ref pos, out var kind);
                if (kind == Tok.End)
                {
                    return;
                }
                if (kind == Tok.Close)
                {
                    if (isRoot) continue;
                    return;
                }
                if (kind == Tok.Open)
                {
                    continue;
                }

                var next = NextToken(s, ref pos, out var nextKind);
                if (nextKind == Tok.Open)
                {
                    var child = new VdfNode { Name = key };
                    ParseBlock(s, ref pos, child, false);
                    parent.Children[key] = child;
                }
                else if (nextKind == Tok.Text)
                {
                    parent.Children[key] = new VdfNode { Name = key, Value = next };
                }
                else
                {
                    return;
                }
            }
        }

        private enum Tok { Text, Open, Close, End }

        /// <summary>Returns the next token: a text (quoted or bare), a brace, or the end of the input.</summary>
        private static string NextToken(string s, ref int pos, out Tok kind)
        {
            while (pos < s.Length)
            {
                char c = s[pos];
                if (char.IsWhiteSpace(c) || c == '\uFEFF')
                {
                    pos++;
                    continue;
                }
                if (c == '/' && pos + 1 < s.Length && s[pos + 1] == '/')
                {
                    while (pos < s.Length && s[pos] != '\n') pos++;
                    continue;
                }
                break;
            }

            if (pos >= s.Length)
            {
                kind = Tok.End;
                return null;
            }

            char ch = s[pos];
            if (ch == '{') { pos++; kind = Tok.Open; return null; }
            if (ch == '}') { pos++; kind = Tok.Close; return null; }

            var sb = new StringBuilder();
            if (ch == '"')
            {
                pos++;
                while (pos < s.Length && s[pos] != '"')
                {
                    if (s[pos] == '\\' && pos + 1 < s.Length)
                    {
                        pos++;
                        sb.Append(s[pos] switch { 'n' => '\n', 't' => '\t', '\\' => '\\', '"' => '"', var other => other });
                    }
                    else
                    {
                        sb.Append(s[pos]);
                    }
                    pos++;
                }
                pos++; // closing quote
            }
            else
            {
                while (pos < s.Length && char.IsWhiteSpace(s[pos]) == false && s[pos] != '{' && s[pos] != '}')
                {
                    sb.Append(s[pos++]);
                }
            }

            kind = Tok.Text;
            return sb.ToString();
        }

        /// <summary>
        /// Replaces a single value inside a block of the raw file without touching the rest of the file
        /// (formatting, comments and unknown keys stay byte-for-byte as Steam wrote them - which is why this edits the
        /// text instead of parsing and re-serialising the whole file).
        /// Returns false if the block or key does not exist.
        /// </summary>
        public static bool TrySetValueInBlock(ref string text, string blockKey, string valueKey, string newValue)
        {
            var blockMatch = Regex.Match(text, "\"" + Regex.Escape(blockKey) + "\"\\s*\\{", RegexOptions.IgnoreCase);
            if (blockMatch.Success == false) return false;

            int start = blockMatch.Index + blockMatch.Length;
            int depth = 1, i = start;
            while (i < text.Length && depth > 0)
            {
                if (text[i] == '{') depth++;
                else if (text[i] == '}') depth--;
                i++;
            }
            if (depth != 0) return false;

            string block = text.Substring(start, i - start);
            var valueRegex = new Regex("(\"" + Regex.Escape(valueKey) + "\"\\s+\")([^\"]*)(\")", RegexOptions.IgnoreCase);
            if (valueRegex.IsMatch(block) == false) return false;

            string newBlock = valueRegex.Replace(block, m => m.Groups[1].Value + newValue + m.Groups[3].Value, 1);
            text = text.Substring(0, start) + newBlock + text.Substring(i);
            return true;
        }
    }
}
