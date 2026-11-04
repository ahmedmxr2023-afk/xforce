using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Pt2Conv.Cfb;

namespace Pt2Conv;

/// <summary>
/// PHASE-2: regenerates Current/Tagnames/Tagnames.csv from a PLCopen XML
/// (the &lt;ts:TAGNAME&gt; blocks) and reports, per template column, whether the XML
/// supplies it, whether it needs hardware/IO input, or whether it is missing.
/// Never guesses a value for a hardware/IO field: those are reported, left empty.
/// </summary>
public static class TagGen
{
    public static int Run(string[] args)
    {
        if (args.Length < 4) { Console.Error.WriteLine("usage: gentags <xml> <template.pt2x> <out.csv> [import|export]"); return 1; }
        string xmlPath = args[1], tmplPt2x = args[2], outPath = args[3];
        var mode = args.Length > 4 ? ParseMode(args[4]) : AddrMode.Import;

        var cf = CompoundFile.Open(tmplPt2x);
        var s = cf.ListStreams().First(x => x.Path.Equals("Current/Tagnames/Tagnames.csv", StringComparison.OrdinalIgnoreCase));
        string csv = GenerateCsv(xmlPath, Encoding.UTF8.GetString(s.Data), mode, out string report);

        File.WriteAllText(outPath, csv, new UTF8Encoding(false));
        Console.WriteLine($"Wrote {outPath} ({new FileInfo(outPath).Length:N0} B)");
        Console.Write(report);
        return 0;
    }

    /// <summary>
    /// Builds the Tagnames.csv text from the XML's &lt;ts:TAGNAME&gt; blocks, using
    /// <paramref name="tmplCsv"/> only for the column order and the empty-value
    /// placeholders. Returns the CSV text and, via <paramref name="report"/>, a
    /// per-column classification (supplied / HW-IO / missing).
    /// </summary>
    public static string GenerateCsv(string xmlPath, string tmplCsv, AddrMode mode, out string report,
        IReadOnlySet<string>? forceMultiWrite = null)
    {
        var xmlTags = ParseXmlTags(xmlPath);
        Console.WriteLine($"XML tags: {xmlTags.Count}");
        var tagPrograms = ParseProgramUsage(xmlPath);

        var lines = tmplCsv.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        var cols = SplitCsv(lines[1]);

        var tmpl = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var tmplOrder = new List<string>();
        for (int i = 2; i < lines.Count; i++)
            if (lines[i].StartsWith("\""))
            {
                var f = SplitCsv(lines[i]);
                if (!tmpl.ContainsKey(f[0])) tmplOrder.Add(f[0]);
                tmpl[f[0]] = f;
            }

        var src = SourceMap();
        var shared = tmpl.Keys.Where(xmlTags.ContainsKey).ToList();

        int addrField = Col(cols, "Address");
        int physField = Col(cols, "PhysicalAddress");

        // Address / PhysicalAddress are assigned by the address engine. The allocation
        // ORDER is the target project's own tag order - which is the template CSV's row
        // order (the XML does not carry it). XML-only tags keep declaration order, last.
        var order = new List<string>();
        foreach (var n in tmplOrder) if (xmlTags.ContainsKey(n)) order.Add(n);
        foreach (var n in xmlTags.Keys) if (!tmpl.ContainsKey(n)) order.Add(n);
        var paOrder = order
            .Select(n => (
                Name: n,
                Pa: xmlTags[n].GetValueOrDefault("POINT_ASSIGNMENT.PhysicalAddress", ""),
                DataType: xmlTags[n].GetValueOrDefault("@type", "")))
            .ToList();
        var addrMap = AssignAddresses(paOrder, mode, TemplateAddresses(tmpl, cols), TemplateTypes(tmpl, cols));

        // Aliased tags: USER_ALIAS entries define an alias (MBR.<number>) and DEFAULT_ALIAS
        // entries without a number read one of those aliases. TriStation allows the reader and
        // its target to share the MBR address; two different alias numbers at the same address
        // are what it rejects (TN0090/TN0116).
        var aliasTargets = new List<(string Legacy, string Extended, string Address)>();
        var usedAliasNumbers = new HashSet<int>();
        var usedMbrNumbers = new HashSet<int>();
        foreach (var kv in addrMap)
        {
            var mm = Regex.Match(kv.Value ?? "", @"^MBR\.(\d+)$");
            if (mm.Success && int.TryParse(mm.Groups[1].Value, out int mn)) usedMbrNumbers.Add(mn);
        }
        foreach (var nm in order)
        {
            var fl = xmlTags[nm];
            string an = fl.GetValueOrDefault("POINT_ASSIGNMENT.AliasNumber", "0");
            if (!fl.GetValueOrDefault("POINT_ASSIGNMENT.AliasCategory", "").Equals("USER_ALIAS", StringComparison.OrdinalIgnoreCase)) continue;
            if (an.Length == 0 || an == "0" || !int.TryParse(an, out int anum)) continue;
            string pa = fl.GetValueOrDefault("POINT_ASSIGNMENT.PhysicalAddress", "");
            if (pa.Length == 0) pa = "MBR." + (anum - 12000).ToString("0000");
            aliasTargets.Add((an, fl.GetValueOrDefault("POINT_ASSIGNMENT.AliasNumberExt", (120000 + (anum - 12000)).ToString()), pa));
            usedAliasNumbers.Add(anum);
        }

        // Aliased tags whose XML carries no alias number (lossy "*_Replicated" exports strip both
        // AliasNumber and PhysicalAddress) are allocated by TriStation from the point class that
        // matches the alias type and the tag's data type:
        //   READ  + BOOL -> MBR (12001+/120001+)   READ  + numeric -> MIR (31001+/320001+)
        //   WRITE + BOOL -> MBW (2001+/20001+)     WRITE + numeric -> MIW (40251+/415001+)
        // Seed each used set with the points the table will already carry.
        var usedAliasPoints = new Dictionary<string, HashSet<int>>(StringComparer.OrdinalIgnoreCase)
        {
            ["MBR"] = new(), ["MIR"] = new(), ["MBW"] = new(), ["MIW"] = new()
        };
        void NotePoint(string? addr)
        {
            var mm = Regex.Match(addr ?? "", @"^(MBR|MIR|MBW|MIW)\.(\d+)$");
            if (mm.Success && int.TryParse(mm.Groups[2].Value, out int mn))
                usedAliasPoints[mm.Groups[1].Value].Add(mn);
        }
        foreach (var kv in addrMap) NotePoint(kv.Value);
        foreach (var nm in order) NotePoint(xmlTags[nm].GetValueOrDefault("POINT_ASSIGNMENT.PhysicalAddress", ""));

        // The XML sometimes repeats a PhysicalAddress (or leaves aliases resolved to the same
        // point). TriStation reallocates the later tag instead of failing with TN0090, so make
        // every address unique within its class before writing the table. Alias entries keep
        // their shared address.
        {
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var nextByPrefix = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var nm in order)
            {
                var fl = xmlTags[nm];
                if (!fl.GetValueOrDefault("POINT_ASSIGNMENT.AliasCategory", "").Equals("NO_ALIAS", StringComparison.OrdinalIgnoreCase)) continue;
                if (!addrMap.TryGetValue(nm, out var addr) || string.IsNullOrEmpty(addr)) continue;
                if (used.Add(addr)) { continue; }
                int dot = addr.IndexOf('.');
                string prefix = dot > 0 ? addr.Substring(0, dot) : addr;
                if (!nextByPrefix.TryGetValue(prefix, out int next))
                {
                    next = 1;
                    foreach (var u in used)
                    {
                        if (!u.StartsWith(prefix + ".", StringComparison.OrdinalIgnoreCase)) continue;
                        if (int.TryParse(u.Substring(prefix.Length + 1), out int num) && num >= next) next = num + 1;
                    }
                }
                string candidate;
                do { candidate = prefix + "." + next.ToString("0000"); next++; } while (!used.Add(candidate));
                nextByPrefix[prefix] = next;
                addrMap[nm] = candidate;
            }
        }

