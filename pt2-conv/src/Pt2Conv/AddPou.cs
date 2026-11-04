using System.Text;
using System.Xml.Linq;
using Pt2Conv.Cfb;

namespace Pt2Conv;

/// <summary>
/// PHASE-1: insert a POU into the element catalog (Current/Project.xml) by cloning a
/// donor element's catalog entry and language stream under a freshly allocated
/// DogTag/FName. Unverified until opened in TriStation.
///
/// Known from runA_COPY.PT2X:
///   - catalog entry lives in Current/Project.xml  (root SerializableClass)
///   - element folder = Current/Elements/&lt;ElemName&gt;/&lt;FName&gt;&lt;Extension&gt; (+ sibling _crc)
///   - FName == DogTag printed as 8 lowercase hex digits  (0x167 == 359)
///   - NextElementDogTag == max(DogTag) at save time
///   - &lt;stream&gt;_crc == SHA-256 hex of the stream bytes
///
/// NOT touched (unknown semantics): Current/Bin/Project.EMA and top-level meta_crc.
/// NOT synthesised: the catalog entry's Declarations (CTS2Vars) are cloned from the
/// donor, so local/interface variables of an inserted POU are not yet generated.
/// </summary>
public static class AddPou
{
    /// <summary>
    /// Serializes Current/Project.xml the way TriStation does: declaration + one line,
    /// and self-closing elements without the space XElement normally inserts before "/>".
    /// </summary>
    internal static byte[] SerializeProject(XElement root)
        => Encoding.UTF8.GetBytes("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n" +
                                  root.ToString(SaveOptions.DisableFormatting).Replace(" />", "/>") + "\n");

    public sealed class InsertResult
    {
        public int DogTag;
        public string FName = "";
        public string Extension = "";
        public string Donor = "";
        public List<string> Streams = new();
    }

    public static int Run(string[] args)
    {
        if (args.Length < 5)
        {
            Console.Error.WriteLine("usage: addpou <template.pt2x> <out.pt2x> <sourcePOU> <newName>");
            return 1;
        }
        string tmplPath = args[1], outPath = args[2], sourcePou = args[3], newName = args[4];

        if (File.Exists(outPath))
        {
            Console.Error.WriteLine("ERROR: output file already exists: " + outPath);
            return 3;
        }

        var tree = CompoundFile.Open(tmplPath).ListStreams()
            .Select(s => (Path: s.Path, Data: s.Data)).ToList();

        InsertResult r;
        try { r = Insert(tree, newName, donor: sourcePou); }
        catch (Exception ex) { Console.Error.WriteLine("ERROR: " + ex.Message); return 2; }

        Program.RewriteCrcs(tree);
        Program.RewriteGlobalCrc(tree);

        var dirOut = Path.GetDirectoryName(outPath);
        if (!string.IsNullOrEmpty(dirOut)) Directory.CreateDirectory(dirOut);
        CompoundFileWriter.Write(outPath, tree);

        Console.WriteLine($"cloned {r.Donor} -> {newName}");
        Console.WriteLine($"  DogTag {r.DogTag}  FName {r.FName}  Ext {r.Extension}");
        Console.WriteLine($"  element streams added: {r.Streams.Count}");
        foreach (var s in r.Streams) Console.WriteLine($"    {s}");
        Console.WriteLine($"Wrote {outPath} ({tree.Count} streams)");
        return 0;
    }

