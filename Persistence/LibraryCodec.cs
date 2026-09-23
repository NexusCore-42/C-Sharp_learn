using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using System.Xml.Serialization;

namespace FastSim;
public static class LibraryCodec
{
    public static string WriteXml(IEnumerable<Template> library)
    {
        XElement Body(Node n) => new("Item", new XAttribute("Type", n.Type), new XAttribute("Name", n.Name), new XAttribute("Description", n.Description), new XAttribute("Value", n.Value), n.Children.Select(Body));
        return new XElement("FastSimLibrary", new XAttribute("Version", 1), library.Select(t =>
        {
            MessageLibrary.ValidateTemplate(t);
            return new XElement("Message", new XAttribute("TemplateId", t.Id), new XAttribute("TemplateName", t.TemplateName), new XAttribute("Name", t.Name), new XAttribute("Stream", t.Stream), new XAttribute("Function", t.Function), new XAttribute("W", t.W), new XAttribute("Direction", t.Direction), new XAttribute("Role", t.Role), new XAttribute("Description", t.Description), t.Root == null ? null : Body(t.Root));
        })).ToString();
    }
    public static List<Template> ReadXml(string text)
    {
        using var reader = XmlReader.Create(new StringReader(text), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 20_000_000 });
        var doc = XDocument.Load(reader, LoadOptions.SetLineInfo); var root = doc.Root ?? throw new FormatException("Missing XML root");
        if (root.Name == "FastSimProject")
        {
            using var projectReader = root.CreateReader(); var project = (Project)new XmlSerializer(typeof(Project)).Deserialize(projectReader)!;
            ProjectStore.Validate(project); return project.Library;
        }
        Exception Error(XElement e, string message) => new FormatException($"{message} at line {((IXmlLineInfo)e).LineNumber}");
        string Required(XElement e, string key) => (string?)e.Attribute(key) ?? throw Error(e, "Missing " + key + " attribute");
        Node Body(XElement e, int depth)
        {
            if (depth > 64) throw Error(e, "LIST depth exceeds 64");
            if (e.Name != "Item") throw Error(e, "Expected Item");
            var n = new Node { Type = Sml.NormalizeType((string?)e.Attribute("Type") ?? Required(e, "Format")), Name = (string?)e.Attribute("Name") ?? "", Description = (string?)e.Attribute("Description") ?? "", Value = (string?)e.Attribute("Value") ?? "" };
            n.Children = e.Elements().Select(c => Body(c, depth + 1)).ToList();
            try { MessageLibrary.ValidateNode(n); } catch (Exception ex) { throw Error(e, ex.Message); }
            return n;
        }
        var messages = root.Name.LocalName is "Message" or "SecsMessage" ? new[] { root } : root.Name == "FastSimLibrary" ? root.Elements().ToArray() : throw Error(root, "Expected FastSimLibrary or Message");
        var result = new List<Template>();
        foreach (var element in messages)
        {
            if (element.Name.LocalName is not ("Message" or "SecsMessage")) throw Error(element, "Expected Message");
            var t = new Template();
            if (!byte.TryParse(Required(element, "Stream"), out byte s) || s > 127) throw Error(element, "Invalid Stream");
            if (!byte.TryParse(Required(element, "Function"), out byte f)) throw Error(element, "Invalid Function");
            if (!bool.TryParse(Required(element, "W"), out bool w)) throw Error(element, "Invalid W-Bit");
            t.Id = (string?)element.Attribute("TemplateId") ?? t.Id; t.TemplateName = (string?)element.Attribute("TemplateName") ?? $"S{s}F{f}";
            t.Name = (string?)element.Attribute("Name") ?? ""; t.Description = (string?)element.Attribute("Description") ?? ""; t.Direction = (string?)element.Attribute("Direction") ?? ""; t.Role = (string?)element.Attribute("Role") ?? "";
            t.Stream = s; t.Function = f; t.W = w;
            if (element.Elements().Count() > 1) throw Error(element, "Message can have only one root Item");
            t.Root = element.Elements().FirstOrDefault() is { } body ? Body(body, 0) : null; result.Add(t);
        }
        if (result.Count == 0) throw new FormatException("Library contains no messages"); return result;
    }
    public sealed class NodeMeta { public string Name { get; set; } = ""; public string Description { get; set; } = ""; }
    public sealed class Metadata
    {
        public string TemplateId { get; set; } = ""; public string TemplateName { get; set; } = ""; public string Name { get; set; } = "";
        public string Description { get; set; } = ""; public string Direction { get; set; } = ""; public string Role { get; set; } = "";
        public Dictionary<string, NodeMeta> Nodes { get; set; } = [];
    }
    public static string WriteSml(IEnumerable<Template> library) => string.Join("\r\n", library.Select(t =>
    {
        MessageLibrary.ValidateTemplate(t);
        var meta = new Metadata { TemplateId = t.Id, TemplateName = t.TemplateName, Name = t.Name, Description = t.Description, Direction = t.Direction, Role = t.Role };
        void Visit(Node n, string path) { meta.Nodes[path] = new() { Name = n.Name, Description = n.Description }; for (int i = 0; i < n.Children.Count; i++) Visit(n.Children[i], path + "/" + i); }
        if (t.Root != null) Visit(t.Root, "");
        // Standard SML remains readable by parsers that ignore // comments.
        return "// @FastSim-Metadata " + JsonSerializer.Serialize(meta) + "\r\n" + Sml.Write(t);
    }));
    public static List<Template> ReadSml(string text)
    {
        var markers = Regex.Matches(text, @"^// @FastSim-Metadata (.+)\r?$", RegexOptions.Multiline);
        if (markers.Count == 0) { var plain = Sml.Parse(text); foreach (var t in plain) t.TemplateName = t.Name.Length > 0 ? t.Name : MessageLibrary.ProtocolName(t); return plain; }
        var result = new List<Template>();
        string prefix = text[..markers[0].Index]; if (Regex.Replace(prefix, @"(?m)^\s*//.*$", "").Trim().Length > 0) result.AddRange(Sml.Parse(prefix));
        for (int i = 0; i < markers.Count; i++)
        {
            var marker = markers[i]; int end = i + 1 < markers.Count ? markers[i + 1].Index : text.Length;
            var parsed = Sml.Parse(text[(marker.Index + marker.Length)..end]);
            var meta = JsonSerializer.Deserialize<Metadata>(marker.Groups[1].Value) ?? throw new FormatException("Invalid SML metadata"); var t = parsed[0];
            if (meta.TemplateId == null || meta.TemplateName == null || meta.Name == null || meta.Description == null || meta.Direction == null || meta.Role == null || meta.Nodes == null || meta.Nodes.Values.Any(n => n == null || n.Name == null || n.Description == null)) throw new FormatException("Invalid SML metadata: fields cannot be null");
            if (meta.TemplateId.Length > 0) t.Id = meta.TemplateId; t.TemplateName = meta.TemplateName; t.Name = meta.Name; t.Description = meta.Description; t.Direction = meta.Direction; t.Role = meta.Role;
            foreach (var (path, value) in meta.Nodes) { var node = t.Root?.At(path) ?? throw new FormatException("SML metadata path does not exist: " + path); node.Name = value.Name; node.Description = value.Description; }
            result.AddRange(parsed);
        }
        return result;
    }
    public static List<Template> Import(Project p, string text, bool xml)
    {
        var parsed = xml ? ReadXml(text) : ReadSml(text); foreach (var t in parsed) MessageLibrary.ValidateTemplate(t);
        var staging = new Project { Library = p.Library.ToList() }; var added = parsed.Select(t => MessageLibrary.Add(staging, t)).ToList();
        p.Library.AddRange(added); return added;
    }
}