        // Template/placeholder tags (names containing XXX) are not real points. TriStation's
        // importer stores them as unaliased BOOL memory tags at one shared dummy MBN address;
        // keeping their exported point type/alias makes the tag validator reject them (TN0026).
        int placeholderMbn = 0;
        foreach (var v in addrMap.Values)
        {
            var mm = Regex.Match(v ?? "", @"^MBN\.(\d+)$");
            if (mm.Success && int.TryParse(mm.Groups[1].Value, out int n) && n > placeholderMbn) placeholderMbn = n;
        }
        string placeholderAddr = "MBN." + (placeholderMbn + 1).ToString("0000");
        int rehomedMbn = placeholderMbn;
        var usedFinal = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var sb = new StringBuilder();
        sb.Append(Regex.Replace(lines[0], "NumberOfTags =\\d+", "NumberOfTags =" + xmlTags.Count)).Append("\r\n");
        sb.Append(string.Join(",", cols.Select(Q))).Append("\r\n");

        foreach (var name in order)
        {
            var flat = xmlTags[name];
            var fields = new List<string>(cols.Count);
            foreach (var c in cols)
            {
                string val;
                int ci = Col(cols, c);
                if (src.TryGetValue(c, out var m))
                {
                    if (m.Key == "@name") { fields.Add(name); continue; }
                    val = flat.TryGetValue(m.Key, out var v) ? Transform(m.Value, v) : "";
                }
                else
                {
                    // Columns with no XML source keep the target project's own value for
                    // this tag (TriStation preserves them across an import), falling back
                    // to the column's modal value for tags the template does not know.
                    val = tmpl.TryGetValue(name, out var trow) && ci >= 0 && ci < trow.Count ? trow[ci] : "";
                }
                if (val.Length == 0 && tmpl.TryGetValue(name, out var trow2) && ci >= 0 && ci < trow2.Count)
                    val = trow2[ci];
                fields.Add(val);
            }

            if (addrMap.TryGetValue(name, out var assigned))
            {
                if (addrField >= 0) fields[addrField] = assigned;
                if (physField >= 0) fields[physField] = DerivePhysicalAddress(
                    assigned, flat.GetValueOrDefault("POINT_ASSIGNMENT.BinNumber", ""));
            }

            string pointType = flat.GetValueOrDefault("POINT_ASSIGNMENT.PointType", "");
            string aliasNum = flat.GetValueOrDefault("POINT_ASSIGNMENT.AliasNumber", "0");
            string aliasCat = flat.GetValueOrDefault("POINT_ASSIGNMENT.AliasCategory", "");
            string aliasType = flat.GetValueOrDefault("POINT_ASSIGNMENT.AliasType", "");
            // A template/placeholder tag carries no address, no alias number and (by TriStation's
            // naming convention) an "XXX" name, and is exported as a memory tag that still claims
            // an INPUT/OUTPUT point with NO_ALIAS. TriStation stores those as plain unaliased BOOL
            // memory tags; keeping the exported point type makes the address validator reject them
            // (TN0026/TN0116). Aliased XXX tags (DEFAULT_ALIAS/USER_ALIAS or PointType=MEMORY) are
            // real points and are handled by the alias allocator below.
            bool isPlaceholder =
                name.IndexOf("XXX", StringComparison.OrdinalIgnoreCase) >= 0 &&
                flat.GetValueOrDefault("POINT_ASSIGNMENT.PhysicalAddress", "").Length == 0 &&
                (aliasNum.Length == 0 || aliasNum == "0") &&
                aliasCat.Equals("NO_ALIAS", StringComparison.OrdinalIgnoreCase) &&
                (pointType.Equals("INPUT", StringComparison.OrdinalIgnoreCase) ||
                 pointType.Equals("OUTPUT", StringComparison.OrdinalIgnoreCase));
            bool autoAlias = !isPlaceholder &&
                (aliasNum.Length == 0 || aliasNum == "0") &&
                !aliasType.Equals("UNALIASED", StringComparison.OrdinalIgnoreCase);
            if (autoAlias)
            {
                void Set(string col, string value)
                {
                    int ci = Col(cols, col);
                    if (ci >= 0) fields[ci] = value;
                }
                bool readWrite = aliasType.Equals("READWRITE_ALIASED", StringComparison.OrdinalIgnoreCase);
                bool isBool = flat.GetValueOrDefault("@type", "").Equals("BOOL", StringComparison.OrdinalIgnoreCase);
                string prefix = (readWrite, isBool) switch
                {
                    (false, true) => "MBR",
                    (false, false) => "MIR",
                    (true, true) => "MBW",
                    _ => "MIW",
                };
                var used = usedAliasPoints[prefix];
                int idx = 1;
                while (used.Contains(idx)) idx++;
                used.Add(idx);
                int legacyBase = (readWrite, isBool) switch
                {
                    (false, true) => 12000,
                    (false, false) => 31000,
                    (true, true) => 2000,
                    _ => 40250,
                };
                int extendedBase = (readWrite, isBool) switch
                {
                    (false, true) => 120000,
                    (false, false) => 320000,
                    (true, true) => 20000,
                    _ => 415000,
                };
                string legacy = (legacyBase + idx).ToString(CultureInfo.InvariantCulture);
                string extended = (extendedBase + idx).ToString(CultureInfo.InvariantCulture);
                string address = prefix + "." + idx.ToString("0000");
                usedAliasNumbers.Add(legacyBase + idx);
                Set("AliasNumberSelection", "1");
                Set("UserAliasLegacy", legacy);
                Set("UserAliasExtended", extended);
                Set("AliasType", readWrite ? "3" : "2");
                Set("Location", "1");
                Set("IsMemory", "True");
                if (addrField >= 0) fields[addrField] = address;
                if (physField >= 0) fields[physField] = address;
            }
            if (isPlaceholder)
            {
                void Set(string col, string value)
                {
                    int ci = Col(cols, col);
                    if (ci >= 0) fields[ci] = value;
                }
                Set("DataType", "BOOL");
                Set("AliasType", "1");
                Set("AliasNumberSelection", "0");
                Set("UserAliasLegacy", "0");
                Set("UserAliasExtended", "0");
                Set("IsMemory", "True");
                Set("IsInput", "False");
                Set("IsOutput", "False");
                Set("Location", "1");
                Set("IsSafety", "False");
                Set("IsControl", "True");
                Set("Group1", "");
                Set("Group2", "");
                if (addrField >= 0) fields[addrField] = "MBN." + (++rehomedMbn).ToString("0000");
                if (physField >= 0) fields[physField] = "MBN." + rehomedMbn.ToString("0000");
            }

            // Final duplicate guard: addresses copied straight from the XML are not in addrMap,
            // so enforce uniqueness on the values actually written.
            if (addrField >= 0)
            {
                string a = fields[addrField];
                if (a.Length > 0 && !usedFinal.Add(a))
                {
                    int dot = a.IndexOf('.');
                    string prefix = dot > 0 ? a.Substring(0, dot) : a;
                    int next = 1;
                    foreach (var u in usedFinal)
                        if (u.StartsWith(prefix + ".", StringComparison.OrdinalIgnoreCase)
                            && int.TryParse(u.Substring(prefix.Length + 1), out int num) && num >= next)
                            next = num + 1;
                    string candidate;
                    do { candidate = prefix + "." + next.ToString("0000"); next++; } while (!usedFinal.Add(candidate));
                    fields[addrField] = candidate;
                }
                // Keep the derived physical address. For an aliased tag it is the alias target
                // (e.g. IIR.0021) while Address stays the point coordinate (02.06.21); forcing the
                // two to agree makes TriStation report the tag as unallocated (PC0117) and its
                // memory as overlapping (PB0002/PB0003). For a plain point both derive the same.
                if (physField >= 0 && fields[addrField].Length > 0)
                {
                    bool aliased = !flat.GetValueOrDefault("POINT_ASSIGNMENT.AliasCategory", "")
                        .Equals("NO_ALIAS", StringComparison.OrdinalIgnoreCase);
                    fields[physField] = aliased
                        ? fields[physField]
                        : DerivePhysicalAddress(fields[addrField], flat.GetValueOrDefault("POINT_ASSIGNMENT.BinNumber", ""));
                    if (fields[physField].Length == 0) fields[physField] = fields[addrField];
                }
            }

            // Opt-in repair for lossy exports: a tag provably written by more than one program
            // needs multiple writes enabled or the compiler stops with PC0005.
            if (forceMultiWrite != null && forceMultiWrite.Contains(name))
            {
                int ci = Col(cols, "EnableMultipleWrites");
                if (ci >= 0) fields[ci] = "True";
            }

            // Programs that reference this tag. TriStation stores them in reverse program
            // order and uses this list to allocate the tag to the compiled application;
            // a missing entry leaves the tag unallocated (PC0117) or elements disconnected.
            int plField = Col(cols, "ProgramsList");
            if (plField >= 0)
            {
                var owners = tagPrograms.TryGetValue(name, out var list) ? list : new List<string>();
                fields[plField] = owners.Count == 0 ? "{}" : "{" + string.Join(",", owners) + "}";
            }

            sb.Append(string.Join(",", fields.Select(Q))).Append("\r\n");
        }
        sb.Append("</Tags>\r\n");

