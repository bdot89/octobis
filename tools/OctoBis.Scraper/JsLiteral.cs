using System.Globalization;
using System.Text;

namespace OctoBis.Scraper;

/// <summary>
/// A tolerant reader for the JavaScript object literals the database embeds in its pages.
///
/// These are not JSON: keys are unquoted, strings are single-quoted with backslash escapes, arrays
/// can be sparse (<c>react: [,]</c>), and values are sometimes bare expressions such as
/// <c>LANG.tab_drops</c> or <c>Listview.funcBox.createSimpleCol('group', ...)</c>. Anything that is
/// not a string, number, array or object is captured verbatim as an <see cref="Expression"/> rather
/// than being treated as a parse failure - we only ever read the handful of keys we care about, so
/// an unfamiliar expression elsewhere in the payload must not bring the whole parse down.
/// </summary>
public static class JsLiteral
{
    /// <summary>A bare identifier or call expression that was not evaluated.</summary>
    public sealed record Expression(string Text);

    /// <summary>A hole in a sparse array literal.</summary>
    public sealed record Hole;

    /// <summary>Parses a value starting at <paramref name="i"/>, advancing it past the value.</summary>
    public static object? ParseValue(string s, ref int i)
    {
        SkipWhitespace(s, ref i);
        if (i >= s.Length) return null;

        return s[i] switch
        {
            '{' => ParseObject(s, ref i),
            '[' => ParseArray(s, ref i),
            '\'' or '"' => ParseString(s, ref i),
            _ => ParseNumberOrExpression(s, ref i)
        };
    }

    public static Dictionary<string, object?> ParseObject(string s, ref int i)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        i++; // consume '{'

        while (i < s.Length)
        {
            SkipWhitespace(s, ref i);
            if (i >= s.Length) break;
            if (s[i] == '}') { i++; break; }
            if (s[i] == ',') { i++; continue; }

            var key = s[i] is '\'' or '"' ? ParseString(s, ref i) : ReadIdentifier(s, ref i);

            SkipWhitespace(s, ref i);
            if (i < s.Length && s[i] == ':') i++;

            result[key] = ParseValue(s, ref i);
        }

        return result;
    }

    public static List<object?> ParseArray(string s, ref int i)
    {
        var result = new List<object?>();
        i++; // consume '['
        var expectingValue = true;

        while (i < s.Length)
        {
            SkipWhitespace(s, ref i);
            if (i >= s.Length) break;

            if (s[i] == ']')
            {
                // A trailing comma is a separator, not a hole: [1,2,] has two elements.
                i++;
                break;
            }

            if (s[i] == ',')
            {
                // Two commas in a row (or a leading comma) means a sparse-array hole.
                if (expectingValue) result.Add(new Hole());
                expectingValue = true;
                i++;
                continue;
            }

            result.Add(ParseValue(s, ref i));
            expectingValue = false;
        }

        return result;
    }

    private static string ParseString(string s, ref int i)
    {
        var quote = s[i++];
        var sb = new StringBuilder();

        while (i < s.Length && s[i] != quote)
        {
            if (s[i] == '\\' && i + 1 < s.Length)
            {
                i++;
                sb.Append(s[i] switch
                {
                    'n' => '\n',
                    'r' => '\r',
                    't' => '\t',
                    _ => s[i]
                });
            }
            else
            {
                sb.Append(s[i]);
            }
            i++;
        }

        if (i < s.Length) i++; // consume closing quote
        return sb.ToString();
    }

    private static object? ParseNumberOrExpression(string s, ref int i)
    {
        var start = i;

        if (s[i] is '-' or '+') i++;
        while (i < s.Length && (char.IsDigit(s[i]) || s[i] == '.')) i++;

        // A number must be followed by a separator; otherwise this was an identifier all along.
        if (i > start)
        {
            SkipWhitespace(s, ref i);
            var terminated = i >= s.Length || s[i] is ',' or '}' or ']' or ':';
            if (terminated &&
                double.TryParse(s.AsSpan(start, TrimmedLength(s, start, i)), NumberStyles.Float,
                                CultureInfo.InvariantCulture, out var number))
            {
                return number;
            }
            i = start;
        }

        return new Expression(ReadExpression(s, ref i));
    }

    private static int TrimmedLength(string s, int start, int end)
    {
        var length = end - start;
        while (length > 0 && char.IsWhiteSpace(s[start + length - 1])) length--;
        return length;
    }

    /// <summary>Consumes a bare expression, keeping bracket and quote nesting balanced.</summary>
    private static string ReadExpression(string s, ref int i)
    {
        var start = i;
        var depth = 0;

        while (i < s.Length)
        {
            var c = s[i];

            if (c is '\'' or '"')
            {
                ParseString(s, ref i);
                continue;
            }

            if (c is '(' or '[' or '{') depth++;
            else if (c is ')' or ']' or '}')
            {
                if (depth == 0) break;
                depth--;
            }
            else if (c == ',' && depth == 0) break;

            i++;
        }

        return s[start..i].Trim();
    }

    private static string ReadIdentifier(string s, ref int i)
    {
        var start = i;
        while (i < s.Length && (char.IsLetterOrDigit(s[i]) || s[i] is '_' or '$')) i++;
        return s[start..i];
    }

    private static void SkipWhitespace(string s, ref int i)
    {
        while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
    }

    // ---- Typed accessors used by the parsers ------------------------------------------------

    public static string? Str(Dictionary<string, object?> o, string key)
        => o.TryGetValue(key, out var v) && v is string s ? s : null;

    /// <summary>
    /// Numbers, whether or not the payload quoted them.
    ///
    /// The database is not consistent about this: a drop listview writes <c>id: 11981</c> while a
    /// quest listview writes <c>id: '41409'</c>. Reading only unquoted numbers silently discarded
    /// every quest reward in the database - 1,013 item pages, none of which produced a source.
    /// </summary>
    public static double? Num(Dictionary<string, object?> o, string key)
        => o.TryGetValue(key, out var v) ? AsNumber(v) : null;

    public static int? Int(Dictionary<string, object?> o, string key)
        => Num(o, key) is { } d ? (int)d : null;

    private static double? AsNumber(object? value) => value switch
    {
        double d => d,
        string s when double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) => parsed,
        _ => null
    };

    public static List<object?>? Arr(Dictionary<string, object?> o, string key)
        => o.TryGetValue(key, out var v) ? v as List<object?> : null;

    /// <summary>First numeric entry of an array value, skipping sparse holes. Used for `location: [2677]`.</summary>
    public static int? FirstInt(Dictionary<string, object?> o, string key)
    {
        foreach (var entry in Arr(o, key) ?? new List<object?>())
            if (AsNumber(entry) is { } d) return (int)d;
        return null;
    }
}
