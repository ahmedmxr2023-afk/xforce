using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Pt2Conv.Cfb;

namespace Pt2Conv;

public static class BuildXml
{
    public sealed class ManifestDoc
    {
        public string source { get; set; } = "";
        public int version { get; set; }
        public List<StreamRecord> streams { get; set; } = new();
        public sealed class StreamRecord
        {
            public string path { get; set; } = "";
            public long size { get; set; }
            public string sha256 { get; set; } = "";
        }
    }

    public static int Run(string[] args)
    {
        if (args.Length < 3)
        {
            Console.Error.WriteLine("usage: buildxml <xml> <out.pt2x> [<refExtractDir>] [<pouFilter>] [--tags] [--insert] [--export]");
            Console.Error.WriteLine("  pouFilter: comma-separated POU names to regenerate (default: all POUs from the XML present in the template).");
            Console.Error.WriteLine("  --tags:   also regenerate Current/Tagnames/Tagnames.csv from the XML.");
            Console.Error.WriteLine("  --insert: insert XML POUs missing from the template (clone donor entry + language stream).");
            Console.Error.WriteLine("  --export: address mode export (preserve PhysicalAddress verbatim)");
            Console.Error.WriteLine("  --assume-multi-write: enable EnableMultipleWrite for tags written by more than one program");
            return 1;
        }
        var pos = args.Skip(1).Where(a => !a.StartsWith("--", StringComparison.Ordinal)).ToList();
        string xmlPath = pos[0];

        // Pre-flight: say up front what this XML contains that the builder cannot represent.
        var preflight = Capabilities.Analyze(xmlPath);
        string? toolchain = Toolchain.CompatibilityNote();
        if (toolchain != null) Console.WriteLine("  toolchain: " + toolchain);
        bool force = args.Any(a => a.Equals("--force", StringComparison.OrdinalIgnoreCase));
        if (!preflight.CanConvert || preflight.Warnings.Count > 0)
        {
            Console.WriteLine($"  pre-flight: {preflight.Pous} POU(s), {preflight.Tags} tag(s), "
                              + $"{preflight.Blockers.Count} blocker(s), {preflight.Warnings.Count} warning(s)");
            foreach (var b in preflight.Blockers) Console.WriteLine("    X " + b);
            foreach (var w in preflight.Warnings) Console.WriteLine("    ! " + w);
        }
        if (!preflight.CanConvert && !force)
        {
            Console.Error.WriteLine("Conversion refused: the XML contains blocking items (use --force to try anyway).");
            return 3;
        }
        string outPath = pos[1];
        string refDir = pos.Count >= 3 && !string.IsNullOrWhiteSpace(pos[2]) ? pos[2] : null;
        string? filter = pos.Count >= 4 ? pos[3] : null;
        bool regenTags = args.Any(a => a.Equals("--tags", StringComparison.OrdinalIgnoreCase));
        bool insertMissing = args.Any(a => a.Equals("--insert", StringComparison.OrdinalIgnoreCase));
        bool assumeMultiWrite = args.Any(a => a.Equals("--assume-multi-write", StringComparison.OrdinalIgnoreCase));
        var addrMode = args.Any(a => a.Equals("--export", StringComparison.OrdinalIgnoreCase))
            ? TagGen.AddrMode.Export : TagGen.AddrMode.Import;

        bool useEmbeddedBase = false;
        if (refDir == null)
        {
            refDir = EmbeddedBase.EnsureExtracted();
            useEmbeddedBase = true;
            insertMissing = true;
            regenTags = true;
            Console.WriteLine($"  using embedded base project: {refDir}");
        }

        if (File.Exists(outPath))
        {
            Console.Error.WriteLine("ERROR: output file already exists: " + outPath);
            Console.Error.WriteLine("       Refusing to overwrite. Choose a different output path so a real project file is never destroyed.");
            return 3;
        }

        var tree = new List<(string Path, byte[] Data)>();

        // Load reference manifest if present
        var refMan = Path.Combine(refDir, "_manifest.json");
        if (File.Exists(refMan))
        {
            var man = JsonSerializer.Deserialize<ManifestDoc>(
                File.ReadAllText(refMan), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
            foreach (var r in man.streams)
            {
                string full = Path.Combine(refDir, r.path.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(full))
                    tree.Add((r.path, File.ReadAllBytes(full)));
            }
        }
        else
        {
            foreach (var f in Directory.EnumerateFiles(refDir, "*", SearchOption.AllDirectories))
            {
                string fn = Path.GetFileName(f);
                if (fn == "_manifest.json") continue;
                string rel = Path.GetRelativePath(refDir, f).Replace('\\', '/');
                tree.Add((rel, File.ReadAllBytes(f)));
            }
        }

        // Rewrite CRCs (preserve orphan meta_crc)
        RewriteCrcs(tree);

        // The embedded base was captured against an older TriStation library. Rebase every
        // library element to the installed library's identity (dogTag + FName/streams), so the
        // catalogue, the .EMA binaries and the block snapshots all speak the same ids.
        RebaseLibraryElements(tree);
        AdoptSheetTemplates(tree);

        // Snapshot of the template tree (after CRC normalisation) for the fast path: when the
        // converted project ends up identical to the template, the template container can be
        // reused as-is instead of being re-packed with a different sector layout.
        var baseStreams = tree.ToDictionary(t => t.Path, t => (byte[])t.Data.Clone(), StringComparer.OrdinalIgnoreCase);
        string? baseSource = null;
        if (File.Exists(refMan))
        {
            try
            {
                var man = JsonSerializer.Deserialize<ManifestDoc>(
                    File.ReadAllText(refMan), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (!string.IsNullOrWhiteSpace(man?.source) && File.Exists(man.source)) baseSource = man.source;
            }
            catch { /* optional */ }
        }

        // Remember which user elements the embedded base ships with (LibID=1, ElementType 1/2;
        // library elements have LibID 6..8), so foreign POUs can be stripped from the output.
        var baseUserNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (useEmbeddedBase)
        {
            int bp = tree.FindIndex(t => t.Path.Equals("Current/Project.xml", StringComparison.OrdinalIgnoreCase));
            if (bp >= 0)
            {
                var bd = XDocument.Parse(Encoding.UTF8.GetString(tree[bp].Data), LoadOptions.PreserveWhitespace);
                foreach (var e in bd.Descendants())
                {
                    if (e.Name.LocalName != "Class") continue;
                    var et = e.Attribute("ElementType");
                    var lib = e.Attribute("LibID");
                    var nm = e.Attribute("ElemName");
                    if (et != null && (et.Value == "1" || et.Value == "2") && lib?.Value == "1"
                        && nm != null && !string.IsNullOrEmpty(nm.Value))
                        baseUserNames.Add(nm.Value);
                }
            }
        }

        // Regenerate every user POU's language stream from the PLCopen XML.
        var wants = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (filter != null)
        {
            foreach (var f in filter.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                wants.Add(f);
        }

        var doc = XmlRepair.Load(xmlPath);
        var pouEls = doc.Descendants().Where(e => e.Name.LocalName == "pou").ToList();

        var referencedTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in doc.Descendants())
        {
            var tn = e.Attribute("typeName");
            if (tn != null && !string.IsNullOrEmpty(tn.Value)) referencedTypes.Add(tn.Value);
        }
        // Duplicate POU elements in the export (same name, identical body) must only be
        // generated once, otherwise we overwrite the stream with the same content twice.
        var pouNames = pouEls
            .Select(e => (string?)e.Attribute("name"))
            .Where(n => !string.IsNullOrEmpty(n))
            .Select(n => n!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Guard: the XML's program POU must exist in the template, otherwise the
        // template belongs to a different project and the result mixes foreign
        // catalog/compiled data with the XML's sources -> TriStation rejects it.
        var programNames = pouEls
            .Where(e => string.Equals((string?)e.Attribute("pouType"), "program", StringComparison.OrdinalIgnoreCase))
            .Where(e => e.Descendants().Any(d => d.Name.LocalName == "body"))
            .Select(e => (string?)e.Attribute("name"))
            .Where(n => !string.IsNullOrEmpty(n))
            .Select(n => n!)
            .ToList();
        if (programNames.Count > 0)
        {
            // A matching template contains at least one of the XML's program POUs.
            // (Some PLCopen exports carry template/helper programs that TriStation
            // does not persist as elements, so "all present" is too strict.)
            bool anyPresent = programNames.Any(n => TryGetElementDir(tree, n, out _, out _));
            if (!anyPresent && !insertMissing)
                throw new InvalidOperationException(
                    "Template mismatch: none of the XML's program POU(s) [" + string.Join(", ", programNames) + "] " +
                    "are present under " + Path.Combine(refDir, "Current", "Elements") + ".\n" +
                    "This template belongs to a different project. Extract the matching .pt2x and use that as the template.");
        }

        var tagDesc = new Dictionary<string, FbdGenerator.TagInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in TagGen.TagTypes(xmlPath))
            tagDesc[kv.Key] = new FbdGenerator.TagInfo(kv.Value.Type, kv.Value.Desc);
        LoadLibraryFromTemplate(tree);
        // TriStation's importer creates function blocks first, then the programs; within the
        // programs it appends them in reverse export order (verified against a reference
        // import: P01_FB, TEMPLATES, SeimensLogic). DogTags/stream FNames follow that order.
        var programSet = new HashSet<string>(programNames, StringComparer.OrdinalIgnoreCase);
        var orderedPous = pouNames.Where(p => !programSet.Contains(p)).ToList();
        orderedPous.AddRange(pouNames.Where(programSet.Contains).Reverse());

        // Element creation is a separate pass because a program that instantiates a project
        // function block has to reference that block's *final* catalogue identity (DogTag +
        // eventConnectionsChanged id). Those identities only exist once every element has been
        // inserted, so nothing may be generated before the whole element set is in place.
        if (insertMissing)
        {
            foreach (var pou in orderedPous.Where(p => filter == null || wants.Contains(p)))
            {
                if (TryGetElementDir(tree, pou, out _, out _)) continue;
                // The CFB writer folds directory names case-insensitively, so two element
                // folders differing only by case would collapse into one. Skip instead of
                // emitting a catalog entry whose stream path cannot exist.
                if (tree.Any(t => t.Path.StartsWith("Current/Elements/" + pou + "/", StringComparison.OrdinalIgnoreCase)))
                {
                    Console.WriteLine($"  skip {pou}: element name collides case-insensitively with an existing element");
                    continue;
                }
                try
                {
                    bool isProgram = programNames.Contains(pou);
                    // Imported POUs always use the importer's catalog shape; a donor clone
                    // would carry the library element's flags/LibID and not match a real
                    // TriStation import.
                    var r = AddPou.InsertSynth(tree, pou, isProgram, "." + (PromotesToSt(xmlPath, pou) ? "ST" : "FBD"));
                    Console.WriteLine($"  insert {pou}: DogTag {r.DogTag} FName {r.FName} ({r.Streams.Count} stream(s))");
                }
                catch (Exception ex) { Console.WriteLine($"  skip {pou}: {ex.Message}"); }
            }
        }

        EnsureProjectTypeEvents(tree, orderedPous, xmlPath);
        // Snapshots for project-defined function blocks: a program that instantiates FB04_DI needs
        // that FB's catalogue identity and interface in its own FBD stream, otherwise TriStation
        // shows the instance as unresolved.
        var projectTypes = BuildProjectTypes(tree, xmlPath, pouEls);

        foreach (var pou in orderedPous.Where(p => filter == null || wants.Contains(p)))
        {
            string lang = PromotesToSt(xmlPath, pou) ? "ST" : "FBD";
            if (!TryGetElementDir(tree, pou, out var dirPrefix, out var storedName))
            {
                if (!insertMissing) { Console.WriteLine($"  skip {pou}: not in template"); continue; }
                // The CFB writer folds directory names case-insensitively, so two element
                // folders differing only by case would collapse into one. Skip instead of
                // emitting a catalog entry whose stream path cannot exist.
                if (tree.Any(t => t.Path.StartsWith("Current/Elements/" + pou + "/", StringComparison.OrdinalIgnoreCase)))
                {
                    Console.WriteLine($"  skip {pou}: element name collides case-insensitively with an existing element");
                    continue;
                }
                try
                {
                    bool isProgram = programNames.Contains(pou);
                    // Imported POUs always use the importer's catalog shape; a donor clone
                    // would carry the library element's flags/LibID and not match a real
                    // TriStation import.
                    var r = AddPou.InsertSynth(tree, pou, isProgram, "." + lang);
                    Console.WriteLine($"  insert {pou}: DogTag {r.DogTag} FName {r.FName} ({r.Streams.Count} stream(s))");
                }
                catch (Exception ex) { Console.WriteLine($"  skip {pou}: {ex.Message}"); continue; }
                TryGetElementDir(tree, pou, out dirPrefix, out storedName);
            }
            // The element folder owns the catalogue name. Emit the diagram under that spelling so
            // the POU stream and the project catalogue agree (the XML may differ only by case).
            string emitName = storedName ?? pou;
            try
            {
                var target = tree.FindIndex(t => t.Path.StartsWith(dirPrefix, StringComparison.Ordinal) &&
                                                 t.Path.EndsWith("." + lang, StringComparison.OrdinalIgnoreCase));
                if (target < 0) { Console.WriteLine($"  skip {pou}: no {lang} stream"); continue; }
                // The exporter writes one <pou> element per stored diagram copy: a POU that
                // appears twice is stored as primary + secondary sheet bodies. The decompiler
                // emits a single definition and marks the stored-twice case with a comment.
                bool duplicated = pouEls.Count(e => string.Equals((string?)e.Attribute("name"), pou, StringComparison.OrdinalIgnoreCase)) > 1
                    || pouEls.Any(e => string.Equals((string?)e.Attribute("name"), pou, StringComparison.OrdinalIgnoreCase)
                                       && e.Nodes().OfType<System.Xml.Linq.XComment>()
                                           .Any(c => c.Value.Contains("ts:diagramCopies=2", StringComparison.Ordinal)));
                byte[] gen = lang == "FBD"
                    ? FbdGenerator.GenerateBytes(xmlPath, emitName, tagDesc, referencedTypes, CountSheets(tree[target].Data), duplicated, projectTypes)
                    : StGenerator.Generate(xmlPath, emitName);
                long oldLen = tree[target].Data.Length;
                byte[] templateBytes = tree[target].Data;
                // The template's streams carry the *old* library/event identities (snapshot
                // eventIds, compiler line maps). Once the catalogue is rebased to the installed
                // library they are stale and TriStation shows the element with a red X, so every
                // POU is regenerated from the XML instead of reusing a matching template stream.
                bool unchanged = false;
                if (unchanged)
                {
                    // Same content as the template project: keep its exact stream (localIds,
                    // network handles, emission order, timestamps) instead of regenerating.
                    Console.WriteLine($"  {pou}: {(lang == "FBD" ? "FBD" : "ST ")} unchanged (template kept)");
                    continue;
                }
                tree[target] = (tree[target].Path, gen);
                if (lang == "FBD") PopulateUnresolvedTagnames(tree, emitName, Encoding.UTF8.GetString(gen));
                ReportDroppedPlacements(doc, pou, lang);

                // TriStation keeps the compiler's ST listing beside every FBD element (.IST).
                // The listing is also what records the element's temp variables, so an element
                // without it never counts as compiled.
                // TriStation keeps the compiler's ST listing only for function blocks; programs
                // have no .IST.
                if (lang == "FBD" && !programNames.Contains(pou, StringComparer.OrdinalIgnoreCase))
                    WriteListing(tree, tree[target].Path, xmlPath, emitName);

                // TriStation also stores a CTS2Doc for ST POUs (the documentation page);
                // FBD POUs have none in the reference import.
                if (lang == "ST")
                {
                    string stPath = tree[target].Path;
                    string docPath = stPath[..^3] + ".DOC";
                    byte[] docBytes = DocGenerator.Generate(xmlPath, emitName);
                    int di = tree.FindIndex(t => t.Path.Equals(docPath, StringComparison.OrdinalIgnoreCase));
                    if (di >= 0) tree[di] = (tree[di].Path, docBytes);
                    else
                    {
                        tree.Add((docPath, docBytes));
                        tree.Add((docPath + "_crc", Array.Empty<byte>()));
                    }
                }
                Console.WriteLine($"  {pou}: {(lang == "FBD" ? "FBD" : "ST ")} {oldLen} -> {gen.Length} B");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  skip {pou}: {ex.Message}");
            }
        }

        // Remove leftover base user elements that the XML does not define (only when using
        // the embedded base; with a user-supplied template, its own POUs are intentional).
        if (useEmbeddedBase && baseUserNames.Count > 0)
        {
            var toRemove = baseUserNames.Where(b => !pouNames.Contains(b, StringComparer.OrdinalIgnoreCase)).ToList();
            if (toRemove.Count > 0)
            {
                RemoveBaseElements(tree, toRemove);
                Console.WriteLine($"  removed base element(s): {string.Join(", ", toRemove)}");
            }
        }

        // Phase 2: regenerate the tag database from the XML's <ts:TAGNAME> blocks.
        if (regenTags)
        {
            int ti = tree.FindIndex(t => t.Path.Equals("Current/Tagnames/Tagnames.csv", StringComparison.OrdinalIgnoreCase));
            if (ti < 0) Console.WriteLine("  tags: template has no Current/Tagnames/Tagnames.csv; skipped");
            else
            {
                try
                {
                    IReadOnlySet<string>? multiWriters = assumeMultiWrite ? Semantics.MultipleWriteTags(doc) : null;
                    string csv = TagGen.GenerateCsv(xmlPath, Encoding.UTF8.GetString(tree[ti].Data), addrMode, out string report, multiWriters);
                    tree[ti] = (tree[ti].Path, new UTF8Encoding(false).GetBytes(csv));
                    Console.Write(report);
                }
                catch (Exception ex) { Console.WriteLine($"  tags: skipped ({ex.Message})"); }
            }
        }

        // Phase 2b: adopt the project's library binaries from the XML's LIBRARY_DATA blobs.
        // TriStation embeds each library as a nested compound file; when the XML comes from a
        // project whose libraries differ from the template's, the template binaries would link
        // the wrong (older) library. Same-size streams are left alone because TriStation keeps
        // its own copy for the current install.
        AdoptLibraryBinaries(tree, doc);
        // Replace the library archives with the installed (updated) ones. The XML export carries
        // the library as it was in the source project; TriStation rewrites those archives when it
        // updates the library, and the compiler flags any other build as "library needs update".
        ApplyLibraryArchives(tree);

        // The importer writes the XML's contentDescription into the project header.
        string contentDescription = doc.Descendants()
            .FirstOrDefault(e => e.Name.LocalName == "fileHeader")
            ?.Attribute("contentDescription")?.Value ?? "";
        if (contentDescription.Length > 0) SetProjectDescription(tree, contentDescription);

        // The execution list is what actually schedules the programs; an empty one makes the
        // build stop with WB0237 "no programs specified in the implementation document's
        // execution list" even when every element is correct.
        SetExecutionList(tree, doc);

        // Phase 2c: rebuild the hardware configuration when the XML's chassis/board layout differs
        // from the template's (module setup blocks are module-library data, captured per module id).
        HwConfig.Apply(tree, doc);

        // The chassis carries a name on every used point. Names inherited from the template
        // describe the *old* project's tags at the *old* addresses; keeping them next to the
        // regenerated tag table makes TriStation report the same tagname twice ("PC0119 ...
        // duplicate tagname ... found and removed") when the project is opened. Rebuild the
        // point names from the tag table so each name exists exactly once. This also allocates
        // the module point offsets the tag bitmap is derived from, so it must run first.
        RenameHardwarePoints(tree);

        // The point-allocation bitmap is what TriStation consults when it validates tag addresses
        // on open. Leaving the template's bitmap in place makes it re-allocate our tags and report
        // duplicate/invalid addresses (TN0090/TN0116/TN0026), so rebuild it from the final table.
        RebuildGlobalMemory(tree);

        // Recompute CRC for newly generated streams.
        RewriteCrcs(tree);
        Program.RewriteGlobalCrc(tree);
        var dirOut = Path.GetDirectoryName(outPath);
        if (!string.IsNullOrEmpty(dirOut)) Directory.CreateDirectory(dirOut);

        bool identical = tree.Count == baseStreams.Count && tree.All(t =>
            baseStreams.TryGetValue(t.Path, out var b) && t.Data.AsSpan().SequenceEqual(b));
        if (identical)
        {
            if (useEmbeddedBase)
            {
                File.WriteAllBytes(outPath, EmbeddedBase.LoadPt2xBytes());
                Console.WriteLine($"Wrote {outPath} (unchanged: template container reused)");
                return 0;
            }
            if (baseSource != null && !string.Equals(Path.GetFullPath(baseSource), Path.GetFullPath(outPath), StringComparison.OrdinalIgnoreCase))
            {
                File.Copy(baseSource, outPath);
                Console.WriteLine($"Wrote {outPath} (unchanged: template container reused)");
                return 0;
            }
        }

        CompoundFileWriter.Write(outPath, tree);
        Console.WriteLine($"Wrote {outPath} ({tree.Count} streams)");
        return 0;
    }

    /// <summary>
    /// Mirrors the importer's per-element xref: a program's declarations whose flags are
    /// 65536 are program-local names not present in the tag database, and TriStation lists
    /// them in the catalog entry's UnresolvedTagnames collection (used by the cross-reference
    /// report). Function blocks have none.
    /// </summary>
    private static void PopulateUnresolvedTagnames(List<(string Path, byte[] Data)> tree, string pou, string fbdXml)
    {
        int ds = fbdXml.IndexOf("<Collection Name=\"declarations\"", StringComparison.Ordinal);
        if (ds < 0) return;
        int de = fbdXml.IndexOf("</Collection>", ds, StringComparison.Ordinal);
        var decl = de < 0 ? fbdXml[ds..] : fbdXml[ds..de];
        var names = new List<string>();
        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(decl,
                     "<Class name=\"([^\"]*)\"[^>]*? flags=\"65536\""))
        {
            string n = System.Net.WebUtility.HtmlDecode(m.Groups[1].Value);
            if (n.Length > 0) names.Add(n);
        }

        int px = tree.FindIndex(t => t.Path.Equals("Current/Project.xml", StringComparison.OrdinalIgnoreCase));
        if (px < 0) return;
        var doc = XDocument.Parse(Encoding.UTF8.GetString(tree[px].Data), LoadOptions.PreserveWhitespace);
        var entry = doc.Descendants().FirstOrDefault(e =>
            e.Name.LocalName == "Class" &&
            string.Equals((string?)e.Attribute("ElemName"), pou, StringComparison.OrdinalIgnoreCase));
        if (entry == null) return;
        var col = entry.Elements().FirstOrDefault(e =>
            e.Name.LocalName == "Collection" && (string?)e.Attribute("Name") == "UnresolvedTagnames");
        if (col == null) return;
        var existing = col.Elements().Where(e => e.Name.LocalName == "Class")
            .Select(e => (string?)e.Attribute("Value") ?? "").ToList();
        if (existing.Count == names.Count && existing.SequenceEqual(names, StringComparer.Ordinal)) return;
        col.RemoveNodes();
        foreach (var n in names) col.Add(new XElement("Class", new XAttribute("Value", n)));
        tree[px] = ("Current/Project.xml", AddPou.SerializeProject(doc.Root!));
    }

    /// <summary>Writes (or refreshes) the <c>.IST</c> listing stream beside an FBD element.</summary>
    private static void WriteListing(List<(string Path, byte[] Data)> tree, string fbdPath, string xmlPath, string pou)
    {
        string istPath = fbdPath[..^4] + ".IST";
        byte[] bytes = Encoding.UTF8.GetBytes(FbdGenerator.BuildListing(xmlPath, pou));
        int i = tree.FindIndex(t => t.Path.Equals(istPath, StringComparison.OrdinalIgnoreCase));
        if (i >= 0) tree[i] = (tree[i].Path, bytes);
        else
        {
            tree.Add((istPath, bytes));
            tree.Add((istPath + "_crc", Array.Empty<byte>()));
        }
    }

    /// <summary>Copies the XML's execution list (the programs scheduled by the controller) into the
    /// project's <c>ExecutionList</c> collection, preserving its order.</summary>
    private static void SetExecutionList(List<(string Path, byte[] Data)> tree, XDocument xml)
    {
        var task = xml.Descendants().FirstOrDefault(e =>
            e.Name.LocalName == "task" &&
            string.Equals((string?)e.Attribute("name"), "ExecutionList", StringComparison.OrdinalIgnoreCase));

        var names = task == null
            ? new List<string>()
            : task.Elements()
                .Where(e => e.Name.LocalName == "pouInstance")
                .Where(e => string.Equals((string?)e.Attribute("type"), "program", StringComparison.OrdinalIgnoreCase))
                .Select(e => (string?)e.Attribute("name"))
                .Where(n => !string.IsNullOrEmpty(n))
                .Select(n => n!)
                .ToList();
        // Some exports carry an empty (or missing) execution list. Without one TriStation stops
        // with WB0237, so fall back to scheduling every program the XML defines.
        if (names.Count == 0)
            names = xml.Descendants()
                .Where(e => e.Name.LocalName == "pou")
                .Where(e => string.Equals((string?)e.Attribute("pouType"), "program", StringComparison.OrdinalIgnoreCase))
                .Where(e => e.Descendants().Any(d => d.Name.LocalName == "body"))
                .Select(e => (string?)e.Attribute("name"))
                .Where(n => !string.IsNullOrEmpty(n))
                .Select(n => n!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        if (names.Count == 0) return;

        // A program with no executable elements cannot be scheduled: TriStation stops with
        // WG0009 ("PROGRAM x must contain at least one executable element"). Empty programs are
        // common leftovers in exports (e.g. a scratch program called "added").
        var executable = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pou in xml.Descendants().Where(e => e.Name.LocalName == "pou"))
        {
            var body = pou.Descendants().FirstOrDefault(e => e.Name.LocalName == "body");
            if (body == null) continue;
            bool hasLogic = body.Descendants().Any(e => e.Name.LocalName is "block" or "inVariable" or "inOutVariable"
                or "outVariable" or "contact" or "coil" or "step" or "transition" or "action" or "jump" or "return")
                || (body.Descendants().FirstOrDefault(e => e.Name.LocalName == "ST")?.Value ?? "").Trim().Length > 0;
            string pname = (string?)pou.Attribute("name") ?? "";
            if (hasLogic) executable.Add(pname);
        }
        names = names.Where(n => executable.Contains(n)).ToList();
        if (names.Count == 0) return;

        int px = tree.FindIndex(t => t.Path.Equals("Current/Project.xml", StringComparison.OrdinalIgnoreCase));
        if (px < 0) return;
        XDocument doc;
        try { doc = XDocument.Parse(Encoding.UTF8.GetString(tree[px].Data), LoadOptions.PreserveWhitespace); }
        catch { return; }

        var list = doc.Descendants().FirstOrDefault(e =>
            e.Name.LocalName == "Collection" && (string?)e.Attribute("Name") == "ExecutionList");
        if (list == null) return;

        // The catalogue stores the element's name; the XML may spell it differently in case
        // ("Templates" vs "TEMPLATES"). An execution list entry that does not match the catalogue
        // makes TriStation rewrite the element on open, so schedule the stored spelling.
        var catalogue = doc.Descendants()
            .Where(e => e.Name.LocalName == "Class" && (string?)e.Attribute("ElemName") != null)
            .Select(e => (string)e.Attribute("ElemName")!)
            .ToList();
        names = names
            .Select(n => catalogue.FirstOrDefault(c => string.Equals(c, n, StringComparison.OrdinalIgnoreCase)) ?? n)
            .ToList();

        list.ReplaceNodes(names.Select(n => new XElement("Class", new XAttribute("Value", n))));
        tree[px] = ("Current/Project.xml", AddPou.SerializeProject(doc.Root!));
    }

    /// <summary>Sets every hardware point's <c>Name</c> from the regenerated tag table (by its
    /// <c>CC.SS.PP</c> address) and clears the names of points that carry no tag. Stale template
    /// names would otherwise collide with the tag table when TriStation opens the project.</summary>
    private static void RenameHardwarePoints(List<(string Path, byte[] Data)> tree)
    {
        int hi = tree.FindIndex(t => t.Path.Equals("Current/Elements/Configuration/HwConfiguration.xml", StringComparison.OrdinalIgnoreCase));
        int ti = tree.FindIndex(t => t.Path.Equals("Current/Tagnames/Tagnames.csv", StringComparison.OrdinalIgnoreCase));
        if (hi < 0 || ti < 0) return;

        var byAddress = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string[] lines;
        try { lines = Encoding.UTF8.GetString(tree[ti].Data).Split('\n'); }
        catch { return; }
        if (lines.Length < 3) return;
        char[] quotes = { '"' };
        var header = lines[1].TrimEnd('\r').Split(new[] { "\",\"" }, StringSplitOptions.None)
            .Select(x => x.Trim(quotes)).ToList();
        int nameCol = header.IndexOf("Tagname");
        int addrCol = header.IndexOf("Address");
        if (nameCol < 0 || addrCol < 0) return;
        foreach (var raw in lines.Skip(2))
        {
            var cells = raw.TrimEnd('\r').Split(new[] { "\",\"" }, StringSplitOptions.None)
                .Select(x => x.Trim(quotes)).ToList();
            if (cells.Count <= Math.Max(nameCol, addrCol)) continue;
            string addr = cells[addrCol], name = cells[nameCol];
            if (addr.Length > 0 && name.Length > 0) byAddress[addr] = name;
        }

        XDocument doc;
        try { doc = XDocument.Parse(Encoding.UTF8.GetString(tree[hi].Data), LoadOptions.PreserveWhitespace); }
        catch { return; }

        // Memory sections (MIN/MBN/MBR/...) are not under a chassis: each section is a virtual
        // board whose BoardPoint equals the element index. Map the section order from
        // GlobalMemory.xml and name each point from the tag at that address.
        var segOfPrefix = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        int gi = tree.FindIndex(t => t.Path.Equals("Current/Elements/Configuration/GlobalMemory.xml", StringComparison.OrdinalIgnoreCase));
        if (gi >= 0)
        {
            try
            {
                var gm = XDocument.Parse(Encoding.UTF8.GetString(tree[gi].Data));
                var sm = gm.Descendants().FirstOrDefault(e => e.Name.LocalName == "Collection" && (string?)e.Attribute("Name") == "SectionMap");
                if (sm != null)
                {
                    int idx = 0;
                    foreach (var sec in sm.Elements().Where(e => e.Name.LocalName == "Class"))
                        segOfPrefix[(string?)sec.Attribute("Description") ?? ""] = idx++;
                }
            }
            catch { }
        }
        var memName = new Dictionary<(int Seg, int Off), string>();
        foreach (var kv in byAddress)
        {
            var m = System.Text.RegularExpressions.Regex.Match(kv.Key, @"^([A-Za-z]+)\.(\d+)$");
            if (!m.Success) continue;
            if (!segOfPrefix.TryGetValue(m.Groups[1].Value, out int seg)) continue;
            if (int.TryParse(m.Groups[2].Value, out int num) && num > 0)
                memName[(seg, num - 1)] = kv.Value;
        }
        if (memName.Count > 0)
        {
            var memSegs = new HashSet<int>(memName.Keys.Select(k => k.Seg));
            foreach (var point in doc.Descendants().Where(e => e.Name.LocalName == "Class" && e.Attribute("BoardPoint") != null))
            {
                int seg = int.TryParse((string?)point.Attribute("Segment"), out int s1) ? s1 : -1;
                if (!memSegs.Contains(seg)) continue;
                if (!int.TryParse((string?)point.Attribute("BoardPoint"), out int bp)) continue;
                point.SetAttributeValue("Offset", bp.ToString(CultureInfo.InvariantCulture));
                point.SetAttributeValue("Size", "0");
                point.SetAttributeValue("Name", memName.TryGetValue((seg, bp), out var mn) ? mn : "");
            }
        }

        int named = 0;
        // Module point offsets are allocated per segment across every chassis, in chassis/slot
        // order, one unit per point. TriStation derives a tag's memory word from its module point
        // offset, so a different scheme makes distinct points collapse onto the same word and the
        // compiler reports overlapping tag addresses (PB0002/PB0003).
        var cursors = new Dictionary<int, int>();
        var chassisList = doc.Descendants()
            .Where(e => e.Name.LocalName == "Class" && e.Attribute("ChassisNum") != null)
            .Select(e => (El: e, Num: int.TryParse((string?)e.Attribute("ChassisNum"), out int cn) ? cn : int.MinValue))
            .Where(t => t.Num >= 0)
            .OrderBy(t => t.Num);
        foreach (var (chassis, ch) in chassisList)
        {
            // Same slot convention as the boards: main/HD_EXP are 0-based, LD_EXP 1-based.
            string cname = (string?)chassis.Attribute("Name") ?? "";
            bool zeroBased = cname.Contains("MAIN", StringComparison.OrdinalIgnoreCase)
                             || cname.Contains("HD_EXP", StringComparison.OrdinalIgnoreCase);
            var boardList = chassis.Descendants()
                .Where(e => e.Name.LocalName == "Class" && e.Attribute("SlotID") != null)
                .Select(e => (El: e, Slot: int.TryParse((string?)e.Attribute("SlotID"), out int s) ? s : int.MinValue))
                .Where(t => t.Slot >= 0)
                .OrderBy(t => t.Slot);
            foreach (var (board, slot) in boardList)
            {
                foreach (var point in board.Descendants().Where(e => e.Name.LocalName == "Class" && e.Attribute("BoardPoint") != null))
                {
                    if (!int.TryParse((string?)point.Attribute("BoardPoint"), out int bp)) continue;

                    int addrSlot = zeroBased ? slot + 1 : slot;
                    string key = string.Format(CultureInfo.InvariantCulture, "{0:00}.{1:00}.{2:00}", ch, addrSlot, bp + 1);
                    string? name = byAddress.TryGetValue(key, out var n) ? n : null;
                    if (name != null) named++;

                    int seg = int.TryParse((string?)point.Attribute("Segment"), out int s0) ? s0 : 0;
                    cursors.TryGetValue(seg, out int cursor);
                    point.SetAttributeValue("Offset", cursor.ToString(CultureInfo.InvariantCulture));
                    point.SetAttributeValue("Size", "0");
                    cursors[seg] = cursor + 1;

                    if (name != null) point.SetAttributeValue("Name", name);
                    else if (point.Attribute("Name") != null) point.SetAttributeValue("Name", "");
                }
            }
        }

        tree[hi] = (tree[hi].Path, Encoding.UTF8.GetBytes(
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\r\n" + doc.Root!.ToString(SaveOptions.DisableFormatting) + "\r\n"));
        Console.WriteLine($"  hardware: point names rebuilt from the tag table ({named} named)");
    }

    /// <summary>Compiled stamps of the installed library, captured once as data (they are constant
    /// per library version, like the hardware module catalogue).</summary>
    private sealed record CompiledStamp(int Id, string Date, int User, string Flags, string RefDog, string RefEvent, string RefDate);

    // The captured JSON writes numbers as strings; accept either representation.
    private static string StrOf(JsonElement e, string name, string fallback)
        => e.TryGetProperty(name, out var v)
            ? (v.ValueKind == JsonValueKind.String ? (v.GetString() ?? fallback) : v.GetRawText())
            : fallback;

    private static int IntOf(JsonElement e, string name)
        => int.TryParse(StrOf(e, name, "0"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) ? n : 0;

    private static readonly Lazy<Dictionary<string, CompiledStamp>> _compiledStamps = new(() =>
    {
        var map = new Dictionary<string, CompiledStamp>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var stream = typeof(BuildXml).Assembly
                .GetManifestResourceStream("Pt2Conv.Resources.library_compiled.json");
            if (stream == null) return map;
            using var reader = new StreamReader(stream, Encoding.UTF8);
            using var doc = JsonDocument.Parse(reader.ReadToEnd());
            foreach (var p in doc.RootElement.EnumerateObject())
            {
                var v = p.Value;
                map[p.Name] = new CompiledStamp(
                    IntOf(v, "id"),
                    StrOf(v, "date", "1970-01-01 00:00:00"),
                    IntOf(v, "user"),
                    StrOf(v, "flags", ""),
                    StrOf(v, "refDog", ""),
                    StrOf(v, "refEvent", ""),
                    StrOf(v, "refDate", ""));
            }
        }
        catch { /* optional data: the library version check simply stays as-is */ }
        return map;
    });

    private static Dictionary<string, CompiledStamp> CompiledStamps => _compiledStamps.Value;

    /// <summary>Installs the installed library archives (<c>Current/Bin/*.EMA</c>) from the resources
    /// captured for this TriStation library version. The XML carries the source project's copy,
    /// which the compiler considers older than the installed library.</summary>
    private static void ApplyLibraryArchives(List<(string Path, byte[] Data)> tree)
    {
        var asm = typeof(BuildXml).Assembly;
        foreach (var (file, res) in new[]
                 {
                     ("STDLIB.EMA", "Pt2Conv.Resources.lib.stdlib.ema.gz"),
                     ("TCXLIB.EMA", "Pt2Conv.Resources.lib.tcxlib.ema.gz"),
                     ("TX1LIB.EMA", "Pt2Conv.Resources.lib.tx1lib.ema.gz"),
                 })
        {
            using var stream = asm.GetManifestResourceStream(res);
            if (stream == null) continue;
            using var gz = new System.IO.Compression.GZipStream(stream, System.IO.Compression.CompressionMode.Decompress);
            using var mem = new MemoryStream();
            gz.CopyTo(mem);
            byte[] bytes = mem.ToArray();

            string path = "Current/Bin/" + file;
            int i = tree.FindIndex(t => t.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
            if (i >= 0) tree[i] = (tree[i].Path, bytes);
            else
            {
                tree.Add((path, bytes));
                tree.Add((path + "_crc", Array.Empty<byte>()));
            }
        }
    }

    /// <summary>Rebases every library element in the catalogue to the installed library's
    /// identity: DogTag, the hex FName that mirrors it, RefTrigger, and the element's stream files.</summary>
    /// <summary>Canonical catalogue records of the installed library's elements, captured once as
    /// data (constant per library version, like the hardware module catalogue).</summary>
    private static readonly Lazy<Dictionary<string, string>> _libraryRecords = new(() =>
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var stream = typeof(BuildXml).Assembly
                .GetManifestResourceStream("Pt2Conv.Resources.library_records.json");
            if (stream == null) return map;
            using var reader = new StreamReader(stream, Encoding.UTF8);
            using var doc = JsonDocument.Parse(reader.ReadToEnd());
            foreach (var p in doc.RootElement.EnumerateObject())
                map[p.Name] = p.Value.GetString() ?? "";
        }
        catch { /* optional data */ }
        return map;
    });

    private static Dictionary<string, string> LibraryRecords => _libraryRecords.Value;

    private sealed record LibVersion(int Major, int Minor);

    private static readonly Lazy<Dictionary<string, LibVersion>> _libraryVersions = new(() =>
    {
        var map = new Dictionary<string, LibVersion>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var stream = typeof(BuildXml).Assembly
                .GetManifestResourceStream("Pt2Conv.Resources.library_versions.json");
            if (stream == null) return map;
            using var reader = new StreamReader(stream, Encoding.UTF8);
            using var doc = JsonDocument.Parse(reader.ReadToEnd());
            foreach (var p in doc.RootElement.EnumerateObject())
                map[p.Name] = new LibVersion(
                    p.Value.GetProperty("major").GetInt32(),
                    p.Value.GetProperty("minor").GetInt32());
        }
        catch { /* optional data */ }
        return map;
    });

    private static Dictionary<string, LibVersion> LibraryVersions => _libraryVersions.Value;

    /// <summary>Maps a point coordinate ("CC.SS.PP") to the module point's allocated offset using
    /// the current <c>HwConfiguration.xml</c>.</summary>
    private static Dictionary<string, int> ReadCoordinateOffsets(List<(string Path, byte[] Data)> tree)
    {
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        int hi = tree.FindIndex(t => t.Path.Equals("Current/Elements/Configuration/HwConfiguration.xml", StringComparison.OrdinalIgnoreCase));
        if (hi < 0) return map;
        XDocument doc;
        try { doc = XDocument.Parse(Encoding.UTF8.GetString(tree[hi].Data)); }
        catch { return map; }
        foreach (var chassis in doc.Descendants().Where(e => e.Name.LocalName == "Class" && e.Attribute("ChassisNum") != null))
        {
            if (!int.TryParse((string?)chassis.Attribute("ChassisNum"), out int ch) || ch < 0) continue;
            string cname = (string?)chassis.Attribute("Name") ?? "";
            bool zeroBased = cname.Contains("MAIN", StringComparison.OrdinalIgnoreCase)
                             || cname.Contains("HD_EXP", StringComparison.OrdinalIgnoreCase);
            foreach (var board in chassis.Descendants().Where(e => e.Name.LocalName == "Class" && e.Attribute("SlotID") != null))
            {
                if (!int.TryParse((string?)board.Attribute("SlotID"), out int slot) || slot < 0) continue;
                foreach (var point in board.Descendants().Where(e => e.Name.LocalName == "Class" && e.Attribute("BoardPoint") != null))
                {
                    if (!int.TryParse((string?)point.Attribute("BoardPoint"), out int bp)) continue;
                    if (!int.TryParse((string?)point.Attribute("Offset"), out int off)) continue;
                    int addrSlot = zeroBased ? slot + 1 : slot;
                    map[string.Format(CultureInfo.InvariantCulture, "{0:00}.{1:00}.{2:00}", ch, addrSlot, bp + 1)] = off;
                }
            }
        }
        return map;
    }

    /// <summary>Rebuilds the point-allocation bitmap (<c>GlobalMemory.xml</c>) from the final tag
    /// table. TriStation validates/recomputes tag addresses against this bitmap when a project is
    /// opened; a stale template bitmap causes duplicate or invalid point addresses on build.</summary>
    private static void RebuildGlobalMemory(List<(string Path, byte[] Data)> tree)
    {
        int gi = tree.FindIndex(t => t.Path.Equals("Current/Elements/Configuration/GlobalMemory.xml", StringComparison.OrdinalIgnoreCase));
        int ti = tree.FindIndex(t => t.Path.Equals("Current/Tagnames/Tagnames.csv", StringComparison.OrdinalIgnoreCase));
        if (gi < 0 || ti < 0) return;

        var lines = Encoding.UTF8.GetString(tree[ti].Data).Replace("\r", "").Split('\n');
        if (lines.Length < 2) return;
        var header = SplitCsvLine(lines[1]);
        int ai = header.FindIndex(h => h.Equals("Address", StringComparison.OrdinalIgnoreCase));
        int pi = header.FindIndex(h => h.Equals("PhysicalAddress", StringComparison.OrdinalIgnoreCase));
        if (ai < 0) return;

        // Coordinate point (CC.SS.PP) -> the module point's allocated offset, taken from the
        // (already renamed/offset) hardware configuration. TriStation derives an I/O tag's memory
        // word from that offset, so the allocation bitmap must mark the same index.
        var coordinateOffset = ReadCoordinateOffsets(tree);

        var tags = new Dictionary<string, HashSet<int>>(StringComparer.OrdinalIgnoreCase);
        for (int i = 2; i < lines.Length; i++)
        {
            if (lines[i].Length == 0) continue;
            var f = SplitCsvLine(lines[i]);
            if (ai >= f.Count) continue;
            string address = f[ai];
            string physical = pi >= 0 && pi < f.Count ? f[pi] : "";
            // An aliased I/O tag: Address is the point coordinate, PhysicalAddress names the memory
            // class it resolves to. Mark that class at the point's offset.
            var coord = System.Text.RegularExpressions.Regex.Match(address ?? "", @"^\d+\.\d+\.\d+$");
            if (coord.Success && coordinateOffset.TryGetValue(address!, out int off))
            {
                var piM = System.Text.RegularExpressions.Regex.Match(physical ?? "", @"^([A-Za-z]+)\.");
                if (piM.Success)
                {
                    string pre = piM.Groups[1].Value;
                    if (!tags.TryGetValue(pre, out var ps)) tags[pre] = ps = new HashSet<int>();
                    ps.Add(off);
                }
            }
            foreach (string addr in new[] { address, physical })
            {
                var m = System.Text.RegularExpressions.Regex.Match(addr ?? "", @"^([A-Za-z]+)\.(\d+)$");
                if (!m.Success) continue;
                string prefix = m.Groups[1].Value;
                if (!int.TryParse(m.Groups[2].Value, out int num) || num < 1) continue;
                if (!tags.TryGetValue(prefix, out var set)) tags[prefix] = set = new HashSet<int>();
                set.Add(num - 1);
            }
        }

        XDocument doc;
        try { doc = XDocument.Parse(Encoding.UTF8.GetString(tree[gi].Data), LoadOptions.PreserveWhitespace); }
        catch { return; }
        var sectionMap = doc.Descendants().FirstOrDefault(e =>
            e.Name.LocalName == "Collection" && (string?)e.Attribute("Name") == "SectionMap");
        if (sectionMap == null) return;

        foreach (var sec in sectionMap.Elements().Where(e => e.Name.LocalName == "Class"))
        {
            string desc = (string?)sec.Attribute("Description") ?? "";
            var memMap = sec.Elements().FirstOrDefault(e => e.Name.LocalName == "Collection" && (string?)e.Attribute("Name") == "MemoryMap")
                ?.Elements().FirstOrDefault(e => e.Name.LocalName == "Class");
            if (memMap == null) continue;
            int items = int.TryParse((string?)memMap.Attribute("NumOfItems"), out int n) && n > 0 ? n : 0;
            if (items == 0)
            {
                var old = (string?)memMap.Attribute("PacketDump") ?? "";
                items = old.Length / 2;
            }
            if (items == 0) continue;
            var bits = new byte[items];
            if (tags.TryGetValue(desc, out var set))
                foreach (int idx in set)
                    if (idx / 8 < bits.Length) bits[idx / 8] |= (byte)(1 << (idx % 8));
            var sb = new StringBuilder(bits.Length * 2);
            foreach (byte b in bits) sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
            memMap.SetAttributeValue("PacketDump", sb.ToString());
        }

        var text = "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\r\n" + doc.Root!.ToString(SaveOptions.DisableFormatting);
        tree[gi] = (tree[gi].Path, new UTF8Encoding(false).GetBytes(text));
    }

    private static List<string> SplitCsvLine(string line)
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

    /// <summary>Replaces the embedded base's sheet-template elements with the identities and
    /// drawings of the installed STDSHEET library. The base was captured with an older STDSHEET,
    /// so its DogTag/FName no longer exist in the installed library; TriStation then shows
    /// "sheet template no longer exists in this project" and cannot open the drawings.</summary>
    private static void AdoptSheetTemplates(List<(string Path, byte[] Data)> tree)
    {
        int px = tree.FindIndex(t => t.Path.Equals("Current/Project.xml", StringComparison.OrdinalIgnoreCase));
        if (px < 0) return;
        XDocument doc;
        try { doc = XDocument.Parse(Encoding.UTF8.GetString(tree[px].Data), LoadOptions.PreserveWhitespace); }
        catch { return; }
        var elements = doc.Descendants().FirstOrDefault(e =>
            e.Name.LocalName == "Collection" && (string?)e.Attribute("Name") == "Elements");
        if (elements == null) return;

        JsonDocument res;
        try
        {
            using var s = typeof(BuildXml).Assembly.GetManifestResourceStream("Pt2Conv.Resources.stdsheet.json");
            if (s == null) return;
            using var reader = new StreamReader(s, Encoding.UTF8);
            res = JsonDocument.Parse(reader.ReadToEnd());
        }
        catch { return; }

        int adopted = 0;
        foreach (var prop in res.RootElement.EnumerateObject())
        {
            string name = prop.Name;
            var e = elements.Elements().FirstOrDefault(x =>
                x.Name.LocalName == "Class" &&
                string.Equals((string?)x.Attribute("ElemName"), name, StringComparison.OrdinalIgnoreCase));
            if (e == null) continue;

            string oldF = (string?)e.Attribute("FName") ?? "";
            string newF = prop.Value.GetProperty("fName").GetString() ?? oldF;
            string ext = prop.Value.GetProperty("ext").GetString() ?? ".BKG";
            if (oldF.Length == 0 || newF.Length == 0) continue;

            e.SetAttributeValue("DogTag", IntText(prop.Value, "dogTag"));
            e.SetAttributeValue("FName", newF);
            e.SetAttributeValue("Flags", prop.Value.GetProperty("flags").GetString() ?? "10");
            var ver = e.Elements().FirstOrDefault(x =>
                x.Name.LocalName == "Class" && (string?)x.Attribute("Embed") == "ElemVersion");
            if (ver != null)
            {
                ver.SetAttributeValue("VerMajor", IntText(prop.Value, "verMajor"));
                ver.SetAttributeValue("VerMinor", IntText(prop.Value, "verMinor"));
                ver.SetAttributeValue("eventId", IntText(prop.Value, "eventId"));
                if (prop.Value.TryGetProperty("date", out var dt) && dt.GetString() is { Length: > 0 } ds)
                    ver.SetAttributeValue("date", ds);
                ver.SetAttributeValue("user", IntText(prop.Value, "user"));
            }

            byte[] bkg = Convert.FromBase64String(prop.Value.GetProperty("bkg").GetString() ?? "");
            string dirPrefix = "Current/Elements/" + name + "/";
            for (int i = 0; i < tree.Count; i++)
            {
                if (!tree[i].Path.StartsWith(dirPrefix, StringComparison.OrdinalIgnoreCase)) continue;
                string rel = tree[i].Path[dirPrefix.Length..];
                bool crc = rel.EndsWith("_crc", StringComparison.OrdinalIgnoreCase);
                string stem = crc ? rel[..^4] : rel;
                int dot = stem.IndexOf('.');
                string fileStem = dot > 0 ? stem[..dot] : stem;
                string fileExt = dot > 0 ? stem[dot..] : "";
                if (!fileStem.Equals(oldF, StringComparison.OrdinalIgnoreCase)) continue;
                string newRel = newF + fileExt + (crc ? "_crc" : "");
                var data = crc ? Array.Empty<byte>() : bkg;
                tree[i] = (dirPrefix + newRel, data);
            }
            adopted++;
        }

        if (adopted > 0)
        {
            var text = "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\r\n" + doc.Root!.ToString(SaveOptions.DisableFormatting);
            tree[px] = ("Current/Project.xml", Encoding.UTF8.GetBytes(text));
            Console.WriteLine($"  sheet templates adopted from installed STDSHEET ({adopted})");
        }
    }

    private static string IntText(JsonElement e, string name)
        => e.TryGetProperty(name, out var v)
            ? (v.ValueKind == JsonValueKind.Number ? v.GetRawText() : (v.GetString() ?? "0"))
            : "0";

    private static void RebaseLibraryElements(List<(string Path, byte[] Data)> tree)
    {
        int px = tree.FindIndex(t => t.Path.Equals("Current/Project.xml", StringComparison.OrdinalIgnoreCase));
        if (px < 0) return;
        XDocument doc;
        try { doc = XDocument.Parse(Encoding.UTF8.GetString(tree[px].Data), LoadOptions.PreserveWhitespace); }
        catch { return; }

        var tags = FbdGenerator.LibraryDogTags();
        var elements = doc.Descendants().FirstOrDefault(e =>
            e.Name.LocalName == "Collection" && (string?)e.Attribute("Name") == "Elements");
        if (elements == null) return;

        var renames = new List<(string Name, string OldF, string NewF)>();
        var dogMap = new Dictionary<int, int>();
        bool changed = false;
        // DogTags/FNames must stay unique across the whole catalogue. The captured library data
        // can name a value that another element already owns (e.g. REAL_TO_DINT -> 99 collides
        // with the STDSHEET sheets at 93..100); applying it hides the sheet templates and makes
        // TriStation ask "no longer exists in this project" and fail to read the project.
        var usedDog = new HashSet<int>();
        var usedF = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in elements.Elements().Where(e => e.Name.LocalName == "Class"))
        {
            if (int.TryParse((string?)c.Attribute("DogTag"), out int d)) usedDog.Add(d);
            string f = (string?)c.Attribute("FName") ?? "";
            if (f.Length > 0) usedF.Add(f);
        }
        foreach (var c in elements.Elements().Where(e => e.Name.LocalName == "Class"))
        {
            string lib = (string?)c.Attribute("LibID") ?? "";
            if (lib != "6" && lib != "7" && lib != "8") continue;
            string name = (string?)c.Attribute("ElemName") ?? "";
            if (name.Length == 0 || !tags.TryGetValue(name, out int newDog)) continue;

            if (int.TryParse((string?)c.Attribute("DogTag"), out int curDog) && curDog != newDog)
            {
                string canon = newDog.ToString("x8", CultureInfo.InvariantCulture);
                string oldF0 = (string?)c.Attribute("FName") ?? "";
                bool collides = (usedDog.Contains(newDog) && curDog != newDog)
                                || (oldF0.Length > 0 && usedF.Contains(canon) && !oldF0.Equals(canon, StringComparison.OrdinalIgnoreCase));
                if (!collides)
                {
                    usedDog.Remove(curDog);
                    usedF.Remove(oldF0);
                    dogMap[curDog] = newDog;
                    c.SetAttributeValue("DogTag", newDog.ToString(CultureInfo.InvariantCulture));
                    if (oldF0.Length > 0 && !oldF0.Equals(canon, StringComparison.OrdinalIgnoreCase))
                    {
                        c.SetAttributeValue("FName", canon);
                        renames.Add((name, oldF0, canon));
                    }
                    usedDog.Add(newDog);
                    usedF.Add(canon);
                }
            }

            var trigger = c.Elements().FirstOrDefault(x => x.Name.LocalName == "Class" && (string?)x.Attribute("Embed") == "RefTrigger");
            trigger?.SetAttributeValue("DogTag", newDog.ToString(CultureInfo.InvariantCulture));

            // The importer leaves the per-element object CRC at 0 for library elements; the
            // embedded base carries CRCs computed against its older build.
            c.SetAttributeValue("EMOCRC32", "0");

            // The library archive records a compiled stamp per element; an element left at
            // EventCompiled 0 is reported as out of date (WI0013) against the installed library.
            if (CompiledStamps.TryGetValue(name, out var stamp))
            {
                var compiled = c.Elements().FirstOrDefault(x =>
                    x.Name.LocalName == "Class" && (string?)x.Attribute("Embed") == "EventCompiled");
                if (compiled != null)
                {
                    compiled.SetAttributeValue("eventId", stamp.Id.ToString(CultureInfo.InvariantCulture));
                    compiled.SetAttributeValue("date", stamp.Date);
                    compiled.SetAttributeValue("user", stamp.User.ToString(CultureInfo.InvariantCulture));
                }
                if (stamp.Flags.Length > 0) c.SetAttributeValue("Flags", stamp.Flags);
                if (trigger != null && stamp.RefDog.Length > 0)
                {
                    trigger.SetAttributeValue("DogTag", stamp.RefDog);
                    trigger.SetAttributeValue("EventId", stamp.RefEvent);
                    trigger.SetAttributeValue("Date", stamp.RefDate);
                }
            }
            changed = true;
        }

        if (!changed) return;

        // Cross-reference lists inside each library element record the dogTags of the elements
        // that call it. Those were captured against the old catalogue and must follow the
        // rebase, otherwise the catalogue points at elements that no longer exist.
        if (dogMap.Count > 0)
        {
            foreach (var col in doc.Descendants().Where(e =>
                         e.Name.LocalName == "Collection" && (string?)e.Attribute("Name") == "XrefList"))
                foreach (var x in col.Elements().Where(e => e.Name.LocalName == "Class"))
                    if (int.TryParse((string?)x.Attribute("DogTag"), out int od) && dogMap.TryGetValue(od, out int nd))
                        x.SetAttributeValue("DogTag", nd.ToString(CultureInfo.InvariantCulture));
        }

        // External library object list: the base lists five objects per library (its older
        // library had five); the installed library has four. TriStation flags the mismatch as
        // "library needs update", so adopt the installed linkage list.
        try
        {
            using var res = typeof(BuildXml).Assembly.GetManifestResourceStream("Pt2Conv.Resources.library_extlibobj.txt");
            if (res != null)
            {
                using var reader = new StreamReader(res, Encoding.UTF8);
                var replacement = XElement.Parse(reader.ReadToEnd(), LoadOptions.PreserveWhitespace);
                var current = doc.Descendants().FirstOrDefault(e =>
                    e.Name.LocalName == "Collection" && (string?)e.Attribute("Name") == "ExtLibObj");
                if (current != null) current.ReplaceWith(replacement);
            }
        }
        catch { /* optional data */ }

        // The base's library archives predate the installed library, so TriStation offers (and on
        // build, enforces) a library update. The project carries the switch for that check; with
        // the archives already replaced from the source project we do not want a second update.
        doc.Root?.SetAttributeValue("IgnoreUpdateLibrariesDialog", "True");

        // Library versions come from the installed library, not from the base's Description text
        // (TX1LIB shipped as v1.100 while the installed archive is v1.101, which the compiler
        // reports as "element is out-of-date").
        var wantedVersions = LibraryVersions;
        var libraries = doc.Descendants().FirstOrDefault(e =>
            e.Name.LocalName == "Collection" && (string?)e.Attribute("Name") == "Libraries");
        if (libraries != null)
        {
            foreach (var libEl in libraries.Elements().Where(e => e.Name.LocalName == "Class"))
            {
                string libName = (string?)libEl.Attribute("LibName") ?? "";
                if (!wantedVersions.TryGetValue(libName, out var v)) continue;
                var ver = libEl.Elements().FirstOrDefault(x =>
                    x.Name.LocalName == "Class" && (string?)x.Attribute("Embed") == "LibVersion");
                if (ver == null) continue;
                ver.SetAttributeValue("VerMajor", v.Major.ToString(CultureInfo.InvariantCulture));
                ver.SetAttributeValue("VerMinor", v.Minor.ToString(CultureInfo.InvariantCulture));
            }
        }

        foreach (var (name, oldF, newF) in renames)
        {
            string dir = "Current/Elements/" + name + "/";
            for (int i = 0; i < tree.Count; i++)
            {
                string path = tree[i].Path;
                if (!path.StartsWith(dir, StringComparison.OrdinalIgnoreCase)) continue;
                string file = path.Substring(dir.Length);
                if (!file.StartsWith(oldF, StringComparison.OrdinalIgnoreCase)) continue;
                tree[i] = (dir + newF + file.Substring(oldF.Length), tree[i].Data);
            }
        }

        tree[px] = ("Current/Project.xml", AddPou.SerializeProject(doc.Root!));
        Console.WriteLine($"  library: rebased {renames.Count} element identit{(renames.Count == 1 ? "y" : "ies")} to the installed version");
    }

