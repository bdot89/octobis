using System.Globalization;
using System.Text;

namespace OctoBis.Scraper;

/// <summary>
/// A reader for the subset of Lua the Atlas-CFM data files use.
///
/// These files are addon source, not a data format: they contain comments, local variables, table
/// constructors mixing array items with named keys, expressions like <c>LZ["Blackwing Lair"]</c>
/// and <c>L["Wrist"] .. "/ " .. L["Waist"]</c>, and <c>unpack(sharedTable)</c> splices. Running
/// them would need a Lua interpreter; reading them needs only this.
///
/// Anything not recognised as a literal is captured verbatim as a <see cref="LuaExpression"/> so a
/// construct we do not model cannot break the surrounding table.
/// </summary>
public static class LuaTable
{
    /// <summary>An unevaluated expression, e.g. <c>LZ["Molten Core"]</c>.</summary>
    public sealed record LuaExpression(string Text)
    {
        /// <summary>
        /// Pulls the readable text out of a localisation lookup. The English string is the lookup
        /// key itself, so <c>LB["Nefarian"]</c> yields "Nefarian" without needing the locale files.
        /// Concatenations keep every quoted part, so <c>L["Wrist"] .. "/ " .. L["Waist"]</c>
        /// becomes "Wrist/ Waist".
        /// </summary>
        public string? AsText()
        {
            var parts = new List<string>();
            var i = 0;
            while (i < Text.Length)
            {
                if (Text[i] is '"' or '\'')
                {
                    parts.Add(ReadString(Text, ref i));
                    continue;
                }
                i++;
            }
            return parts.Count > 0 ? string.Concat(parts) : null;
        }
    }

    public sealed class Table
    {
        /// <summary>Positional entries, in source order.</summary>
        public List<object?> Array { get; } = new();

        /// <summary>Named entries.</summary>
        public Dictionary<string, object?> Fields { get; } = new(StringComparer.Ordinal);

        /// <summary>
        /// The trailing line comment that followed this table in the source. The Atlas files name
        /// every loot entry that way - <c>{ id = 16928, ... }, -- Nemesis Gloves</c> - which is the
        /// only place an item name appears, so it is worth keeping.
        /// </summary>
        public string? TrailingComment { get; set; }

        public object? Field(string name) => Fields.GetValueOrDefault(name);

        public Table? TableField(string name) => Field(name) as Table;

        public double? Number(string name) => Field(name) as double?;

        public int? Int(string name) => Number(name) is { } d ? (int)d : null;

        /// <summary>Field as readable text, whether it is a plain string or a localisation lookup.</summary>
        public string? Text(string name) => Field(name) switch
        {
            string s => s,
            LuaExpression e => e.AsText(),
            _ => null
        };
    }

    /// <summary>
    /// Reads a whole file, returning every assignment it makes. Keys are the full dotted target,
    /// e.g. "AtlasCFM.InstanceData.BlackwingLair"; locals are keyed by their bare name.
    /// </summary>
    public static Dictionary<string, object?> ParseFile(string source)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        var locals = new Dictionary<string, object?>(StringComparer.Ordinal);
        var i = 0;

        while (i < source.Length)
        {
            SkipTrivia(source, ref i);
            if (i >= source.Length) break;

            var isLocal = Consume(source, ref i, "local");
            if (isLocal) SkipTrivia(source, ref i);

            var target = ReadAssignmentTarget(source, ref i);
            if (target is null) { i++; continue; }

            SkipTrivia(source, ref i);
            if (i >= source.Length || source[i] != '=') continue;
            i++;

            SkipTrivia(source, ref i);
            var value = ReadValue(source, ref i, locals);

            if (isLocal) locals[target] = value;
            result[target] = value;
        }

