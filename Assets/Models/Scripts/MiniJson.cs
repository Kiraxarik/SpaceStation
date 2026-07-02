using System.Collections.Generic;
using System.Globalization;
using System.Text;

/// <summary>
/// Minimal recursive-descent JSON parser producing a plain object graph:
/// Dictionary&lt;string,object&gt; for objects, List&lt;object&gt; for arrays, double for
/// numbers, string, bool, or null for the leaves.
///
/// WHY THIS EXISTS: Unity's built-in JsonUtility (used by BlockbenchGeometryParser
/// for .geo.json) can only deserialize into fields/arrays of a known C# class — it
/// has no support for Dictionary. Bedrock's .animation.json format is keyed by
/// bone name ("bones": { "arm": {...} }) and by timestamp string
/// ("rotation": { "0.0": [...], "0.5": [...] }), both of which are genuinely
/// dynamic keys unknown at compile time. JsonUtility cannot represent that; this
/// parser can. No external dependency (Newtonsoft isn't in this project's
/// manifest) — this is a self-contained ~150-line parser, scoped to animation
/// files only. .geo.json keeps using JsonUtility unchanged.
///
/// SCOPE: standard JSON only (no comments, no trailing commas — Blockbench's
/// export doesn't use either). Not built for speed; animation files are small
/// and this only runs on mod load / first request, same cold-path cost class as
/// BlockbenchGeometryParser.
/// </summary>
public static class MiniJson
{
    public static object Parse(string json)
    {
        int i = 0;
        SkipWhitespace(json, ref i);
        var value = ParseValue(json, ref i);
        return value;
    }

    static object ParseValue(string s, ref int i)
    {
        SkipWhitespace(s, ref i);
        if (i >= s.Length) return null;

        switch (s[i])
        {
            case '{': return ParseObject(s, ref i);
            case '[': return ParseArray(s, ref i);
            case '"': return ParseString(s, ref i);
            case 't':
                Expect(s, ref i, "true");
                return true;
            case 'f':
                Expect(s, ref i, "false");
                return false;
            case 'n':
                Expect(s, ref i, "null");
                return null;
            default:
                return ParseNumber(s, ref i);
        }
    }

    static Dictionary<string, object> ParseObject(string s, ref int i)
    {
        var obj = new Dictionary<string, object>();
        i++; // consume '{'
        SkipWhitespace(s, ref i);
        if (i < s.Length && s[i] == '}') { i++; return obj; }

        while (true)
        {
            SkipWhitespace(s, ref i);
            string key = ParseString(s, ref i);
            SkipWhitespace(s, ref i);
            if (i >= s.Length || s[i] != ':')
            {
                UnityEngine.Debug.LogError("[MiniJson] Expected ':' in object.");
                return obj;
            }
            i++; // consume ':'
            object val = ParseValue(s, ref i);
            obj[key] = val;

            SkipWhitespace(s, ref i);
            if (i >= s.Length) break;
            if (s[i] == ',') { i++; continue; }
            if (s[i] == '}') { i++; break; }
            UnityEngine.Debug.LogError("[MiniJson] Expected ',' or '}' in object.");
            break;
        }
        return obj;
    }

    static List<object> ParseArray(string s, ref int i)
    {
        var arr = new List<object>();
        i++; // consume '['
        SkipWhitespace(s, ref i);
        if (i < s.Length && s[i] == ']') { i++; return arr; }

        while (true)
        {
            object val = ParseValue(s, ref i);
            arr.Add(val);
            SkipWhitespace(s, ref i);
            if (i >= s.Length) break;
            if (s[i] == ',') { i++; continue; }
            if (s[i] == ']') { i++; break; }
            UnityEngine.Debug.LogError("[MiniJson] Expected ',' or ']' in array.");
            break;
        }
        return arr;
    }

    static string ParseString(string s, ref int i)
    {
        if (i >= s.Length || s[i] != '"')
        {
            UnityEngine.Debug.LogError("[MiniJson] Expected string.");
            return "";
        }
        i++; // consume opening quote
        var sb = new StringBuilder();
        while (i < s.Length && s[i] != '"')
        {
            char c = s[i];
            if (c == '\\' && i + 1 < s.Length)
            {
                i++;
                char esc = s[i];
                switch (esc)
                {
                    case '"': sb.Append('"'); break;
                    case '\\': sb.Append('\\'); break;
                    case '/': sb.Append('/'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'u':
                        if (i + 4 < s.Length)
                        {
                            string hex = s.Substring(i + 1, 4);
                            sb.Append((char)int.Parse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                            i += 4;
                        }
                        break;
                    default: sb.Append(esc); break;
                }
                i++;
            }
            else
            {
                sb.Append(c);
                i++;
            }
        }
        i++; // consume closing quote
        return sb.ToString();
    }

    static double ParseNumber(string s, ref int i)
    {
        int start = i;
        while (i < s.Length && (char.IsDigit(s[i]) || s[i] == '-' || s[i] == '+' || s[i] == '.' ||
                                 s[i] == 'e' || s[i] == 'E'))
            i++;
        string num = s.Substring(start, i - start);
        return double.TryParse(num, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) ? d : 0.0;
    }

    static void Expect(string s, ref int i, string literal)
    {
        if (i + literal.Length <= s.Length && s.Substring(i, literal.Length) == literal)
            i += literal.Length;
        else
            UnityEngine.Debug.LogError($"[MiniJson] Expected '{literal}' at position {i}.");
    }

    static void SkipWhitespace(string s, ref int i)
    {
        while (i < s.Length && (s[i] == ' ' || s[i] == '\t' || s[i] == '\n' || s[i] == '\r'))
            i++;
    }

    // ── Convenience accessors (all null-safe, return sensible defaults) ────────

    public static Dictionary<string, object> AsObject(object v) => v as Dictionary<string, object>;
    public static List<object> AsArray(object v) => v as List<object>;
    public static string AsString(object v, string fallback = "") => v is string str ? str : fallback;
    public static double AsNumber(object v, double fallback = 0.0) => v is double d ? d : fallback;
    public static bool AsBool(object v, bool fallback = false) => v is bool b ? b : fallback;

    public static Dictionary<string, object> GetObject(Dictionary<string, object> obj, string key)
        => obj != null && obj.TryGetValue(key, out var v) ? AsObject(v) : null;

    public static List<object> GetArray(Dictionary<string, object> obj, string key)
        => obj != null && obj.TryGetValue(key, out var v) ? AsArray(v) : null;

    public static string GetString(Dictionary<string, object> obj, string key, string fallback = "")
        => obj != null && obj.TryGetValue(key, out var v) ? AsString(v, fallback) : fallback;

    public static double GetNumber(Dictionary<string, object> obj, string key, double fallback = 0.0)
        => obj != null && obj.TryGetValue(key, out var v) ? AsNumber(v, fallback) : fallback;

    public static bool GetBool(Dictionary<string, object> obj, string key, bool fallback = false)
        => obj != null && obj.TryGetValue(key, out var v) ? AsBool(v, fallback) : fallback;
}