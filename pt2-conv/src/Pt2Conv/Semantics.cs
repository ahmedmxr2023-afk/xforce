using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Pt2Conv;

/// <summary>
/// Semantic analysis of a PLCopen export that goes beyond syntax: where a global tag is written.
/// TriStation reports <c>PC0005</c> ("Multiple writes to tagname … written by …") when a program
/// writes a tag at more than one site without <c>EnableMultipleWrite</c>; lossy exports can drop
/// that flag, so the conflict has to be detected here.
/// </summary>
public static class Semantics
{
    /// <summary>Map of global tag name to write-site count per program. FBD: an in/out or output
    /// variable placement that has an incoming connection, or a coil. ST: an assignment target at
    /// the start of a statement.</summary>
    public static Dictionary<string, Dictionary<string, int>> WriteSitesByTag(XDocument doc)
    {
        var map = new Dictionary<string, Dictionary<string, int>>(StringComparer.OrdinalIgnoreCase);
        foreach (var pou in doc.Descendants().Where(e => e.Name.LocalName == "pou"))
        {
            string pouType = ((string?)pou.Attribute("pouType") ?? "").ToLowerInvariant();
            if (pouType != "program") continue;
            string program = (string?)pou.Attribute("name") ?? "";
            if (program.Length == 0) continue;
            var body = pou.Descendants().FirstOrDefault(e => e.Name.LocalName == "body");
            if (body == null) continue;

            foreach (var v in body.Descendants().Where(e =>
                         e.Name.LocalName is "inOutVariable" or "outVariable" or "coil"))
            {
                // A value is written only when the placement is fed (has a connection) or is a coil;
                // a bare placement is just a reference.
                bool fed = v.Descendants().Any(d => d.Name.LocalName == "connection");
                if (!fed && v.Name.LocalName != "coil") continue;
                string name = (string?)v.Attribute("expression")
                              ?? v.Descendants().FirstOrDefault(d => d.Name.LocalName == "expression")?.Value
                              ?? "";
                name = name.Trim();
                if (!IsIdentifier(name)) continue;
                Count(name, program);
            }

            var st = body.Descendants().FirstOrDefault(e => e.Name.LocalName == "ST");
            if (st != null)
                foreach (Match m in Regex.Matches(st.Value ?? "", @"(?m)^\s*([A-Za-z_][A-Za-z0-9_]*)\s*:="))
                    Count(m.Groups[1].Value, program);
        }
        return map;

        void Count(string name, string program)
        {
            if (!map.TryGetValue(name, out var perProgram))
                map[name] = perProgram = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            perProgram.TryGetValue(program, out int n);
            perProgram[program] = n + 1;
        }
    }

    /// <summary>Tags written more than once in total (same program at several sites, or by more
    /// than one program). These need <c>EnableMultipleWrite</c>.</summary>
    public static HashSet<string> MultipleWriteTags(XDocument doc)
        => WriteSitesByTag(doc)
            .Where(kv => kv.Value.Values.Sum() > 1)
            .Select(kv => kv.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static bool IsIdentifier(string s)
        => s.Length > 0 && (char.IsLetter(s[0]) || s[0] == '_') && s.All(c => char.IsLetterOrDigit(c) || c == '_');
}