    /// <summary>
    /// Inserts a new POU into <paramref name="tree"/>'s catalog + element folder by cloning a donor.
    /// When <paramref name="donor"/> is null, a donor is chosen from the catalog by <paramref name="requireExt"/>
    /// (preferring a program, ElementSubType="0", when <paramref name="preferProgram"/>).
    /// The donor's language stream and its _crc are cloned; the language stream is renamed but its
    /// body is NOT rewritten (the caller is expected to regenerate it).
    /// </summary>
    /// <summary>
    /// Creates a catalog entry for a POU from scratch (no donor) using the entry shape a
    /// TriStation XML import produces, and seeds the element folder with an empty language
    /// stream so the caller can regenerate it. The blank/base project ships without any
    /// FBD element to clone (its whole element library is ST), so importing an FBD POU
    /// must synthesise the entry.
    /// </summary>
    public static InsertResult InsertSynth(
        List<(string Path, byte[] Data)> tree,
        string newName,
        bool isProgram,
        string ext)
    {
        int px = tree.FindIndex(t => t.Path.Equals("Current/Project.xml", StringComparison.OrdinalIgnoreCase));
        if (px < 0) throw new InvalidOperationException("no Current/Project.xml in template");
        var doc = XDocument.Parse(Encoding.UTF8.GetString(tree[px].Data), LoadOptions.PreserveWhitespace);
        var root = doc.Root ?? throw new InvalidOperationException("no root element");

        var elements = root.Descendants().FirstOrDefault(e =>
            e.Name.LocalName == "Collection" && (string?)e.Attribute("Name") == "Elements")
            ?? throw new InvalidOperationException("no Elements collection in Project.xml");

        var entries = elements.Elements().Where(e => e.Name.LocalName == "Class").ToList();
        if (entries.Any(e => string.Equals((string?)e.Attribute("ElemName"), newName, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"new POU name already exists: {newName}");

        int newDog = 1;
        foreach (var e in entries)
            if (int.TryParse((string?)e.Attribute("DogTag"), out int d) && d >= newDog) newDog = d + 1;
        string newFName = newDog.ToString("x8");

        bool st = ext.Equals(".ST", StringComparison.OrdinalIgnoreCase);
        var entry = new XElement("Class",
            new XAttribute("CategoryID", "10097"),
            new XAttribute("ResourceID", st ? "10164" : "10162"),
            new XAttribute("IsNewElement", "False"),
            new XAttribute("PlatformLock1", "False"),
            new XAttribute("PlatformLock2", "False"),
            new XAttribute("ElementType", isProgram ? "1" : "2"),
            new XAttribute("ElementSubType", isProgram ? "0" : "6"),
            new XAttribute("LibID", "1"),
            new XAttribute("ElemName", newName),
            new XAttribute("DescShort", ""),
            new XAttribute("Description", ""),
            new XAttribute("FName", newFName),
            new XAttribute("Extension", st ? ".ST" : ".FBD"),
            new XAttribute("Owner", "1"),
            new XAttribute("Locked", "False"),
            new XAttribute("LockOwner", "0"),
            new XAttribute("DogTag", newDog.ToString()),
            new XAttribute("Flags", isProgram ? "1083423" : (st ? "1048607" : "1050655")),
            new XAttribute("PPDFlags", "0"),
            new XAttribute("EMOCRC32", "0"),
            // Matches the FBD document header we write and the shape TriStation itself gives a
            // regenerated element: 3 = FBD function block with logic, 513 = FBD program,
            // 2 = ST / faceplate-only element.
            new XAttribute("ExtOptionFlags", st ? "2" : (isProgram ? "513" : "3")),
            new XAttribute("CemEventInputs", "0"),
            new XAttribute("CemActionOutputs", "0"));
        string now = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
        string none = "1970-01-01 00:00:00";
        entry.Add(new XElement("Class",
            new XAttribute("Embed", "ElemVersion"), new XAttribute("ClassType", "CTS2Version"),
            new XAttribute("VerMajor", isProgram ? "1" : "1"), new XAttribute("VerMinor", isProgram ? "0" : "1"),
            new XAttribute("VerLocked", "False"), new XAttribute("ProjectFileStatus", "0"),
            new XAttribute("eventType", "0"), new XAttribute("eventId", "0"),
            new XAttribute("date", now), new XAttribute("user", "1")));
        foreach (var ev in new[] { "EventCompiled", "EventConnectionsChanged", "EventLocked" })
            entry.Add(new XElement("Class",
                new XAttribute("Embed", ev), new XAttribute("ClassType", "CTS2Event"),
                new XAttribute("eventType", "0"), new XAttribute("eventId", "0"),
                new XAttribute("date", none), new XAttribute("user", "0")));
        entry.Add(new XElement("Class",
            new XAttribute("Embed", "EventCreated"), new XAttribute("ClassType", "CTS2Event"),
            new XAttribute("eventType", "0"), new XAttribute("eventId", "0"),
            new XAttribute("date", now), new XAttribute("user", "1")));
        entry.Add(new XElement("Class",
            new XAttribute("Embed", "EventPrinted"), new XAttribute("ClassType", "CTS2Event"),
            new XAttribute("eventType", "0"), new XAttribute("eventId", "0"),
            new XAttribute("date", none), new XAttribute("user", "0")));
        entry.Add(new XElement("Class",
            new XAttribute("Embed", "EventModified"), new XAttribute("ClassType", "CTS2Event"),
            new XAttribute("eventType", "0"), new XAttribute("eventId", "0"),
            new XAttribute("date", now), new XAttribute("user", "1")));
        entry.Add(new XElement("Class",
            new XAttribute("Embed", "RefTrigger"), new XAttribute("ClassType", "CTS2XrefXml"),
            new XAttribute("DogTag", "0"), new XAttribute("EventId", "0"), new XAttribute("Date", none)));
        entry.Add(new XElement("Collection",
            new XAttribute("Name", "XrefList"), new XAttribute("ClassType", "CTS2Xref"),
            new XAttribute("CollectionType", "1")));
        entry.Add(new XElement("Collection",
            new XAttribute("Name", "UnresolvedTagnames"), new XAttribute("ClassType", "xCString"),
            new XAttribute("CollectionType", "2"), new XAttribute("IsMemberBase", "1")));
        entry.Add(new XElement("Collection",
            new XAttribute("Name", "Declarations"), new XAttribute("ClassType", "CTS2Vars"),
            new XAttribute("CollectionType", "1")));
        elements.Add(entry);
        // TriStation stores NextElementDogTag as the last used tag (not last + 1).
        root.SetAttributeValue("NextElementDogTag", newDog.ToString());

        tree[px] = ("Current/Project.xml", SerializeProject(root));

        string streamExt = st ? ".ST" : ".FBD";
        var streams = new List<(string, byte[])>
        {
            ($"Current/Elements/{newName}/{newFName}{streamExt}", Array.Empty<byte>()),
            ($"Current/Elements/{newName}/{newFName}{streamExt}_crc", Array.Empty<byte>()),
        };
        tree.AddRange(streams);
        return new InsertResult
        {
            DogTag = newDog,
            FName = newFName,
            Extension = streamExt,
            Donor = "",
            Streams = streams.Select(s => s.Item1).ToList(),
        };
    }

    /// <summary>
    /// Removes a catalog entry (and reports its FName) so an element folder can be
    /// rebuilt deterministically against a blank base.
    /// </summary>
    public static string? Remove(List<(string Path, byte[] Data)> tree, string name)
    {
        int px = tree.FindIndex(t => t.Path.Equals("Current/Project.xml", StringComparison.OrdinalIgnoreCase));
        if (px < 0) return null;
        var doc = XDocument.Parse(Encoding.UTF8.GetString(tree[px].Data), LoadOptions.PreserveWhitespace);
        var root = doc.Root;
        if (root == null) return null;
        var elements = root.Descendants().FirstOrDefault(e =>
            e.Name.LocalName == "Collection" && (string?)e.Attribute("Name") == "Elements");
        if (elements == null) return null;
        var entry = elements.Elements().FirstOrDefault(e =>
            e.Name.LocalName == "Class" &&
            string.Equals((string?)e.Attribute("ElemName"), name, StringComparison.OrdinalIgnoreCase));
        if (entry == null) return null;
        string? fname = (string?)entry.Attribute("FName");
        entry.Remove();
        int maxDog = 0;
        foreach (var e in elements.Elements().Where(e => e.Name.LocalName == "Class"))
            if (int.TryParse((string?)e.Attribute("DogTag"), out int d) && d > maxDog) maxDog = d;
        root.SetAttributeValue("NextElementDogTag", maxDog.ToString());
        tree[px] = ("Current/Project.xml", SerializeProject(root));
        return fname;
    }

    public static InsertResult Insert(
        List<(string Path, byte[] Data)> tree,
        string newName,
        string? donor = null,
        string? requireExt = null,
        bool preferProgram = true,
        int? requireElementType = null)
    {
        int px = tree.FindIndex(t => t.Path.Equals("Current/Project.xml", StringComparison.OrdinalIgnoreCase));
        if (px < 0) throw new InvalidOperationException("no Current/Project.xml in template");

        string xml = Encoding.UTF8.GetString(tree[px].Data);
        var doc = XDocument.Parse(xml, LoadOptions.PreserveWhitespace);
        var root = doc.Root ?? throw new InvalidOperationException("no root element");

        var entries = root.Descendants()
            .Where(e => e.Name.LocalName == "Class" && e.Attribute("ElemName") != null).ToList();

        if (entries.Any(e => (string?)e.Attribute("ElemName") == newName))
            throw new InvalidOperationException($"new POU name already exists: {newName}");

        XElement src;
        if (donor != null)
        {
            src = entries.FirstOrDefault(e => (string?)e.Attribute("ElemName") == donor)
                  ?? throw new InvalidOperationException($"donor POU not found in catalog: {donor}");
        }
        else if (requireExt != null)
        {
            var cand = entries.Where(e =>
                string.Equals((string?)e.Attribute("Extension"), requireExt, StringComparison.OrdinalIgnoreCase)).ToList();
            if (requireElementType.HasValue)
                cand = cand.Where(e => string.Equals((string?)e.Attribute("ElementType"), requireElementType.Value.ToString(), StringComparison.OrdinalIgnoreCase)).ToList();
            if (preferProgram)
                src = cand.FirstOrDefault(e => string.Equals((string?)e.Attribute("ElementSubType"), "0", StringComparison.OrdinalIgnoreCase) && string.Equals((string?)e.Attribute("ElementType"), "1", StringComparison.OrdinalIgnoreCase))
                      ?? cand.FirstOrDefault(e => string.Equals((string?)e.Attribute("ElementType"), "1", StringComparison.OrdinalIgnoreCase))
                      ?? cand.FirstOrDefault();
            else
                src = cand.FirstOrDefault(e => !string.Equals((string?)e.Attribute("ElementType"), "1", StringComparison.OrdinalIgnoreCase))
                      ?? cand.FirstOrDefault();
            if (src == null)
            {
                // No same-language donor exists (a blank base ships only ST library
                // elements): synthesise the entry the way TriStation's importer does.
                return InsertSynth(tree, newName, requireElementType == 1, requireExt);
            }
        }
        else throw new InvalidOperationException("donor or requireExt must be supplied");

        int maxDog = 0;
        foreach (var e in entries)
            if (int.TryParse((string?)e.Attribute("DogTag"), out int d) && d > maxDog) maxDog = d;
        int newDog = maxDog + 1;
        string newFName = newDog.ToString("x8");
        string ext = (string?)src.Attribute("Extension") ?? ".FBD";

        // ---- catalog entry clone ----
        var clone = new XElement(src);
        clone.SetAttributeValue("ElemName", newName);
        clone.SetAttributeValue("DogTag", newDog.ToString());
        clone.SetAttributeValue("FName", newFName);
        clone.SetAttributeValue("Owner", "1");
        clone.SetAttributeValue("EMOCRC32", "0");

        var parent = src.Parent ?? throw new InvalidOperationException("donor has no parent");
        var nextSibling = parent.Elements("Class")
            .FirstOrDefault(e => int.TryParse((string?)e.Attribute("DogTag"), out int d) && d > newDog);
        if (nextSibling != null) nextSibling.AddBeforeSelf(clone);
        else parent.Add(clone);

        root.SetAttributeValue("NextElementDogTag", newDog.ToString());

        tree[tree.FindIndex(t => t.Path.Equals("Current/Project.xml", StringComparison.OrdinalIgnoreCase))] =
            ("Current/Project.xml", SerializeProject(root));

        // ---- element streams clone (language stream + its _crc only) ----
        string donorName = (string?)src.Attribute("ElemName") ?? "";
        string donorFName = (string?)src.Attribute("FName") ?? "";
        string oldPrefix = $"Current/Elements/{donorName}/";
        string newPrefix = $"Current/Elements/{newName}/";
        var additions = new List<(string Path, byte[] Data)>();
        foreach (var (p, d) in tree)
        {
            if (!p.StartsWith(oldPrefix, StringComparison.OrdinalIgnoreCase)) continue;
            string leaf = p[oldPrefix.Length..];
            string leafExt = (donorFName.Length > 0 && leaf.StartsWith(donorFName, StringComparison.OrdinalIgnoreCase))
                ? leaf[donorFName.Length..] : leaf;
            if (!leafExt.Equals(ext, StringComparison.OrdinalIgnoreCase) &&
                !leafExt.Equals(ext + "_crc", StringComparison.OrdinalIgnoreCase)) continue;
            additions.Add((newPrefix + newFName + leafExt, RenameInElement(p, d, donorName, newName)));
        }
        if (additions.Count == 0)
            throw new InvalidOperationException($"donor {donorName} has no {ext} stream under {oldPrefix}");
        tree.AddRange(additions);

        return new InsertResult
        {
            DogTag = newDog,
            FName = newFName,
            Extension = ext,
            Donor = donorName,
            Streams = additions.Select(a => a.Path).ToList(),
        };
    }

    /// <summary>
    /// Rewrites the POU's own name inside a cloned element stream:
    /// FBD root attribute pouName="..." and the IST/ST declaration line
    /// (FUNCTION_BLOCK|FUNCTION|PROGRAM &lt;name&gt;). Binary streams pass through.
    /// </summary>
    private static byte[] RenameInElement(string path, byte[] data, string src, string neu)
    {
        string ext = Path.GetExtension(path.Split('/').Last()).ToLowerInvariant();
        if (ext is not (".fbd" or ".ist" or ".st" or ".doc")) return (byte[])data.Clone();

        string text = Encoding.UTF8.GetString(data);
        string esc = System.Text.RegularExpressions.Regex.Escape(src);
        text = System.Text.RegularExpressions.Regex.Replace(text, $"pouName=\"{esc}\"", $"pouName=\"{neu}\"");
        text = System.Text.RegularExpressions.Regex.Replace(
            text, $"(?m)^(?<kw>FUNCTION_BLOCK|FUNCTION|PROGRAM)(?<ws>\\s+){esc}\\b",
            m => m.Groups["kw"].Value + m.Groups["ws"].Value + neu);
        return Encoding.UTF8.GetBytes(text);
    }
}
