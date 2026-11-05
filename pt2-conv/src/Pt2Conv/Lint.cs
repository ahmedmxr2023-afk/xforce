using System.Globalization;
using System.Text;
using System.Xml.Linq;
using Pt2Conv.Cfb;

namespace Pt2Conv;

/// <summary>
/// Consistency linter for a finished .pt2x. TriStation silently marks a block instance as
/// "out of date" (red X) when the stored snapshot does not agree with the element it refers to,
/// and that failure is easy to introduce when the embedded base or the library identity table is
/// refreshed. This command fails when:
///   * a block instance has no snapshot for its type,
///   * its snapEventId differs from the snapshot's connections event,
///   * the snapshot's DogTag/event differs from the catalogue element (stale library ID),
///   * a project function block's catalogue Declarations differ from the snapshot pins.
/// </summary>
public static class Lint
{
    public static int Run(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("usage: lint <project.pt2x> [--json]");
            return 1;
        }

        string path = args[1];
        bool asJson = args.Any(a => a.Equals("--json", StringComparison.OrdinalIgnoreCase));
        if (!File.Exists(path))
        {
            Console.Error.WriteLine("ERROR: file not found: " + path);
            return 2;
        }

        var streams = new CompoundFile(path).ListStreams();
        var project = streams.FirstOrDefault(s =>
            s.Path.Equals("Current/Project.xml", StringComparison.OrdinalIgnoreCase));
        if (project == null)
        {
            Console.Error.WriteLine("ERROR: Current/Project.xml not found");
            return 2;
        }

        var errors = new List<string>();
        XDocument doc;
        try { doc = XDocument.Parse(Encoding.UTF8.GetString(project.Data)); }
        catch (Exception ex)
        {
            Console.Error.WriteLine("ERROR: Project.xml is not XML: " + ex.Message);
            return 2;
        }

        var elements = doc.Descendants().FirstOrDefault(e =>
            e.Name.LocalName == "Collection" && (string?)e.Attribute("Name") == "Elements");
        if (elements == null)
        {
            Console.Error.WriteLine("ERROR: Elements collection not found");
            return 2;
        }