        return result;
    }

    // ---- Values --------------------------------------------------------------------------------

    private static object? ReadValue(string s, ref int i, Dictionary<string, object?> locals)
    {
        SkipTrivia(s, ref i);
        if (i >= s.Length) return null;

        if (s[i] == '{') return ReadTable(s, ref i, locals);
        if (s[i] is '"' or '\'') return ReadPossiblyConcatenated(s, ref i);

        if (char.IsDigit(s[i]) || (s[i] == '-' && i + 1 < s.Length && char.IsDigit(s[i + 1])))
        {
            var start = i;
            if (s[i] == '-') i++;
            while (i < s.Length && (char.IsDigit(s[i]) || s[i] == '.')) i++;

            // A number followed by ".." is the left side of a concatenation, not a value.
            if (double.TryParse(s[start..i], NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
                return number;
            i = start;
        }

        var expression = ReadExpressionText(s, ref i);
        return expression switch
        {
            "true" => true,
            "false" => false,
            "nil" => null,
            _ => new LuaExpression(expression)
        };
    }

    private static Table ReadTable(string s, ref int i, Dictionary<string, object?> locals)
    {
        var table = new Table();
        i++; // consume '{'

        while (i < s.Length)
        {
            SkipTrivia(s, ref i);
            if (i >= s.Length) break;

            if (s[i] == '}') { i++; break; }
            if (s[i] is ',' or ';') { i++; continue; }

            // unpack(shared) splices another table's array entries in place.
            if (Consume(s, ref i, "unpack"))
            {
                SkipTrivia(s, ref i);
                if (i < s.Length && s[i] == '(')
                {
                    i++;
                    SkipTrivia(s, ref i);
                    var name = ReadIdentifier(s, ref i);
                    SkipTrivia(s, ref i);
                    if (i < s.Length && s[i] == ')') i++;

                    if (locals.GetValueOrDefault(name) is Table shared)
                        table.Array.AddRange(shared.Array);
                    continue;
                }
            }

            var key = TryReadKey(s, ref i);
            var value = ReadValue(s, ref i, locals);

            if (value is Table entry && ReadTrailingComment(s, i) is { } comment)
                entry.TrailingComment = comment;

            if (key is null) table.Array.Add(value);
            else table.Fields[key] = value;
        }

        return table;
    }

    /// <summary>Looks ahead for a line comment on the same line as the entry just read.</summary>
    private static string? ReadTrailingComment(string s, int from)
    {
        var i = from;
        while (i < s.Length && s[i] != '\n')
        {
            if (s[i] == '-' && i + 1 < s.Length && s[i + 1] == '-')
            {
                i += 2;
                var end = s.IndexOf('\n', i);
                if (end < 0) end = s.Length;
                return s[i..end].Trim();
            }
            if (!char.IsWhiteSpace(s[i]) && s[i] != ',' && s[i] != '}') return null;
            i++;
        }
        return null;
    }

    /// <summary>Reads `name =` or `["name"] =` if present, leaving the cursor on the value.</summary>
    private static string? TryReadKey(string s, ref int i)
    {
        var start = i;

        if (s[i] == '[')
        {
            i++;
            SkipTrivia(s, ref i);
            if (i < s.Length && s[i] is '"' or '\'')
            {
                var key = ReadString(s, ref i);
                SkipTrivia(s, ref i);
                if (i < s.Length && s[i] == ']')
                {
                    i++;
                    SkipTrivia(s, ref i);
                    if (i < s.Length && s[i] == '=' && (i + 1 >= s.Length || s[i + 1] != '=')) { i++; return key; }
                }
            }
            i = start;
            return null;
        }

        if (char.IsLetter(s[i]) || s[i] == '_')
        {
            var name = ReadIdentifier(s, ref i);
            SkipTrivia(s, ref i);
            if (i < s.Length && s[i] == '=' && (i + 1 >= s.Length || s[i + 1] != '=')) { i++; return name; }
            i = start;
        }

        return null;
    }

    private static string? ReadAssignmentTarget(string s, ref int i)
    {
        if (i >= s.Length || (!char.IsLetter(s[i]) && s[i] != '_')) return null;

        var start = i;
        while (i < s.Length && (char.IsLetterOrDigit(s[i]) || s[i] is '_' or '.')) i++;

        var target = s[start..i];
        SkipTrivia(s, ref i);

        // Only an assignment counts; a bare call or comparison is skipped.
        if (i < s.Length && s[i] == '=' && (i + 1 >= s.Length || s[i + 1] != '=')) return target;

        i = start + target.Length;
        return null;
    }

    /// <summary>Reads a string, keeping any `.. "more"` concatenation attached.</summary>
    private static object ReadPossiblyConcatenated(string s, ref int i)
    {
        var start = i;
        ReadString(s, ref i);

        var after = i;
        SkipTrivia(s, ref after);
        if (after + 1 < s.Length && s[after] == '.' && s[after + 1] == '.')
        {
            i = start;
            return new LuaExpression(ReadExpressionText(s, ref i));
        }

        i = start;
        return ReadString(s, ref i);
    }

    private static string ReadString(string s, ref int i)
    {
        var quote = s[i++];
        var sb = new StringBuilder();

        while (i < s.Length && s[i] != quote)
        {
            if (s[i] == '\\' && i + 1 < s.Length)
            {
                i++;
                sb.Append(s[i] switch { 'n' => '\n', 't' => '\t', 'r' => '\r', _ => s[i] });
            }
            else sb.Append(s[i]);
            i++;
        }

        if (i < s.Length) i++;
        return sb.ToString();
    }

    /// <summary>Consumes an expression, keeping bracket nesting and strings balanced.</summary>
    private static string ReadExpressionText(string s, ref int i)
    {
        var start = i;
        var depth = 0;

        while (i < s.Length)
        {
            var c = s[i];

            if (c is '"' or '\'') { ReadString(s, ref i); continue; }
            if (c is '(' or '[' or '{') { depth++; i++; continue; }
            if (c is ')' or ']' or '}')
            {
                if (depth == 0) break;
                depth--; i++; continue;
            }
            if (depth == 0)
            {
                if (c is ',' or ';') break;
                // A comment ends the expression; ".." is concatenation and continues it.
                if (c == '-' && i + 1 < s.Length && s[i + 1] == '-') break;
                if (c == '\n') break;
            }
            i++;
        }

        return s[start..i].Trim();
    }

    private static string ReadIdentifier(string s, ref int i)
    {
        var start = i;
        while (i < s.Length && (char.IsLetterOrDigit(s[i]) || s[i] is '_' or '.')) i++;
        return s[start..i];
    }

    private static bool Consume(string s, ref int i, string word)
    {
        if (i + word.Length > s.Length || !s.AsSpan(i, word.Length).SequenceEqual(word)) return false;

        var after = i + word.Length;
        if (after < s.Length && (char.IsLetterOrDigit(s[after]) || s[after] == '_')) return false;

        i = after;
        return true;
    }

    /// <summary>Skips whitespace and both line and long-bracket comments.</summary>
    private static void SkipTrivia(string s, ref int i)
    {
        while (i < s.Length)
        {
            if (char.IsWhiteSpace(s[i])) { i++; continue; }

            if (s[i] == '-' && i + 1 < s.Length && s[i + 1] == '-')
            {
                i += 2;
                if (i + 1 < s.Length && s[i] == '[' && s[i + 1] == '[')
                {
                    var end = s.IndexOf("]]", i, StringComparison.Ordinal);
                    i = end < 0 ? s.Length : end + 2;
                }
                else
                {
                    while (i < s.Length && s[i] != '\n') i++;
                }
                continue;
            }

            break;
        }
    }
}
