using System.Text.RegularExpressions;
namespace FastSim;

public static class MessageLibrary
{
    public static string ProtocolName(Template t) => $"S{t.Stream}F{t.Function}";
    public static string UniqueName(IEnumerable<Template> library, string desired, string? exceptId = null)
    {
        var used = library.Where(t => t.Id != exceptId).Select(t => t.TemplateName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!used.Contains(desired)) return desired;
        for (int suffix = 1; ; suffix++) if (!used.Contains(desired + "-" + suffix)) return desired + "-" + suffix;
    }
    public static void NormalizeLegacy(IEnumerable<Template> source)
    {
        var all = source.ToList(); var assigned = all.Where(t => t.TemplateName.Length > 0).ToList();
        foreach (var t in all.Where(t => t.TemplateName.Length == 0)) { t.TemplateName = UniqueName(assigned, ProtocolName(t)); assigned.Add(t); }
    }
    public static Template Add(Project project, Template source)
    {
        ValidateTemplate(source);
        var copy = ProjectStore.Copy(source); copy.Id = Guid.NewGuid().ToString("N");
        copy.TemplateName = UniqueName(project.Library, string.IsNullOrWhiteSpace(source.TemplateName) ? ProtocolName(copy) : source.TemplateName.Trim());
        project.Library.Add(copy); return copy;
    }
    public static Template Duplicate(Project p, Template source)
    {
        var copy = ProjectStore.Copy(source);
        // A generated protocol suffix remains a name, never a function number.
        if (Regex.IsMatch(copy.TemplateName, "^" + ProtocolName(copy) + "(?:-[0-9]+)?$")) copy.TemplateName = ProtocolName(copy);
        return Add(p, copy);
    }
    public static void Update(Project project, Template original, Template edited)
    {
        ValidateTemplate(edited);
        string name = edited.TemplateName.Trim();
        if (name.Length == 0 || name == original.TemplateName && Regex.IsMatch(original.TemplateName, "^" + ProtocolName(original) + "(?:-[0-9]+)?$") && ProtocolName(edited) != ProtocolName(original)) name = ProtocolName(edited);
        edited.TemplateName = UniqueName(project.Library, name, original.Id);
        original.TemplateName = edited.TemplateName; original.Name = edited.Name; original.Stream = edited.Stream; original.Function = edited.Function; original.W = edited.W;
        original.Description = edited.Description; original.Direction = edited.Direction; original.Role = edited.Role; original.Root = edited.Root?.Clone();
    }
    public static List<string> References(Project p, string id) => p.Flows.Where(f => f.Steps.Any(s => s.TemplateId == id)).Select(f => f.Name).Concat(p.Steps.Any(s => s.TemplateId == id) ? ["Legacy Sequence"] : Array.Empty<string>()).ToList();
    public static void Delete(Project p, string id)
    {
        void Remove(List<Step> steps)
        {
            // Remove an adjacent explicit Wait Reply too, so it cannot attach to another request.
            for (int i = steps.Count - 1; i >= 0; i--) if (steps[i].TemplateId == id)
            {
                var next = steps.Skip(i + 1).FirstOrDefault(s => s.Enabled);
                if (steps[i].Kind == "Send Primary" && next?.Kind == "Wait Reply") steps.Remove(next);
                steps.RemoveAt(i);
            }
        }
        Remove(p.Steps); foreach (var f in p.Flows) Remove(f.Steps);
        p.Rules.RemoveAll(r => r.ReplyId == id || r.RejectedReplyId == id); p.Library.RemoveAll(t => t.Id == id);
    }
    public static Step AddToFlow(FlowDefinition flow, Template t)
    {
        var step = new Step { TemplateId = t.Id, Name = t.TemplateName, Kind = t.Function % 2 == 0 ? "Wait Reply" : "Send Primary", WaitReply = t.W };
        flow.Steps.Add(step); return step;
    }
    public static void ValidateTemplate(Template t)
    {
        if (t.Stream > 127) throw new FormatException("Stream must be 0–127");
        if (t.Root != null) ValidateNode(t.Root);
    }
    public static void ValidateNode(Node node, int depth = 0)
    {
        if (depth > 64) throw new FormatException("LIST depth exceeds 64");
        if (!Sml.Types.Contains(node.Type)) throw new FormatException("Invalid node type: " + node.Type);
        if (node.Type == "L") { if (node.Value.Length != 0) throw new FormatException("LIST cannot have a scalar Value"); foreach (var c in node.Children) ValidateNode(c, depth + 1); }
        else { if (node.Children.Count > 0) throw new FormatException("Only LIST can contain children"); if (!node.Value.Contains("${")) { using var item = Sml.Build(node); } }
    }
}
