using System.Text;

namespace Pt2Conv;

/// <summary>
/// Generates a TriStation .DOC container stream (CTS2Doc serialization).
/// OBSERVED: 693-B shape used by STFB1..7 (10 empty userMacros; empty declarations).
/// drawingNumber/drawingTitle come from the POU's POU_EXT macros when present.
/// </summary>
public static class DocGenerator
{
    public static int Run(string[] args)
    {
        if (args.Length < 4) { Console.Error.WriteLine("usage: gendoc <xml> <pouName> <out.DOC>"); return 1; }
        var bytes = Generate(args[1], args[2]);
        File.WriteAllBytes(args[3], bytes);
        Console.WriteLine($"Wrote {args[3]} ({bytes.Length:N0} B)");
        return 0;
    }

    public static byte[] Generate(string xmlPath, string pouName)
    {
        var doc = XmlRepair.Load(xmlPath);
        var pou = doc.Descendants()
            .FirstOrDefault(e => e.Name.LocalName == "pou" && (string?)e.Attribute("name") == pouName)
            ?? doc.Descendants()
                .FirstOrDefault(e => e.Name.LocalName == "pou" && string.Equals((string?)e.Attribute("name"), pouName, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"POU '{pouName}' not found");

        string drawingNumber = "", drawingTitle = "";
        var ext = pou.Elements().FirstOrDefault(e => e.Name.LocalName == "POU_EXT");
        if (ext != null)
        {
            foreach (var mac in ext.Elements().Where(e => e.Name.LocalName == "Macros"))
            {
                string n = (string?)mac.Attribute("Name") ?? "";
                string val = mac.Elements().FirstOrDefault(e => e.Name.LocalName == "Value")?.Value ?? "";
                if (n == "DRAWING_NUMBER") drawingNumber = val;
                else if (n == "DRAWING_TITLE") drawingTitle = val;
            }
        }

        var sb = new StringBuilder(900);
        sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\r\n");
        sb.Append("<SerializableClass Type=\"Serializable\" Version=\"1.0\" ClassType=\"CTS2Doc\"")
          .Append($" drawingNumber=\"{Esc(drawingNumber)}\" drawingTitle=\"{Esc(drawingTitle)}\"")
          .Append(" approvedName=\"\" approvedTime=\"\" rev=\"\" extOptFlags=\"3\"")
          .Append(" cemEventInputs=\"0\" cemActionOutputs=\"0\">");
        sb.Append("<Collection Name=\"userMacros\" ClassType=\"xCString\" CollectionType=\"2\" IsMemberBase=\"1\">");
        for (int i = 0; i < 10; i++) sb.Append("<Class Value=\"\"/>");
        sb.Append("</Collection>");
        sb.Append("<Collection Name=\"declarations\" ClassType=\"CTS2Vars\" CollectionType=\"1\"/>");
        sb.Append("<Collection Name=\"hiddenVarTypes\" ClassType=\"CTS2HiddenVarTypeXML\" CollectionType=\"1\"/>");
        sb.Append("</SerializableClass>\r\n");
        return new UTF8Encoding(false).GetBytes(sb.ToString());
    }

    private static string Esc(string s)
        => s.Replace("&", "&amp;").Replace("\"", "&quot;").Replace("<", "&lt;").Replace(">", "&gt;");
}