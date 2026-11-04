using System.Text;
using System.Xml.Linq;

namespace Pt2Conv;

/// <summary>
/// Scans a PLCopen/TriStation XML before conversion and reports what the builder can and cannot
/// represent. The point is to fail loudly and early instead of producing a container that
/// TriStation later rejects.
/// </summary>
public static class Capabilities
{
    public sealed class Report
    {
        public int Pous, Fbd, St, Ld, Sfc, Tags, DataTypes, Configurations, Tasks;
        public List<string> Blockers = new();
        public List<string> Warnings = new();
        public List<string> Notes = new();

        public bool CanConvert => Blockers.Count == 0;
    }

    public static Report Analyze(string xmlPath)
    {
        var report = new Report();
        XDocument doc;
        try { doc = XmlRepair.Load(xmlPath); }
        catch (Exception ex)
        {
            report.Blockers.Add("The file is not readable XML: " + ex.Message);
            return report;
        }

        var pous = doc.Descendants().Where(e => e.Name.LocalName == "pou").ToList();
        report.Pous = pous.Count;

        // Only TriStation 1131 PLCopen exports are supported. A file from another platform
        // (SimaticML, Codesys, ...) has a different element model and must be translated first.
        var header = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "fileHeader");
        string product = (string?)header?.Attribute("productName") ?? "";
        if (product.Length > 0 && product.IndexOf("TriStation", StringComparison.OrdinalIgnoreCase) < 0)
            report.Blockers.Add($"Foreign export ('{product}'). Only TriStation 1131 PLCopen XML is supported"
                                + (product.IndexOf("Simatic", StringComparison.OrdinalIgnoreCase) >= 0
                                    ? "; convert SimaticML with 'pt2conv fromsimatic' first." : "."));
        string rootNs = doc.Root?.Name.NamespaceName ?? "";
        string rootName = doc.Root?.Name.LocalName ?? "";
        if (!rootName.Equals("project", StringComparison.OrdinalIgnoreCase))
        {
            report.Blockers.Add($"Not a PLCopen project document (root element '{rootName}').");
            return report;
        }
        if (rootNs.Length > 0 && rootNs != "http://www.plcopen.org/xml/tc6.xsd" && rootNs != "http://www.triconex.com/controller/03/2008")
            report.Blockers.Add($"Unexpected document namespace '{rootNs}' (expected PLCopen TC6 or TriStation).");

        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in doc.Descendants())
        {
            var tn = e.Attribute("typeName");
            if (tn != null) known.Add(tn.Value);
        }
        var pouNames = new HashSet<string>(
            pous.Select(e => (string?)e.Attribute("name")).Where(n => !string.IsNullOrEmpty(n)).Select(n => n!),
            StringComparer.OrdinalIgnoreCase);

        foreach (var pou in pous)
        {
            string name = (string?)pou.Attribute("name") ?? "(unnamed)";
            // Language = the body child element (FBD/ST/LD/SFC/IL/...). Looking at descendants
            // alone would also match e.g. an ST snippet embedded in a comment.
            var body = pou.Elements().FirstOrDefault(e => e.Name.LocalName == "body");
            var lang = body?.Elements().Select(e => e.Name.LocalName).FirstOrDefault() ?? "";
            bool hasFbd = lang.Equals("FBD", StringComparison.OrdinalIgnoreCase);
            bool hasSt = lang.Equals("ST", StringComparison.OrdinalIgnoreCase);
            bool hasLd = lang.Equals("LD", StringComparison.OrdinalIgnoreCase);
            bool hasSfc = lang.Equals("SFC", StringComparison.OrdinalIgnoreCase);
            if (hasFbd) report.Fbd++;
            if (hasSt) report.St++;
            if (hasFbd && body != null)
            {
                // Constructs that are legal PLCopen but have no TriStation FBD representation.
                // Refuse them instead of silently dropping logic from a custom diagram.
                var unsupported = body.Descendants()
                    .Select(e => e.Name.LocalName)
                    .Where(n => n is "connector" or "continuation" or "jump" or "label" or "return"
                             or "action" or "coil" or "contact")
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                if (unsupported.Count > 0)
                    report.Blockers.Add($"'{name}' uses FBD construct(s) this builder cannot convert: "
                                        + string.Join(", ", unsupported) + ".");
            }
            if (hasLd) { report.Ld++; report.Blockers.Add($"'{name}' uses Ladder Diagram (LD), which this builder cannot convert."); }
            if (hasSfc) { report.Sfc++; report.Blockers.Add($"'{name}' uses Sequential Function Chart (SFC), which this builder cannot convert."); }
            if (!hasFbd && !hasSt && !hasLd && !hasSfc)
            {
                if (lang.Length > 0)
                    report.Blockers.Add($"'{name}' uses {lang}, which this builder cannot convert (only FBD and ST).");
                else
                    report.Warnings.Add($"'{name}' has no diagram body — it will be kept as a declaration only.");
            }
            var pt = (string?)pou.Attribute("pouType") ?? "";
            if (pt.Length > 0 && !pt.Equals("program", StringComparison.OrdinalIgnoreCase)
                               && !pt.Equals("functionBlock", StringComparison.OrdinalIgnoreCase))
                report.Blockers.Add($"'{name}' is a {pt}; only programs and function blocks are supported.");
        }

        // User-defined data types are not converted. PLCopen stores them under types/dataTypes;
        // usage also shows up as a <derived> reference that is neither a POU nor a library type.
        report.DataTypes = doc.Descendants().Count(e => e.Name.LocalName == "dataType");
        if (report.DataTypes > 0)
            report.Blockers.Add($"{report.DataTypes} user-defined data type(s) (UDT) found; they are not converted.");

        report.Configurations = doc.Descendants().Count(e => e.Name.LocalName == "configuration");
        report.Tasks = doc.Descendants().Count(e => e.Name.LocalName == "task");
        if (report.Configurations > 1 || report.Tasks > 1)
            report.Warnings.Add($"{report.Configurations} configuration(s) / {report.Tasks} task(s): only the first execution list is carried over.");

        report.Tags = doc.Descendants().Count(e => e.Name.LocalName == "TAGNAME");
        if (report.Tags == 0)
            report.Warnings.Add("No tag declarations found — the tag database will come from the template.");

        // Programs can reference globals that have no TAGNAME declaration. TriStation itself
        // cannot resolve those (WF0078), so refuse them instead of inventing tags.
        try
        {
            var text = File.ReadAllText(xmlPath);
            var declared = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(text, "(?s)<variable\\s+name=\"([^\"]+)\">(.*?)</variable>"))
                if (m.Groups[2].Value.Contains("TAGNAME", StringComparison.Ordinal)) declared.Add(m.Groups[1].Value);
            var referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (System.Text.RegularExpressions.Match p in System.Text.RegularExpressions.Regex.Matches(text, "(?s)<pou\\s+name=\"[^\"]+\"[^>]*pouType=\"program\"[^>]*>(.*?)</pou>"))
                foreach (System.Text.RegularExpressions.Match g in System.Text.RegularExpressions.Regex.Matches(p.Groups[1].Value, "(?s)<globalVars[^>]*>(.*?)</globalVars>"))
                    foreach (System.Text.RegularExpressions.Match v in System.Text.RegularExpressions.Regex.Matches(g.Groups[1].Value, "<variable\\s+name=\"([^\"]+)\""))
                        referenced.Add(v.Groups[1].Value);
            var missing = referenced.Where(n => !declared.Contains(n) && FbdGenerator.IsDeclarableName(n)).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
            if (missing.Count > 0)
                report.Blockers.Add($"{missing.Count} tag(s) are referenced by programs but have no declaration: "
                                    + string.Join(", ", missing.Take(12)) + (missing.Count > 12 ? $" … (+{missing.Count - 12})" : ""));

            // Lossy exports can strip EnableMultipleWrite. Detect the provable conflict instead of
            // failing later in the compiler with PC0005.
            var multi = Semantics.MultipleWriteTags(doc);
            var flagged = new List<string>();
            foreach (var tagName in multi)
            {
                bool declaredNo = false, hasTag = false;
                foreach (var v in doc.Descendants().Where(e => e.Name.LocalName == "variable"
                                                               && string.Equals((string?)e.Attribute("name"), tagName, StringComparison.OrdinalIgnoreCase)))
                {
                    var tn = v.Descendants().FirstOrDefault(e => e.Name.LocalName == "TAGNAME");
                    if (tn == null) continue;
                    hasTag = true;
                    if (string.Equals((string?)tn.Descendants().FirstOrDefault(e => e.Name.LocalName == "TAG_DECLARATION")?.Attribute("EnableMultipleWrite"),
                                      "No", StringComparison.OrdinalIgnoreCase))
                        declaredNo = true;
                }
                if (hasTag && declaredNo) flagged.Add(tagName);
            }
            if (flagged.Count > 0)
                report.Warnings.Add($"{flagged.Count} tag(s) are written at more than one site but the export declares "
                                    + "EnableMultipleWrite=\"No\" (the compiler will report PC0005): "
                                    + string.Join(", ", flagged.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).Take(12))
                                    + (flagged.Count > 12 ? $" … (+{flagged.Count - 12})" : "")
                                    + ". Re-export with multiple writes enabled, or pass --assume-multi-write to buildxml.");
        }
        catch { }

        if (doc.Descendants().Any(e => e.Name.LocalName == "CEM" || e.Name.LocalName == "cem"))
            report.Warnings.Add("Cause & Effect (CEM) data found; it is not converted.");

        // Block types the library table does not know and that are not POUs in this file: those
        // instances will open as unknown blocks in TriStation.
        var libraryTypes = new HashSet<string>(FbdGenerator.LibraryDogTags().Keys, StringComparer.OrdinalIgnoreCase);
        var unknown = known
            .Where(t => !libraryTypes.Contains(t) && !pouNames.Contains(t))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(t => t, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (unknown.Count > 0)
            report.Blockers.Add("Unknown block type(s), not in the library and not defined in this file: " + string.Join(", ", unknown.Take(12))
                                + (unknown.Count > 12 ? $" … (+{unknown.Count - 12})" : ""));

        // A <derived> type that is neither a POU nor a library element is a user-defined type
        // (or an instance of a missing project POU); either way the builder cannot resolve it.
        var derived = doc.Descendants().Where(e => e.Name.LocalName == "derived")
            .Select(e => (string?)e.Attribute("name") ?? "")
            .Where(n => n.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var unknownDerived = derived
            .Where(n => !libraryTypes.Contains(n) && !pouNames.Contains(n))
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (unknownDerived.Count > 0)
            report.Blockers.Add("Unknown derived type(s) (user-defined data type or missing POU): " + string.Join(", ", unknownDerived.Take(12))
                                + (unknownDerived.Count > 12 ? $" … (+{unknownDerived.Count - 12})" : ""));

        var libs = doc.Descendants().Where(e => e.Name.LocalName == "LIBRARY").Select(e => (string?)e.Attribute("Name") ?? "").Where(n => n.Length > 0).ToList();
        if (libs.Count > 0) report.Notes.Add("Libraries: " + string.Join(", ", libs.Distinct(StringComparer.OrdinalIgnoreCase)));

        string? toolchain = Toolchain.CompatibilityNote();
        if (toolchain != null) report.Notes.Add(toolchain);
        foreach (var libNote in Toolchain.VerifyLibraries())
            report.Notes.Add(libNote);

        return report;
    }

    public static int Run(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("usage: check <xml> [--json]");
            return 1;
        }

        bool json = args.Any(a => a.Equals("--json", StringComparison.OrdinalIgnoreCase));
        var r = Analyze(args[1]);
        if (json)
        {
            var payload = new
            {
                file = Path.GetFullPath(args[1]),
                canConvert = r.CanConvert,
                counts = new
                {
                    pous = r.Pous, fbd = r.Fbd, st = r.St, ld = r.Ld, sfc = r.Sfc,
                    tags = r.Tags, dataTypes = r.DataTypes,
                    configurations = r.Configurations, tasks = r.Tasks,
                },
                blockers = r.Blockers,
                warnings = r.Warnings,
                notes = r.Notes,
            };
            Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(payload,
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            return r.CanConvert ? 0 : 3;
        }
        Console.WriteLine("Pre-flight capability check");
        Console.WriteLine("---------------------------");
        Console.WriteLine($"POUs: {r.Pous}  (FBD {r.Fbd}, ST {r.St}, LD {r.Ld}, SFC {r.Sfc})");
        Console.WriteLine($"Tag declarations: {r.Tags}   Data types: {r.DataTypes}");
        Console.WriteLine($"Configurations: {r.Configurations}   Tasks: {r.Tasks}");
        foreach (var n in r.Notes) Console.WriteLine("  - " + n);

        if (r.Blockers.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("CANNOT CONVERT:");
            foreach (var b in r.Blockers) Console.WriteLine("  X " + b);
        }
        if (r.Warnings.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("Warnings:");
            foreach (var w in r.Warnings) Console.WriteLine("  ! " + w);
        }

        Console.WriteLine();
        Console.WriteLine(r.CanConvert
            ? "Result: convertible. Remaining differences are reported by TriStation when you build."
            : "Result: NOT convertible as-is — see the blocking items above.");
        return r.CanConvert ? 0 : 3;
    }
}
