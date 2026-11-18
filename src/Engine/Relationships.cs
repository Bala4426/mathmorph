using System.Xml.Linq;

namespace MathMorph.Engine;

public sealed record Relationship(string Id, string Type, string Target, bool External);

public static class Relationships
{
    public static string RelsPathFor(string partName)
    {
        var dir = partName.Contains('/') ? partName[..partName.LastIndexOf('/')] : "";
        var file = partName[(partName.LastIndexOf('/') + 1)..];
        return (dir.Length > 0 ? dir + "/" : "") + "_rels/" + file + ".rels";
    }

    public static Dictionary<string, Relationship> Load(SafePackage pkg, string partName)
    {
        var map = new Dictionary<string, Relationship>(StringComparer.Ordinal);
        var e = pkg.Get(RelsPathFor(partName));
        if (e == null) return map;
        var doc = SafeXml.Parse(e.Data);
        var baseDir = partName.Contains('/') ? partName[..partName.LastIndexOf('/')] : "";
        foreach (var r in doc.Root!.Elements(Ns.PR + "Relationship"))
        {
            var id = (string?)r.Attribute("Id") ?? "";
            var type = (string?)r.Attribute("Type") ?? "";
            var target = (string?)r.Attribute("Target") ?? "";
            var ext = string.Equals((string?)r.Attribute("TargetMode"), "External", StringComparison.OrdinalIgnoreCase);
            map[id] = new Relationship(id, type, ext ? target : Resolve(baseDir, target), ext);
        }
        return map;
    }

    public static string Resolve(string baseDir, string target)
    {
        // no percent-decoding here: SafePackage.Get resolves both encoded and decoded zip item names
        var parts = new List<string>();
        if (!target.StartsWith('/') && baseDir.Length > 0) parts.AddRange(baseDir.Split('/'));
        foreach (var seg in target.TrimStart('/').Split('/'))
        {
            if (seg == "" || seg == ".") continue;
            if (seg == "..") { if (parts.Count > 0) parts.RemoveAt(parts.Count - 1); continue; }
            parts.Add(seg);
        }
        return string.Join('/', parts);
    }

    public static string? MainDocumentPart(SafePackage pkg)
    {
        var e = pkg.Get("_rels/.rels");
        if (e == null) return null;
        var doc = SafeXml.Parse(e.Data);
        foreach (var r in doc.Root!.Elements(Ns.PR + "Relationship"))
        {
            var type = (string?)r.Attribute("Type");
            if (type == Ns.RelOfficeDocument || type == Ns.RelStrictOfficeDocument)
                return Resolve("", (string?)r.Attribute("Target") ?? "");
        }
        return null;
    }
}