    /// <summary>Sets the project header description, preserving the single-line format.</summary>
    private static void SetProjectDescription(List<(string Path, byte[] Data)> tree, string description)
    {
        int px = tree.FindIndex(t => t.Path.Equals("Current/Project.xml", StringComparison.OrdinalIgnoreCase));
        if (px < 0) return;
        var doc = XDocument.Parse(Encoding.UTF8.GetString(tree[px].Data), LoadOptions.PreserveWhitespace);
        if (doc.Root == null) return;
        if ((string?)doc.Root.Attribute("Description") == description) return;
        doc.Root.SetAttributeValue("Description", description);
        tree[px] = ("Current/Project.xml", AddPou.SerializeProject(doc.Root));
    }

    /// <summary>
    /// Rewrites the catalogue record of every POU this build regenerates so that it looks exactly
    /// like an element TriStation itself accepted: one coherent event history.
    ///
    /// TriStation resolves a block instance by the pair (DogTag, eventConnectionsChanged) and, in
    /// addition, validates the element's whole event block. Records that mix a months-old
    /// ElemVersion/EventModified with a brand-new connections event (or a zero compiled/RefTrigger
    /// entry) are reported as "out-of-date element" and drawn with a red cross. The real projects
    /// TriStation writes always keep created &lt; version &lt; modified &lt; connectionsChanged on a
    /// single timestamp, with EventCompiled and RefTrigger filled in.
    /// </summary>
    private static void EnsureProjectTypeEvents(List<(string Path, byte[] Data)> tree, List<string> pouNames, string xmlPath)
    {
        int px = tree.FindIndex(t => t.Path.Equals("Current/Project.xml", StringComparison.OrdinalIgnoreCase));
        if (px < 0) return;
        XDocument doc;
        try { doc = XDocument.Parse(Encoding.UTF8.GetString(tree[px].Data), LoadOptions.PreserveWhitespace); }
        catch { return; }

        var wanted = new HashSet<string>(pouNames, StringComparer.OrdinalIgnoreCase);
        var entries = doc.Descendants()
            .Where(c => c.Name.LocalName == "Class" && c.Attribute("ElemName") != null)
            .ToList();
        // Event ids must come from the project's own (small, sequential) range. TriStation
        // renumbers any element whose event id is outside that range when it rebuilds, which
        // silently invalidates every instance snapshot that pointed at the old id.
        int nextEvent = 1;
        if (int.TryParse((string?)doc.Root?.Attribute("NextEventDogTag"), out int seed) && seed > nextEvent)
            nextEvent = seed;
        // Existing catalogue entries already use ids that may be above the header's counter
        // (the embedded base was captured mid-edit). Never hand out an id already in use:
        // an element whose event id is above NextEventDogTag is treated as corrupt.
        int maxEventId = 0;
        foreach (var ev in doc.Descendants().Where(e =>
                     e.Name.LocalName == "Class" && e.Attribute("Embed") != null && e.Attribute("eventId") != null
                     && wanted.Contains((string?)e.Parent?.Attribute("ElemName") ?? "")))
            if (int.TryParse((string?)ev.Attribute("eventId"), out int used) && used > maxEventId) maxEventId = used;
        if (maxEventId + 1 > nextEvent) nextEvent = maxEventId + 1;

        string now = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        bool changed = false;

        static XElement? Embedded(XElement element, string embed) =>
            element.Elements().FirstOrDefault(x => x.Name.LocalName == "Class" && (string?)x.Attribute("Embed") == embed);

        static int EventId(XElement? e, int fallback = 0) =>
            e != null && int.TryParse((string?)e.Attribute("eventId"), out int id) && id > 0 ? id : fallback;

        static void Stamp(XElement e, int id, string stamp)
        {
            e.SetAttributeValue("eventType", "0");
            e.SetAttributeValue("eventId", id.ToString(CultureInfo.InvariantCulture));
            e.SetAttributeValue("date", stamp);
            e.SetAttributeValue("user", "1");
        }

        foreach (var c in entries)
        {
            string name = (string?)c.Attribute("ElemName") ?? "";
            if (!wanted.Contains(name)) continue;
            string type = (string?)c.Attribute("ElementType") ?? "";
            if (type != "1" && type != "2") continue;
            bool isProgram = type == "1";

            // A fresh TriStation import writes ExtOptionFlags=2 for every project POU it
            // generates (FBD function blocks included); 3 only appears once the element has
            // been compiled/edited by the editor.
            string want = "2";
            if ((string?)c.Attribute("ExtOptionFlags") != want)
            {
                c.SetAttributeValue("ExtOptionFlags", want);
                changed = true;
            }

            // A TriStation import normalises the catalogue to this shape: generic subtype 0,
            // owner 0, version 1.0, ExtOptionFlags 2, and NO "compiled image" bits. A genuine
            // unbuilt import carries Flags 0x10081F for an FBD function block; the 0x6000 and
            // 0x20040 bits only appear after a successful compile (together with a real
            // EventCompiled id and a compiled database). Claiming that state without the
            // compiled image makes the editor draw the block as out of date (grey box with
            // bare pin stubs), so clear those bits here.
            if ((string?)c.Attribute("ElementSubType") != "0") { c.SetAttributeValue("ElementSubType", "0"); changed = true; }
            if ((string?)c.Attribute("Owner") != "0") { c.SetAttributeValue("Owner", "0"); changed = true; }
            if ((string?)c.Attribute("LockOwner") != "0") { c.SetAttributeValue("LockOwner", "0"); changed = true; }
            if (!isProgram && int.TryParse((string?)c.Attribute("Flags"), out int fl))
            {
                int unbuilt = fl & ~(0x6000 | 0x20000 | 0x40);
                if (unbuilt != fl) { c.SetAttributeValue("Flags", unbuilt.ToString(CultureInfo.InvariantCulture)); changed = true; }
            }

            var created = Embedded(c, "EventCreated");
            var version = Embedded(c, "ElemVersion");
            var modified = Embedded(c, "EventModified");
            var conn = Embedded(c, "EventConnectionsChanged");
            var compiled = Embedded(c, "EventCompiled");
            var refTrigger = Embedded(c, "RefTrigger");
            if (created == null || version == null || modified == null || conn == null) continue;
            if ((string?)version.Attribute("VerMajor") != "1" || (string?)version.Attribute("VerMinor") != "0")
            {
                version.SetAttributeValue("VerMajor", "1");
                version.SetAttributeValue("VerMinor", "0");
                changed = true;
            }

            // Anchor the whole history on the element's own newest timestamp, so a record that
            // was already written by TriStation keeps its date instead of jumping to "now".
            DateTime anchor = DateTime.Now;
            var dates = new[] { created, version, modified }
                .Select(e => (string?)e.Attribute("date"))
                .Where(d => !string.IsNullOrWhiteSpace(d) && !d!.StartsWith("1970", StringComparison.Ordinal))
                .Select(d => DateTime.TryParse(d, CultureInfo.InvariantCulture, DateTimeStyles.None, out var t) ? t : DateTime.MinValue)
                .Where(t => t > DateTime.MinValue)
                .ToList();
            if (dates.Count > 0) anchor = dates.Max();
            string stamp = anchor.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

            int createdId = EventId(created);
            if (createdId == 0) { createdId = nextEvent++; changed = true; }
            int versionId = EventId(version);
            if (versionId == 0) { versionId = nextEvent++; changed = true; }
            int modifiedId = EventId(modified);
            if (modifiedId == 0) { modifiedId = nextEvent++; changed = true; }

            // A program's connections-changed event stays 0 (TriStation does the same); a function
            // block always gets a fresh one after its modification event, so every instance can
            // point at a real interface version.
            int connId = isProgram ? 0 : nextEvent++;
            // TriStation records the compile against the same connections event (or an older one).
            int compiledId = connId > 0 ? connId : nextEvent++;

            Stamp(created, createdId, stamp);
            Stamp(version, versionId, stamp);
            Stamp(modified, modifiedId, stamp);
            if (connId > 0) Stamp(conn, connId, stamp);
            else
            {
                conn.SetAttributeValue("eventType", "0");
                conn.SetAttributeValue("eventId", "0");
                conn.SetAttributeValue("date", "1970-01-01 00:00:00");
                conn.SetAttributeValue("user", "0");
            }
            // Do NOT claim a compiled state: the compiled database (Project.EMA) is not produced by
            // this builder, and an element that advertises EventCompiled/flags without real compiled
            // code makes TriStation skip the compile and fail later with WC0140.
            if (compiled != null)
            {
                compiled.SetAttributeValue("eventType", "0");
                compiled.SetAttributeValue("eventId", "0");
                compiled.SetAttributeValue("date", "1970-01-01 00:00:00");
                compiled.SetAttributeValue("user", "0");
            }
            if (refTrigger != null)
            {
                // No compiled image, so there is no compile trigger to reference: an unbuilt
                // import keeps RefTrigger empty (DogTag/EventId 0).
                refTrigger.SetAttributeValue("DogTag", "0");
                refTrigger.SetAttributeValue("EventId", "0");
                refTrigger.SetAttributeValue("Date", "1970-01-01 00:00:00");
            }

            // Do not set the "built" flags (0x20000 | 0x40): without a real compiled image they only
            // make the compiler skip the element and fail later.
            changed = true;
        }

        if (changed)
        {
            doc.Root?.SetAttributeValue("NextEventDogTag", nextEvent.ToString(CultureInfo.InvariantCulture));
            tree[px] = ("Current/Project.xml", AddPou.SerializeProject(doc.Root!));
        }
    }

