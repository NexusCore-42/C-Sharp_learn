using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Serialization;

namespace FastSim;
public static class ProjectStore
{
    public static readonly JsonSerializerOptions Json = new() { WriteIndented = true, MaxDepth = 256 };
    public static T Copy<T>(T value)
    {
        if (value is Project p)
        {
            var serializer = new XmlSerializer(typeof(Project)); using var text = new StringWriter();
            using (var writer = XmlWriter.Create(text, new XmlWriterSettings { NewLineHandling = NewLineHandling.Entitize })) serializer.Serialize(writer, p);
            using var reader = new StringReader(text.ToString()); return (T)serializer.Deserialize(reader)!;
        }
        return JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, Json), Json)!;
    }
    public static void AtomicText(string path, string text)
    {
        path = Path.GetFullPath(path); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temp, text, new UTF8Encoding(false)); File.Move(temp, path, true); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    public static void Save(string path, Project p)
    {
        Validate(p);
        var b = new StringBuilder();
        using (var writer = XmlWriter.Create(b, new XmlWriterSettings { Indent = true, OmitXmlDeclaration = true, NewLineHandling = NewLineHandling.Entitize })) new XmlSerializer(typeof(Project)).Serialize(writer, p);
        AtomicText(path, b.ToString());
    }
    public static Project Load(string path)
    {
        using var r = XmlReader.Create(path, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 20_000_000 });
        r.MoveToContent();
        if (r.LocalName != "FastSimProject") throw new FormatException("不是 FastSim 项目 XML。WinSECS XML 需实际样本适配，当前未验证兼容。");
        var serializer = new XmlSerializer(typeof(Project));
        serializer.UnknownNode += (_, e) => { if (e.NodeType is XmlNodeType.Element or XmlNodeType.Attribute) throw new FormatException($"无法无损转换未知节点/属性 {e.Name}（第 {e.LineNumber} 行）；已停止导入"); };
        var p = (Project)serializer.Deserialize(r)!; Validate(p); return p;
    }
    public static void Validate(Project p)
    {
        if (p.Version != 1) throw new FormatException("不支持的项目版本");
        if (p.SimulationRole is not ("" or "EAP" or "EQP")) throw new FormatException("SimulationRole must be EAP or EQP");
        MessageLibrary.NormalizeLegacy(p.Library);
        if (p.Library.Any(t => string.IsNullOrWhiteSpace(t.Id)) || p.Flows.Any(f => string.IsNullOrWhiteSpace(f.Id))) throw new FormatException("Template ID / Flow ID cannot be empty");
        if (p.Library.Select(t => t.TemplateName).Distinct(StringComparer.OrdinalIgnoreCase).Count() != p.Library.Count) throw new FormatException("TemplateName 重复");
        if (p.Flows.Select(f => f.Id).Distinct().Count() != p.Flows.Count) throw new FormatException("Flow ID 重复");
        p.Connection.Options();
        if (!new[] { "Offline", "Local", "Remote" }.Contains(p.Control)) throw new FormatException("控制状态无效");
        if (p.Library.Select(x => x.Id).Distinct().Count() != p.Library.Count) throw new FormatException("模板 ID 重复");
        foreach (var t in p.Library) { MessageLibrary.ValidateTemplate(t); Sml.Parse(Sml.Write(t)); }
        if (p.Variables.Select(x => x.Name).Distinct().Count() != p.Variables.Count) throw new FormatException("全局变量重复");
        foreach (var v in p.Variables) { using var item = Sml.Build(new() { Type = v.Type, Value = v.Value }); }
        JobManager.Validate(p, []);
        foreach (var rule in p.Rules)
        {
            var request = new Template { Stream = rule.Stream, Function = rule.Function };
            foreach (var id in new[] { rule.ReplyId, rule.RejectedReplyId }.Where(x => x.Length > 0))
            {
                var reply = p.Library.SingleOrDefault(x => x.Id == id) ?? throw new FormatException("自动回复模板不存在");
                if (reply.Stream != request.Stream || reply.Function != request.Function + 1 || reply.W) throw new FormatException("自动应答必须为同 Stream 的 F+1 且 W=false");
            }
            if (rule.SaveJobs && !rule.Extract.Any(x => x.Name == "PJID" || x.Name == "CJID")) throw new FormatException("保存作业需提取 PJID 或 CJID");
        }
        foreach (var s in p.Steps.Concat(p.Flows.SelectMany(f => f.Steps))) { FlowRunner.ValidateStep(s, FlowRunner.TemplateFor(p, s)); }
        if (p.Logs.UiLimit is < 100 or > 100000 || p.Logs.FileMegabytes is < 1 or > 1000 || p.Logs.FileCount is < 1 or > 100) throw new FormatException("日志范围：界面 100–100000 条，文件 1–1000 MB，保留 1–100 个");
    }
}