        var catalogue = new Dictionary<string, Cat>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in elements.Elements().Where(e => e.Name.LocalName == "Class"))
        {
            string name = (string?)c.Attribute("ElemName") ?? "";
            if (name.Length == 0) continue;
            var conn = c.Elements().FirstOrDefault(x =>
                x.Name.LocalName == "Class" && (string?)x.Attribute("Embed") == "EventConnectionsChanged");
            var decls = c.Elements().FirstOrDefault(x =>
                x.Name.LocalName == "Collection" && (string?)x.Attribute("Name") == "Declarations");
            var pins = new List<string>();
            if (decls != null)
                foreach (var v in decls.Elements().Where(e => e.Name.LocalName == "Class"))
                    pins.Add(PinKey(v));
            catalogue[name] = new Cat(
                (string?)c.Attribute("LibID") ?? "",
                (string?)c.Attribute("DogTag") ?? "",
                (string?)c.Attribute("ElementType") ?? "",
                (string?)conn?.Attribute("eventId") ?? "",
                pins);
        }

        int streamsChecked = 0, instances = 0, projectInstances = 0;
        foreach (var sc in streams)
        {
            if (!sc.Path.EndsWith(".FBD", StringComparison.OrdinalIgnoreCase)) continue;
            streamsChecked++;

            XDocument fbd;
            try { fbd = XDocument.Parse(Encoding.UTF8.GetString(sc.Data)); }
            catch { errors.Add($"{sc.Path}: not valid XML"); continue; }

            var snapCol = fbd.Descendants().FirstOrDefault(e =>
                e.Name.LocalName == "Collection" && (string?)e.Attribute("Name") == "snapshots");
            var snapshots = new Dictionary<string, Snap>(StringComparer.OrdinalIgnoreCase);
            if (snapCol != null)
            {
                foreach (var c in snapCol.Elements().Where(e => e.Name.LocalName == "Class"))
                {
                    string name = (string?)c.Attribute("name") ?? "";
                    if (name.Length == 0) continue;
                    var ev = c.Elements().FirstOrDefault(x =>
                        x.Name.LocalName == "Class" && (string?)x.Attribute("Embed") == "eventConnectionsChanged");
                    var decls = c.Elements().FirstOrDefault(x =>
                        x.Name.LocalName == "Collection" && (string?)x.Attribute("Name") == "declarations");
                    var pins = new List<string>();
                    if (decls != null)
                        foreach (var v in decls.Elements().Where(e => e.Name.LocalName == "Class"))
                            pins.Add(PinKey(v));
                    snapshots[name] = new Snap(
                        (string?)c.Attribute("dogTag") ?? "",
                        (string?)ev?.Attribute("eventId") ?? "",
                        pins);
                }
            }

            foreach (var b in fbd.Descendants().Where(e =>
                         e.Name.LocalName == "Class" &&
                         ((string?)e.Attribute("ClassType") == "CFBDFBlockXml" ||
                          (string?)e.Attribute("ClassType") == "CFBDFunctionXml")))
            {
                string type = (string?)b.Attribute("typeName") ?? "";
                string inst = (string?)b.Attribute("instanceName") ?? "";
                if (type.Length == 0) continue;
                instances++;
                string where = $"{sc.Path}: {(inst.Length > 0 ? inst : type)} ({type})";

                if (!snapshots.TryGetValue(type, out var snap))
                {
                    errors.Add($"{where}: no snapshot record for the type");
                    continue;
                }
                string snapEvent = (string?)b.Attribute("snapEventId") ?? "";
                if (snapEvent != snap.Event)
                    errors.Add($"{where}: snapEventId={snapEvent} but snapshot event={snap.Event}");

                if (!catalogue.TryGetValue(type, out var cat))
                {
                    errors.Add($"{where}: type is not in the project catalogue");
                    continue;
                }
                if (snap.DogTag != cat.DogTag)
                    errors.Add($"{where}: snapshot dogTag={snap.DogTag} but catalogue dogTag={cat.DogTag} (stale library identity)");
                if (snap.Event != cat.ConnEvent)
                    errors.Add($"{where}: snapshot event={snap.Event} but catalogue connections event={cat.ConnEvent}");

                if (cat.LibId == "1" && cat.ElementType == "2")
                {
                    projectInstances++;
                    if (!SamePins(cat.Pins, snap.Pins))
                        errors.Add($"{where}: project FB interface differs from the catalogue Declarations");
                }
            }
        }

        if (asJson)
        {
            var payload = new
            {
                file = path,
                streams = streamsChecked,
                instances,
                projectInstances,
                errors = errors.Count,
                messages = errors,
            };
            Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(payload));
        }
        else
        {
            foreach (var e in errors.Take(25)) Console.WriteLine("  X " + e);
            if (errors.Count > 25) Console.WriteLine($"  ... and {errors.Count - 25} more");
            Console.WriteLine($"lint: {streamsChecked} FBD stream(s), {instances} instance(s), "
                              + $"{projectInstances} project-FB instance(s), {errors.Count} issue(s)");
        }
        return errors.Count == 0 ? 0 : 1;
    }

    private static string PinKey(XElement v) =>
        string.Join("|",
            (string?)v.Attribute("name") ?? "",
            (string?)v.Attribute("dataType") ?? "",
            (string?)v.Attribute("varVTType") ?? "");

    private static bool SamePins(List<string> a, List<string> b)
    {
        if (a.Count != b.Count) return false;
        var x = a.OrderBy(s => s, StringComparer.Ordinal).ToList();
        var y = b.OrderBy(s => s, StringComparer.Ordinal).ToList();
        for (int i = 0; i < x.Count; i++)
            if (!string.Equals(x[i], y[i], StringComparison.Ordinal)) return false;
        return true;
    }

    private sealed record Cat(string LibId, string DogTag, string ElementType, string ConnEvent, List<string> Pins);
    private sealed record Snap(string DogTag, string Event, List<string> Pins);
}