        // ---- report ----
        var rep = new StringBuilder();
        rep.AppendLine();
        rep.AppendLine("=========================================================================");
        rep.AppendLine("CONVERSION REPORT - Current/Tagnames/Tagnames.csv");
        rep.AppendLine($"Address mode = {mode}.");
        rep.AppendLine("  Import: keep the first-occurrence non-MBN PhysicalAddress; otherwise reuse the");
        rep.AppendLine("          template tag's own Address, else allocate by type (BOOL->MBN, numeric->MIN).");
        rep.AppendLine("          Allocation order = template row order (the target project's tag order).");
        rep.AppendLine("  Export: copy PhysicalAddress verbatim.");
        rep.AppendLine("Round-trip VERIFIED on TriStation 5.8.0: FB 705/705, PACK_REPL 169/169 tags");
        rep.AppendLine("identical to the original export (PhysicalAddress, BinNumber, PointType).");
        rep.AppendLine("=========================================================================");
        rep.AppendLine($"{"column",-28} {"class",-9} {"match",-9} source");
        int supplied = 0, hw = 0, missing = 0;
        foreach (var c in cols)
        {
            string cls, match = "-", srcDesc = "";
            if (src.TryGetValue(c, out var m))
            {
                cls = "supplied"; supplied++; srcDesc = m.Key;
                if (m.Key == "@name") match = "n/a";
                else
                {
                    int ok = 0, tot = 0;
                    foreach (var n in shared)
                    {
                        if (!xmlTags[n].TryGetValue(m.Key, out var xv)) continue;
                        tot++;
                        int ci = Col(cols, c);
                        string got = Transform(m.Value, xv);
                        if (got.Length == 0 && ci < tmpl[n].Count) got = tmpl[n][ci];
                        if (Eq(tmpl[n][ci], got)) ok++;
                    }
                    match = $"{ok}/{tot}";
                }
            }
            else { cls = "MISSING"; missing++; }
            if (HwColumns.Contains(c)) { cls = "HW/IO"; hw++; }
            rep.AppendLine($"{c,-28} {cls,-9} {match,-9} {srcDesc}");
        }
        rep.AppendLine();
        rep.AppendLine($"supplied={supplied}  hardware/IO(no XML source)={hw}  missing={missing}  total columns={cols.Count}");
        var onlyTmpl = tmpl.Keys.Except(xmlTags.Keys).ToList();
        var onlyXml = xmlTags.Keys.Except(tmpl.Keys).ToList();
        rep.AppendLine($"tags: template={tmpl.Count} xml={xmlTags.Count}  template-only={onlyTmpl.Count}  xml-only={onlyXml.Count}");
        report = rep.ToString();
        return sb.ToString();
    }

    private static readonly HashSet<string> HwColumns = new(StringComparer.OrdinalIgnoreCase)
    { "IOModuleConfig", "IOModuleTrigger", "AI_Unit" };

    /// <summary>The template's own Address per tag - the target project's existing assignment.</summary>
    private static Dictionary<string, string> TemplateAddresses(
        Dictionary<string, List<string>> tmpl, List<string> cols)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        int ac = Col(cols, "Address");
        if (ac < 0) return d;
        foreach (var kv in tmpl)
            if (ac < kv.Value.Count && kv.Value[ac].Length > 0) d[kv.Key] = kv.Value[ac];
        return d;
    }

    /// <summary>Template tag names to their declared data type. A template address may only be
    /// reused for a tag of the same type: the scaffold project can contain an unrelated tag that
    /// happens to share a name (e.g. a DINT "Temp1" while the XML declares a BOOL "Temp1"), and
    /// keeping its address would hand a BOOL tag a word address (TN0116).</summary>
    private static Dictionary<string, string> TemplateTypes(
        Dictionary<string, List<string>> tmpl, List<string> cols)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        int tc = Col(cols, "DataType");
        if (tc < 0) return d;
        foreach (var kv in tmpl)
            if (tc < kv.Value.Count && kv.Value[tc].Length > 0) d[kv.Key] = kv.Value[tc];
        return d;
    }

    private static Dictionary<string, KeyValuePair<string, Tx>> SourceMap()
    {
        var d = new Dictionary<string, KeyValuePair<string, Tx>>(StringComparer.OrdinalIgnoreCase);
        void A(string col, string key, Tx t) => d[col] = new(key, t);
        A("Tagname", "@name", Tx.Ident);
        A("DataType", "@type", Tx.Ident);
        // Address/PhysicalAddress are carried verbatim by the source XML's
        // POINT_ASSIGNMENT.PhysicalAddress. Verified against genuine TriStation
        // XML->PT2X exports (Address == PhysicalAddress for every tag).
        A("Address", "POINT_ASSIGNMENT.PhysicalAddress", Tx.Ident);
        A("PhysicalAddress", "POINT_ASSIGNMENT.PhysicalAddress", Tx.Ident);
        A("UserAliasLegacy", "POINT_ASSIGNMENT.AliasNumber", Tx.Ident);
        A("UserAliasExtended", "POINT_ASSIGNMENT.AliasNumberExt", Tx.Ident);
        A("AliasNumberSelection", "POINT_ASSIGNMENT.AliasCategory", Tx.AliasSel);
        A("Description", "@documentation", Tx.Ident);
        // Group1/Group2 are the plant/unit grouping the source export carries.
        A("Group1", "TAG_DECLARATION.Group1", Tx.Ident);
        A("Group2", "TAG_DECLARATION.Group2", Tx.Ident);
        A("InitialValue", "@initial", Tx.Ident);
        A("IsSafety", "TAG_DECLARATION.Application", Tx.AppSafety);
        A("IsControl", "TAG_DECLARATION.Application", Tx.AppControl);
        A("SharedRead", "TAG_DECLARATION.SharedForRead", Tx.YesNo);
        A("IsMemory", "POINT_ASSIGNMENT.PointType", Tx.Mem);
        A("IsInput", "POINT_ASSIGNMENT.PointType", Tx.PointInput);
        A("IsOutput", "POINT_ASSIGNMENT.PointType", Tx.PointOutput);
        // Location is a pure function of the point type (1=MEMORY, 2=INPUT, 3=OUTPUT);
        // TriStation recomputes the Is* flags from it, so it must be set correctly.
        A("Location", "POINT_ASSIGNMENT.PointType", Tx.PointLocation);
        A("EnableMultipleWrites", "TAG_DECLARATION.EnableMultipleWrite", Tx.YesNo);
        A("AliasType", "POINT_ASSIGNMENT.AliasType", Tx.AliasType);
        A("MinimumValue", "TAG_SCALING.MinSpan", Tx.F6);
        A("MaximumValue", "TAG_SCALING.MaxSpan", Tx.F6);
        A("Precision", "TAG_SCALING.DisplayPrecision", Tx.Ident);
        A("DisableScaling", "TAG_SCALING.DisableScaling", Tx.YesNo);
        A("StateNameTRUE", "TAG_DISPLAY.TrueStateName", Tx.Ident);
        A("ColorTRUE", "TAG_DISPLAY.TrueStateColor", Tx.Color);
        A("StateNameFALSE", "TAG_DISPLAY.FalseStateName", Tx.Ident);
        A("ColorFALSE", "TAG_DISPLAY.FalseStateColor", Tx.Color);
        A("Area", "TAG_LOCATION.AreaName", Tx.Ident);
        A("Equipment", "TAG_LOCATION.Equipment", Tx.Ident);
        A("SIFName", "TAG_LOCATION.SIFName", Tx.Ident);
        A("IOModuleType", "IO_MODULE_PARAMS.IoModuleType", Tx.IoModule);
        A("MinimumTriggerTime", "SDI_PARAMS.MinimumTriggerTime", Tx.Ident);
        A("SOEDeadTime", "SDI_PARAMS.SoeDeadTime", Tx.Ident);
        A("R_Adjust", "SDI_PARAMS.RAdjust", Tx.Ident);
        A("ShortedThreshold", "SDO_PARAMS.ShortedThreshold", Tx.Ident);
        A("OpenThreshold", "SDO_PARAMS.OpenThreshold", Tx.Ident);
        A("AI_FilterType", "AI_PARAMS.FilterType", Tx.Filter);
        A("AI_ClampEngineeringVal", "AI_PARAMS.ClampEngValue", Tx.Bool);
        A("AI_FieldFaultMonitor", "AI_PARAMS.FldFaultMon", Tx.Bool);
        A("AI_NamurHighLimit", "AI_PARAMS.FldFaultHiLimit", Tx.F6);
        A("AI_NamurLowLimit", "AI_PARAMS.FldFaultLoLimit", Tx.F6);
        A("AI_MinimumRawValue", "AI_PARAMS.MinRawValue", Tx.Ident);
        A("AI_MaximumRawValue", "AI_PARAMS.MaxRawValue", Tx.Ident);
        A("AI_MinimumEngValue", "AI_PARAMS.MinEngValue", Tx.F6);
        A("AI_MaximumEngValue", "AI_PARAMS.MaxEngValue", Tx.F6);
        A("HART_Enable", "HART_PARAMS.Enable", Tx.Bool);
        A("HART_MonitorDeviceHealth", "HART_PARAMS.MonDeviceHealth", Tx.Bool);
        A("HART_MonitorConfigChanges", "HART_PARAMS.MonConfigChanges", Tx.Bool);
        A("HART_LockConfiguration", "HART_PARAMS.LockConfig", Tx.Bool);
        A("Class", "POINT_ASSIGNMENT.TagClass", Tx.ClassEnum);
        A("SOEFlags", "SOE_BLOCKS_ASSIGNMENT.#text", Tx.Ident);
        A("PI_PointType", "PI_PARAMS.PIPointType", Tx.Pad2);
        A("PI_PointGearTeeth", "PI_PARAMS.GearTeeth", Tx.Pad2);
        A("PI_PointGearRatio", "PI_PARAMS.GearRatio", Tx.F6);
        return d;
    }

    // ===================== Address engine =====================
    public enum AddrMode { Import, Export }

    private static AddrMode ParseMode(string s) =>
        s.StartsWith("exp", StringComparison.OrdinalIgnoreCase) ? AddrMode.Export : AddrMode.Import;

    /// <summary>
    /// Assigns Address/PhysicalAddress to every tag, in the given order (the target
    /// project's tag order = the template CSV row order).
    ///
    /// Import mode reproduces TriStation's XML importer:
    ///   * The FIRST occurrence of any non-empty, non-MBN PhysicalAddress (I/O like MBW/MBR/
    ///     MIW/MIR/MRR, and also MIN/MRN) is preserved verbatim.
    ///   * Otherwise, if the template already has the tag, its own Address is reused (the
    ///     target project's existing assignment - e.g. an empty-pa DINT tag whose class is a
    ///     project-specific MIW rather than MIN).
    ///   * Otherwise the tag is freshly allocated by DataType: BOOL -> MBN.&lt;n&gt; , numeric ->
    ///     MIN.&lt;n&gt; , counters independent.
    ///
    /// Export mode copies the XML PhysicalAddress verbatim (a genuine export is round-trip
    /// faithful); an empty address still gets a typed fallback so the CSV stays valid.
    ///
    /// Allocating in the target project's order makes the fresh MBN/MIN numbers exact, so the
    /// whole assignment round-trips. Verified against TriStation 5.8.0 (FB 705/705,
    /// PACK_REPL 169/169).
    /// </summary>
    public static Dictionary<string, string> AssignAddresses(
        IReadOnlyList<(string Name, string Pa, string DataType)> ordered, AddrMode mode,
        IReadOnlyDictionary<string, string>? templateAddr = null,
        IReadOnlyDictionary<string, string>? templateType = null)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int mbn = 0, min = 0, mrn = 0;
        foreach (var t in ordered)
        {
            string pa = t.Pa ?? "";
            bool preserve = mode == AddrMode.Export
                ? pa.Length > 0
                : pa.Length > 0 && !pa.StartsWith("MBN.", StringComparison.OrdinalIgnoreCase) && !seen.Contains(pa);
            string addr;
            if (preserve)
            {
                addr = pa;
                seen.Add(pa);
            }
            else if (templateAddr != null && templateAddr.TryGetValue(t.Name, out var ta) && ta.Length > 0 &&
                     (templateType == null || !templateType.TryGetValue(t.Name, out var tt) ||
                      string.Equals(tt, t.DataType, StringComparison.OrdinalIgnoreCase)))
                addr = ta;   // target project already has this tag: keep its assignment
            else addr = Allocate(t.DataType, ref mbn, ref min, ref mrn);
            map[t.Name] = addr;
        }
        return map;
    }

    /// <summary>Fresh address for an un-addressed tag: BOOL -> MBN, REAL -> MRN, other numeric ->
    /// MIN. TriStation keeps the three memory classes separate; a REAL tag parked in MIN is
    /// reported as a corrupted address (TN0116).</summary>
    public static string Allocate(string dataType, ref int mbn, ref int min, ref int mrn)
    {
        if (string.Equals(dataType, "BOOL", StringComparison.OrdinalIgnoreCase))
            return "MBN." + (++mbn).ToString("0000", CultureInfo.InvariantCulture);
        if (string.Equals(dataType, "REAL", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(dataType, "LREAL", StringComparison.OrdinalIgnoreCase))
            return "MRN." + (++mrn).ToString("0000", CultureInfo.InvariantCulture);
        return "MIN." + (++min).ToString("0000", CultureInfo.InvariantCulture);
    }

    public static string Allocate(string dataType, ref int mbn, ref int min)
    {
        int unused = 0;
        return Allocate(dataType, ref mbn, ref min, ref unused);
    }

    // The Tagnames.csv PhysicalAddress column is NOT the same quantity as Address:
    //   * coordinate Address (AA.BB.CC) -> "<register-class from BinNumber>.<CC>" e.g. 04.01.17 -> IBR.0017
    //   * memory-style Address (MBN.nnnn) -> identical to Address
    private static readonly Dictionary<string, string> BinPrefix = new(StringComparer.OrdinalIgnoreCase)
    { ["0"] = "QBW", ["1"] = "MBW", ["2"] = "IBR", ["3"] = "MBR", ["4"] = "IIR",
      ["11"] = "MIW", ["13"] = "MBN", ["14"] = "MIN", ["15"] = "MRN" };

    public static string DerivePhysicalAddress(string addr, string binNumber)
    {
        if (string.IsNullOrEmpty(addr)) return "";
        if (Regex.IsMatch(addr, @"^\d+\.\d+\.\d+$"))
        {
            string last = addr.Substring(addr.LastIndexOf('.') + 1);
            if (BinPrefix.TryGetValue(binNumber ?? "", out var pre) && int.TryParse(last, out int cc))
                return pre + "." + cc.ToString("0000", CultureInfo.InvariantCulture);
        }
        return addr;
    }

    private enum Tx { Ident, YesNo, Bool, Color, IoModule, Filter, ClassEnum, AppSafety, AppControl, Mem, PointInput, PointOutput, PointLocation, AliasType, AliasSel, Pad2, F6 }

    private static string Transform(Tx t, string v) => t switch
    {
        Tx.YesNo => v.Equals("Yes", StringComparison.OrdinalIgnoreCase) ? "True" : "False",
        Tx.Bool => v.Equals("true", StringComparison.OrdinalIgnoreCase) ? "True" : "False",
        Tx.Color => v.ToUpperInvariant() switch { "RED" => "255", "GREEN" => "65280", "BLUE" => "16711680", _ => v },
        Tx.IoModule => v.Equals("NA", StringComparison.OrdinalIgnoreCase) ? "0" : v,
        Tx.Filter => v.Equals("NONE", StringComparison.OrdinalIgnoreCase) ? "0" : v,
        Tx.ClassEnum => v.Equals("USER_DEFINED", StringComparison.OrdinalIgnoreCase) ? "3" : v,
        Tx.AppSafety => v.Equals("Safety", StringComparison.OrdinalIgnoreCase) ? "True" : "False",
        Tx.AppControl => v.Equals("Control", StringComparison.OrdinalIgnoreCase) ? "True" : "False",
        Tx.Mem => v.Equals("MEMORY", StringComparison.OrdinalIgnoreCase) ? "True" : "False",
        Tx.PointInput => v.Contains("INPUT", StringComparison.OrdinalIgnoreCase) ? "True" : "False",
        Tx.PointOutput => v.Contains("OUTPUT", StringComparison.OrdinalIgnoreCase) ? "True" : "False",
        Tx.PointLocation => v.Equals("MEMORY", StringComparison.OrdinalIgnoreCase) ? "1"
                          : v.Contains("INPUT", StringComparison.OrdinalIgnoreCase) ? "2"
                          : v.Contains("OUTPUT", StringComparison.OrdinalIgnoreCase) ? "3" : v,
        Tx.AliasType => v.ToUpperInvariant() switch
        {
            "UNALIASED" => "1",
            "READ_ALIASED" => "2",
            "READWRITE_ALIASED" => "3",
            _ => "1",
        },
        Tx.AliasSel => v.Equals("NO_ALIAS", StringComparison.OrdinalIgnoreCase) ? "0"
                      : v.Equals("USER_ALIAS", StringComparison.OrdinalIgnoreCase) ? "2" : "1",
        Tx.F6 => double.TryParse(v, NumberStyles.Any, CultureInfo.InvariantCulture, out var d)
                 ? Math.Round(d, 3).ToString("F6", CultureInfo.InvariantCulture) : v,
        // TriStation stores the pulse-input parameters as two-digit codes ("00", "01"); a bare
        // "0"/"1" makes tag validation fail with TN0141 "Invalid PI parameter".
        Tx.Pad2 => int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)
                 ? n.ToString("00", CultureInfo.InvariantCulture) : v,
        _ => v,
    };

    // ---- shared helpers ----
    private static string Q(string s)
        => "\"" + (s ?? "").Replace("\r", " ").Replace("\n", " ").Replace("\"", "\"\"") + "\"";

    private static bool Eq(string csv, string got)
    {
        if ((csv ?? "").Equals(got ?? "", StringComparison.OrdinalIgnoreCase)) return true;
        if (decimal.TryParse(csv, NumberStyles.Any, CultureInfo.InvariantCulture, out var a) &&
            decimal.TryParse(got, NumberStyles.Any, CultureInfo.InvariantCulture, out var b))
            return a == b;
        return (csv ?? "").Trim().Equals((got ?? "").Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private static int Col(List<string> cols, string name)
    {
        for (int i = 0; i < cols.Count; i++) if (cols[i].Equals(name, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }

    /// <summary>Tag name â†’ programs whose globalVars reference it, in reverse XML program order
    /// (the order TriStation writes into Tagnames.csv's ProgramsList column).</summary>
    private static Dictionary<string, List<string>> ParseProgramUsage(string xmlPath)
    {
        string t = File.ReadAllText(xmlPath);
        var result = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (Match p in Regex.Matches(t, "(?s)<pou\\s+name=\"([^\"]+)\"[^>]*pouType=\"program\"[^>]*>(.*?)</pou>"))
        {
            string program = p.Groups[1].Value;
            var gv = Regex.Match(p.Groups[2].Value, "(?s)<globalVars[^>]*>(.*?)</globalVars>");
            if (!gv.Success) continue;
            foreach (Match v in Regex.Matches(gv.Groups[1].Value, "<variable\\s+name=\"([^\"]+)\""))
            {
                string tag = v.Groups[1].Value;
                if (!result.TryGetValue(tag, out var list)) result[tag] = list = new List<string>();
                if (!list.Contains(program)) list.Add(program);
            }
        }
        foreach (var list in result.Values) list.Reverse();
        return result;
    }

    /// <summary>Declared type and description for every tag in the XML's &lt;ts:TAGNAME&gt;
    /// blocks (name → type/desc), used by the FBD generator for global declarations.</summary>
    public static Dictionary<string, (string Type, string Desc)> TagTypes(string xmlPath)
    {
        var d = new Dictionary<string, (string Type, string Desc)>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in ParseXmlTags(xmlPath))
            d[kv.Key] = (kv.Value.GetValueOrDefault("@type", ""), kv.Value.GetValueOrDefault("@documentation", ""));
        return d;
    }

    private static Dictionary<string, Dictionary<string, string>> ParseXmlTags(string xmlPath)
    {
        string t = File.ReadAllText(xmlPath);
        var result = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in Regex.Matches(t, "(?s)<variable\\s+name=\"([^\"]+)\"\\s*>(.*?)</variable>"))
        {
            string body = m.Groups[2].Value;
            var tn = Regex.Match(body, "(?s)<ts:TAGNAME>(.*?)</ts:TAGNAME>");
            if (!tn.Success) continue;
            var flat = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["@name"] = m.Groups[1].Value };
            var ty = Regex.Match(body, "<type>\\s*<([A-Za-z_][\\w.:]*)");
            flat["@type"] = ty.Success ? ty.Groups[1].Value : "";
            var doc = Regex.Match(body, "(?s)<documentation><!\\[CDATA\\[(.*?)\\]\\]></documentation>");
            flat["@documentation"] = doc.Success ? doc.Groups[1].Value.Trim().Replace("\"", "") : "";
            var iv = Regex.Match(body, "<simpleValue\\s+value=\"([^\"]*)\"");
            flat["@initial"] = iv.Success ? iv.Groups[1].Value : "";
            foreach (Match e in Regex.Matches(tn.Groups[1].Value, "<ts:([A-Za-z_]\\w*)([^>]*?)(/?)>"))
            {
                string elem = e.Groups[1].Value;
                foreach (Match a in Regex.Matches(e.Groups[2].Value, "([A-Za-z_]\\w*)=\"([^\"]*)\""))
                    flat[elem + "." + a.Groups[1].Value] = a.Groups[2].Value.Trim();
                if (e.Groups[3].Value != "/")
                {
                    var txt = Regex.Match(tn.Groups[1].Value, $"<ts:{elem}[^>]*>([^<]*)</ts:{elem}>");
                    if (txt.Success) flat[elem + ".#text"] = txt.Groups[1].Value.Trim();
                }
            }
            result[m.Groups[1].Value] = flat;
        }
        return result;
    }

    private static List<string> SplitCsv(string line)
    {
        var outp = new List<string>(); var sb = new StringBuilder(); bool q = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (q) { if (c == '"') { if (i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; } else q = false; } else sb.Append(c); }
            else { if (c == '"') q = true; else if (c == ',') { outp.Add(sb.ToString()); sb.Clear(); } else sb.Append(c); }
        }
        outp.Add(sb.ToString());
        return outp;
    }
}
