using System.Text;
using System.Text.RegularExpressions;
using Pt2Conv.Cfb;

namespace Pt2Conv;

/// <summary>
/// PHASE-2 RECON: parses Current/Tagnames/Tagnames.csv (the tag database) and,
/// optionally, compares it against the global variable declarations of a PLCopen XML.
/// Read-only: reports structure + set differences; does not modify anything.
/// </summary>
public static class Tags
{
    public static int Run(string[] args)
    {
        if (args.Length < 2) { Console.Error.WriteLine("usage: tags <file.pt2x> [<xml>]"); return 1; }
        var cf = CompoundFile.Open(args[1]);
        var s = cf.ListStreams().FirstOrDefault(x =>
            x.Path.Equals("Current/Tagnames/Tagnames.csv", StringComparison.OrdinalIgnoreCase));
        if (s == null) { Console.Error.WriteLine("no Current/Tagnames/Tagnames.csv"); return 2; }

        var lines = Encoding.UTF8.GetString(s.Data).Split('\n').Select(l => l.TrimEnd('\r')).ToList();

        Console.WriteLine($"file        : {args[1]}");
        Console.WriteLine("header      : " + Regex.Replace(lines[0], @"\s+", " "));

        var cols = SplitCsv(lines[1]);
        Console.WriteLine($"columns     : {cols.Count}");

        var rows = new List<string[]>();
        for (int i = 2; i < lines.Count; i++)
            if (lines[i].StartsWith("\"")) rows.Add(SplitCsv(lines[i]).ToArray());
        Console.WriteLine($"data rows   : {rows.Count}");

        int idxName = Col(cols, "Tagname");
        int idxType = Col(cols, "DataType");
        int idxLoc = Col(cols, "Location");

        Console.WriteLine();
        Console.WriteLine("-- DataType histogram --");
        foreach (var g in rows.GroupBy(r => At(r, idxType)).OrderByDescending(g => g.Count()).Take(15))
            Console.WriteLine($"  {g.Count(),6}  {g.Key}");

        if (idxLoc >= 0)
        {
            Console.WriteLine();
            Console.WriteLine("-- first 5 Location values --");
            foreach (var r in rows.Take(5)) Console.WriteLine("  " + At(r, idxLoc));
            Console.WriteLine("-- Location pattern histogram --");
            foreach (var g in rows.Select(r => Regex.Replace(At(r, idxLoc), @"\d+$", "#"))
                                   .GroupBy(v => v).OrderByDescending(g => g.Count()).Take(10))
                Console.WriteLine($"  {g.Count(),6}  {g.Key}");
        }

        if (args.Length >= 3) CompareWithXml(args[2], cols, lines);
        return 0;
    }

    private static void CompareWithXml(string xmlPath, List<string> cols, List<string> lines)
    {
        int idxName = Col(cols, "Tagname");
        int idxType = Col(cols, "DataType");

        var tagType = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 2; i < lines.Count; i++)
        {
            if (!lines[i].StartsWith("\"")) continue;
            var f = SplitCsv(lines[i]);
            tagType[At(f, idxName)] = At(f, idxType);
        }

        var xmlType = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string xml = File.ReadAllText(xmlPath);
        foreach (Match g in Regex.Matches(xml, "<globalVars[^>]*>(.*?)</globalVars>", RegexOptions.Singleline))
            foreach (Match m in Regex.Matches(g.Groups[1].Value, "<variable name=\"([^\"]+)\"\\s*>(.*?)</variable>", RegexOptions.Singleline))
            {
                string name = m.Groups[1].Value;
                if (name.Length == 0 || name.All(char.IsDigit)) continue;
                var tm = Regex.Match(m.Groups[2].Value, "<type>\\s*<([A-Za-z_]\\w*)");
                xmlType[name] = tm.Success ? tm.Groups[1].Value : "?";
            }

        var t = new HashSet<string>(tagType.Keys, StringComparer.OrdinalIgnoreCase);
        var x = new HashSet<string>(xmlType.Keys, StringComparer.OrdinalIgnoreCase);
        Console.WriteLine();
        Console.WriteLine("-- tag vs XML-global comparison --");
        Console.WriteLine($"  tag names: {t.Count}   named XML globals: {x.Count}   in both: {t.Intersect(x).Count()}");
        var onlyTag = t.Except(x).OrderBy(v => v).ToList();
        var onlyXml = x.Except(t).OrderBy(v => v).ToList();
        Console.WriteLine($"  only in Tagnames.csv ({onlyTag.Count}): {string.Join(", ", onlyTag.Take(25))}");
        Console.WriteLine($"  only in XML globals ({onlyXml.Count}): {string.Join(", ", onlyXml.Take(25))}");

        int typeMismatch = t.Intersect(x).Count(n =>
            tagType.TryGetValue(n, out var a) && xmlType.TryGetValue(n, out var b) &&
            !NormType(a).Equals(NormType(b), StringComparison.OrdinalIgnoreCase));
        Console.WriteLine($"  data-type mismatches among shared names: {typeMismatch}");
    }

    private static string NormType(string s)
    {
        s = (s ?? "").Trim();
        if (s.Equals("BOOL", StringComparison.OrdinalIgnoreCase)) return "BOOL";
        if (s.Equals("REAL", StringComparison.OrdinalIgnoreCase)) return "REAL";
        if (s.StartsWith("DINT", StringComparison.OrdinalIgnoreCase)) return "DINT";
        return s.ToUpperInvariant();
    }

    private static int Col(List<string> cols, string name)
    {
        for (int i = 0; i < cols.Count; i++) if (cols[i].Equals(name, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }

    private static string At(IList<string> r, int i) => (i >= 0 && i < r.Count) ? r[i] : "";

    private static List<string> SplitCsv(string line)
    {
        var outp = new List<string>();
        var sb = new StringBuilder();
        bool q = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (q)
            {
                if (c == '"')
                {
                    if (i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                    else q = false;
                }
                else sb.Append(c);
            }
            else
            {
                if (c == '"') q = true;
                else if (c == ',') { outp.Add(sb.ToString()); sb.Clear(); }
                else sb.Append(c);
            }
        }
        outp.Add(sb.ToString());
        return outp;
    }
}