    /// <summary>
    /// Collects the interface (pins) of every project function block plus its catalogue identity
    /// (DogTag, ElementType/SubType, flags, event). Used to write the snapshot of an FB instance
    /// inside another POU; without it the instance is unresolved when the project is opened.
    /// </summary>
    private static Dictionary<string, FbdGenerator.ProjectTypeInfo> BuildProjectTypes(
        List<(string Path, byte[] Data)> tree, string xmlPath, List<XElement> pouEls)
    {
        var map = new Dictionary<string, FbdGenerator.ProjectTypeInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in pouEls)
        {
            string name = (string?)e.Attribute("name") ?? "";
            string pouType = (string?)e.Attribute("pouType") ?? "";
            if (name.Length == 0 || pouType == "program") continue;
            try
            {
                map[name] = FbdGenerator.BuildProjectType(xmlPath, name);
                // Do NOT append the compiler temps from our own listing: TriStation's stored
                // interface contains only the declared variables (its compiler adds/updates
                // temps itself when the project is built). Writing a different temp set makes
                // every instance of the block an out-of-date interface, which the editor draws
                // as a red diagonal slash over the block.
            }
            catch { /* a malformed POU keeps the unknown-type fallback */ }
        }
        if (map.Count == 0) return map;

        int px = tree.FindIndex(t => t.Path.Equals("Current/Project.xml", StringComparison.OrdinalIgnoreCase));
        if (px < 0) return map;
        try
        {
            var doc = XDocument.Parse(Encoding.UTF8.GetString(tree[px].Data), LoadOptions.PreserveWhitespace);
            foreach (var c in doc.Descendants().Where(c => c.Name.LocalName == "Class" && c.Attribute("ElemName") != null))
            {
                string n = (string)c.Attribute("ElemName")!;
                if (!map.TryGetValue(n, out var info)) continue;
                if (int.TryParse((string?)c.Attribute("DogTag"), out int dog)) info.DogTag = dog;
                if (int.TryParse((string?)c.Attribute("ElementType"), out int et)) info.ElemType = et;
                if (int.TryParse((string?)c.Attribute("ElementSubType"), out int st)) info.SubType = st;
                if (int.TryParse((string?)c.Attribute("ExtOptionFlags"), out int xo)) info.ExtOptFlags = xo;
                if (int.TryParse((string?)c.Attribute("CemEventInputs"), out int ci)) info.CemEI = ci;
                if (int.TryParse((string?)c.Attribute("CemActionOutputs"), out int co)) info.CemAO = co;
                var ev = c.Elements().FirstOrDefault(x =>
                    x.Name.LocalName == "Class" && (string?)x.Attribute("Embed") == "EventConnectionsChanged");
                if (ev != null)
                {
                    if (int.TryParse((string?)ev.Attribute("eventId"), out int eid)) info.EventId = eid;
                    if (int.TryParse((string?)ev.Attribute("user"), out int usr)) info.User = usr;
                    info.Date = (string?)ev.Attribute("date") ?? "";
                }
                if (Environment.GetEnvironmentVariable("PT2_DEBUG_EVENTS") == "1" && n.Equals("FB04_DI", StringComparison.OrdinalIgnoreCase))
                    Console.Error.WriteLine($"[ev] {n} dog={info.DogTag} type={info.ElemType} conn={info.EventId}");

                // The element's catalogue must carry its interface. TriStation resolves an
                // instance by comparing the block's stored snapshot with this Declarations
                // collection; an entry with no Declarations (our inserted POU) makes every
                // instance an out-of-date interface, which the editor draws as a red diagonal
                // slash over the block.
                var decls = c.Elements().FirstOrDefault(x =>
                    x.Name.LocalName == "Collection" && (string?)x.Attribute("Name") == "Declarations");
                if (decls == null)
                {
                    decls = new XElement(c.Name.Namespace + "Collection",
                        new XAttribute("Name", "Declarations"),
                        new XAttribute("ClassType", "CTS2Vars"),
                        new XAttribute("CollectionType", "1"));
                    c.Add(decls);
                }
                decls.RemoveNodes();
                foreach (var pin in info.Pins)
                {
                    decls.Add(new XElement(c.Name.Namespace + "Class",
                        new XAttribute("name", pin.Name),
                        new XAttribute("dataType", pin.DataType),
                        new XAttribute("initVal", pin.InitVal),
                        new XAttribute("desc", pin.Desc),
                        new XAttribute("varVTType", pin.Vt.ToString(CultureInfo.InvariantCulture)),
                        new XAttribute("refCount", pin.RefCount.ToString(CultureInfo.InvariantCulture)),
                        new XAttribute("fbRefCount", pin.FbRefCount.ToString(CultureInfo.InvariantCulture)),
                        new XAttribute("flags", pin.Flags.ToString(CultureInfo.InvariantCulture)),
                        new XAttribute("writes", "0")));
                }
            }
            tree[px] = ("Current/Project.xml", AddPou.SerializeProject(doc.Root!));
        }
        catch { /* catalogue is best-effort */ }
        return map;
    }

    /// <summary>
    /// Reports drawn placements TriStation cannot represent (empty, numeric, "dir/â€¦", "L%4102",
    /// or reserved names). The importer drops them silently, so surfacing the count keeps the loss
    /// visible instead of letting the conversion look lossless.
    /// </summary>
    private static void ReportDroppedPlacements(XDocument doc, string pou, string lang)
    {
        if (lang != "FBD") return;
        var el = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "pou"
            && string.Equals((string?)e.Attribute("name"), pou, StringComparison.Ordinal));
        if (el == null) return;

        var dropped = new List<string>();
        foreach (var v in el.Descendants().Where(e => e.Name.LocalName is "inVariable" or "outVariable" or "inOutVariable"))
        {
            string expr = v.Elements().FirstOrDefault(e => e.Name.LocalName == "expression")?.Value?.Trim() ?? "";
            bool constant = v.Name.LocalName == "inVariable" &&
                            (double.TryParse(expr, NumberStyles.Float, CultureInfo.InvariantCulture, out _)
                             || expr.StartsWith("16#", StringComparison.OrdinalIgnoreCase)
                             || expr.Equals("TRUE", StringComparison.OrdinalIgnoreCase)
                             || expr.Equals("FALSE", StringComparison.OrdinalIgnoreCase));
            if (!FbdGenerator.IsDeclarableName(expr) && !constant) dropped.Add(expr);
        }
        if (dropped.Count == 0) return;
        var sample = string.Join(", ", dropped.Distinct().Take(4).Select(s => s.Length == 0 ? "(empty)" : s));
        Console.WriteLine($"  {pou}: {dropped.Count} placement(s) with a name TriStation cannot store were dropped ({sample})");
    }

    /// <summary>
    /// Copies the compiled library binaries (<c>Current/Bin/*</c> .EMA/.NCA/.emo/.nco) out of
    /// the XML's embedded <c>LIBRARY_DATA</c> compound files when they differ in size from the
    /// template's copy â€” that means the XML was exported from a TriStation with a different
    /// library build. Same-size streams are kept as-is: TriStation itself keeps the installed
    /// copy on import, so replacing them would only diverge from the reference import.
    /// </summary>
    private static void AdoptLibraryBinaries(List<(string Path, byte[] Data)> tree, XDocument doc)
    {
        var config = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "LIBRARY_CONFIGURATION");
        if (config == null) return;

        var current = tree.ToDictionary(t => t.Path, t => t.Data, StringComparer.OrdinalIgnoreCase);
        foreach (var lib in config.Elements().Where(e => e.Name.LocalName == "LIBRARY"))
        {
            string name = (string?)lib.Attribute("Name") ?? "";
            var dataEl = lib.Elements().FirstOrDefault(e => e.Name.LocalName == "LIBRARY_DATA");
            if (dataEl == null || dataEl.Value.Length == 0) continue;

            byte[] blob;
            try { blob = Convert.FromBase64String(dataEl.Value.Trim()); } catch { continue; }
            if (blob.Length < 8 || blob[0] != 0xD0 || blob[1] != 0xCF) continue;

            List<Pt2Conv.Cfb.StreamContent> streams;
            try { streams = new CompoundFile(blob).ListStreams(); } catch { continue; }
            var bins = streams
                .Where(s => s.Path.StartsWith("Current/Bin/", StringComparison.OrdinalIgnoreCase)
                            && s.Data.Length > 0
                            && !s.Path.EndsWith("_crc", StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (bins.Count == 0) continue;

            // Adopt the whole library when any binary has a different size (newer library build).
            bool adopt = bins.Any(s => !current.TryGetValue(s.Path, out var old) || old.Length != s.Data.Length);
            if (!adopt) continue;

            foreach (var s in bins)
            {
                int i = tree.FindIndex(t => t.Path.Equals(s.Path, StringComparison.OrdinalIgnoreCase));
                if (i >= 0) tree[i] = (tree[i].Path, s.Data);
                else
                {
                    tree.Add((s.Path, s.Data));
                    tree.Add((s.Path + "_crc", Array.Empty<byte>()));
                }
                current[s.Path] = s.Data;
            }
            Console.WriteLine($"  library {name}: adopted {bins.Count} binary stream(s) from the XML");
        }
    }

    private static void RewriteCrcs(List<(string Path, byte[] Data)> streams)
    {
        var byName = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < streams.Count; i++)
            byName[streams[i].Path] = streams[i].Data;

        for (int i = 0; i < streams.Count; i++)
        {
            var p = streams[i].Path;
            if (!p.EndsWith("_crc", StringComparison.OrdinalIgnoreCase)) continue;
            string sib = p[..^4];
            if (!byName.TryGetValue(sib, out var b)) continue;
            streams[i] = (p, Encoding.ASCII.GetBytes(Sha256Hex(b)));
        }
    }

    /// <summary>
    /// Removes the catalog entries and element streams for the given base element names.
    /// Only used when the embedded base is a project template, so that its user POUs do not
    /// remain in the output alongside the XML's own POUs.
    /// </summary>
    private static void RemoveBaseElements(List<(string Path, byte[] Data)> tree, List<string> names)
    {
        var nameSet = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
        var keep = new List<(string Path, byte[] Data)>();
        foreach (var s in tree)
        {
            if (s.Path.Equals("Current/Project.xml", StringComparison.OrdinalIgnoreCase))
            {
                var doc = XDocument.Parse(Encoding.UTF8.GetString(s.Data), LoadOptions.PreserveWhitespace);
                var root = doc.Root;
                if (root != null)
                {
                    var els = root.Descendants().FirstOrDefault(e =>
                        e.Name.LocalName == "Collection" &&
                        (string?)e.Attribute("Name") == "Elements");
                    if (els != null)
                    {
                        foreach (var cls in els.Elements().ToList())
                        {
                            if (cls.Name.LocalName != "Class") continue;
                            string et = cls.Attribute("ElementType")?.Value ?? "";
                            if ((et == "1" || et == "2") &&
                                nameSet.Contains(cls.Attribute("ElemName")?.Value ?? ""))
                                cls.Remove();
                        }
                        int maxDog = 0;
                        foreach (var cls in els.Elements().Where(e => e.Name.LocalName == "Class"))
                            if (int.TryParse(cls.Attribute("DogTag")?.Value, out int d) && d > maxDog) maxDog = d;
                        root.SetAttributeValue("NextElementDogTag", maxDog.ToString());
                    }
                }
                keep.Add((s.Path, AddPou.SerializeProject(root)));
                continue;
            }
            keep.Add(s);
        }
        // remove element streams for stripped programs
        var stripped = new List<(string Path, byte[] Data)>();
        foreach (var s in keep)
        {
            if (s.Path.StartsWith("Current/Elements/", StringComparison.OrdinalIgnoreCase))
            {
                var rest = s.Path.Substring("Current/Elements/".Length);
                var seg = rest.Split('/')[0];
                if (nameSet.Contains(seg)) continue;
            }
            stripped.Add(s);
        }
        tree.Clear();
        tree.AddRange(stripped);
    }

    /// <summary>Learns FBD library-block metadata from every template FBD stream.</summary>
    private static void LoadLibraryFromTemplate(List<(string Path, byte[] Data)> streams)
    {
        var proj = streams.Find(t => t.Path.Equals("Current/Project.xml", StringComparison.OrdinalIgnoreCase));
        if (proj.Data != null)
            try { FbdGenerator.LoadLibraryNames(Encoding.UTF8.GetString(proj.Data)); } catch { }
        foreach (var s in streams)
            if (s.Path.EndsWith(".FBD", StringComparison.OrdinalIgnoreCase))
                FbdGenerator.LoadLibraryFromFbd(Encoding.UTF8.GetString(s.Data));
    }

    /// <summary>Reads Tagname-&gt;(DataType,Description) from the template's Tagnames.csv for FBD declarations.</summary>
    private static Dictionary<string, FbdGenerator.TagInfo> LoadTagDescriptions(List<(string Path, byte[] Data)> streams)
    {
        var d = new Dictionary<string, FbdGenerator.TagInfo>(StringComparer.OrdinalIgnoreCase);
        int ti = streams.FindIndex(t => t.Path.Equals("Current/Tagnames/Tagnames.csv", StringComparison.OrdinalIgnoreCase));
        if (ti < 0) return d;
        var lines = Encoding.UTF8.GetString(streams[ti].Data).Replace("\r", "").Split('\n');
        for (int i = 2; i < lines.Length; i++)
        {
            var ln = lines[i];
            if (ln.Length == 0) continue;
            if (ln.StartsWith("\"")) ln = ln[1..];
            if (ln.EndsWith("\"")) ln = ln[..^1];
            var f = ln.Split("\",\"");
            if (f.Length >= 5) d[f[0]] = new FbdGenerator.TagInfo(f[1], f[4]);
        }
        return d;
    }

    /// <summary>Finds the stream path prefix for element dir <paramref name="pou"/> (e.g. "Current/Elements/FB05_DCS_CMD").</summary>
    private static bool TryGetElementDir(List<(string Path, byte[] Data)> streams, string pou, out string dirPrefix,
        out string? actualName)
    {
        string probe = "Current/Elements/" + pou + "/";
        foreach (var t in streams)
        {
            if (t.Path.StartsWith(probe, StringComparison.Ordinal))
            {
                dirPrefix = probe;
                actualName = pou;
                return true;
            }
        }
        // The CFB writer folds directory names case-insensitively, so "Templates" and "TEMPLATES"
        // are the same element folder. Match it case-insensitively and report the stored spelling;
        // regenerating into the existing folder (with its name) updates that element instead of
        // leaving the template's stale body behind.
        foreach (var t in streams)
        {
            if (!t.Path.StartsWith(probe, StringComparison.OrdinalIgnoreCase)) continue;
            string rel = t.Path.Substring("Current/Elements/".Length);
            int slash = rel.IndexOf('/');
            actualName = slash > 0 ? rel[..slash] : pou;
            dirPrefix = "Current/Elements/" + actualName + "/";
            return true;
        }
        dirPrefix = probe;
        actualName = null;
        return false;
    }

    /// <summary>True if the POU has an ST body (rather than an FBD body).</summary>
    private static bool PromotesToSt(string xmlPath, string pou)
    {
        var d = XmlRepair.Load(xmlPath);
        var p = d.Descendants().FirstOrDefault(e => e.Name.LocalName == "pou" && (string?)e.Attribute("name") == pou);
        return p != null &&
               p.Descendants().FirstOrDefault(e => e.Name.LocalName == "ST") != null &&
               p.Descendants().FirstOrDefault(e => e.Name.LocalName == "FBD") == null;
    }

    /// <summary>Number of sheet entries in an existing FBD stream (0 when unparseable).</summary>
    private static int CountSheets(byte[] data)
    {
        try
        {
            var d = XDocument.Parse(Encoding.UTF8.GetString(data));
            var col = d.Root?.Elements().FirstOrDefault(e =>
                e.Name.LocalName == "Collection" && (string?)e.Attribute("Name") == "sheets");
            return col == null ? 0 : col.Elements().Count(e => e.Name.LocalName == "Class");
        }
        catch { return 0; }
    }

    private static string Sha256Hex(byte[] data)
    {
        using var sha = SHA256.Create();
        var h = sha.ComputeHash(data);
        var sb = new StringBuilder(h.Length * 2);
        foreach (byte x in h) sb.Append(x.ToString("x2"));
        return sb.ToString();
    }
}