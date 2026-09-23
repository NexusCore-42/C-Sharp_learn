namespace FastSim;
public sealed partial class SimulatorForm
{
    readonly ContextMenuStrip libraryMenu = new();
    volatile Dictionary<(byte, byte), Template> autoReplyTemplates = new();
    void SetupLibraryMenus(ToolStripMenuItem file, ToolStripMenuItem menu)
    {
        void Add(ToolStripMenuItem parent, string name, Action action) => parent.DropDownItems.Add(name, null, (_, _) => Guard(action));
        Add(menu, "Add Message…", () => AddLibraryMessage(library.SelectedNode?.Tag is byte s ? s : current?.Stream ?? 1));
        Add(menu, "Import XML…", () => ImportLibraryDialog(true)); Add(menu, "Import SML…", () => ImportLibraryDialog(false));
        Add(menu, "Export Library XML…", () => ExportLibraryDialog(true, false)); Add(menu, "Export Library SML…", () => ExportLibraryDialog(false, false));
        Add(file, "Export current XML…", () => ExportLibraryDialog(true, true));
        Add(file, "Open Project…", () => { using var dialog = new OpenFileDialog { Filter = "FastSim Project|*.xml" }; if (dialog.ShowDialog(this) == DialogResult.OK) LoadFlowProject(dialog.FileName); });
        Add(file, "Save Project", SaveFlowProject);
        library.ContextMenuStrip = libraryMenu;
        library.NodeMouseClick += (_, e) => { if (e.Button == MouseButtons.Right) library.SelectedNode = e.Node; };
        libraryMenu.Opening += (_, _) => RebuildLibraryMenu();
    }
    void RebuildLibraryMenu()
    {
        libraryMenu.Items.Clear();
        void Item(string title, Action action) => libraryMenu.Items.Add(title, null, (_, _) => Guard(action));
        if (library.SelectedNode?.Tag is Template t)
        {
            Item("Edit Message", () => EditLibraryMessage(t));
            var add = new ToolStripMenuItem("Add to Flow") { Enabled = !mockMode && flowRunner?.Running != true }; libraryMenu.Items.Add(add);
            if (flowProject.Flows.Count == 1) add.Click += (_, _) => Guard(() => AddLibraryToFlow(t, flowProject.Flows[0].Id));
            else foreach (var flow in flowProject.Flows) add.DropDownItems.Add(flow.Name, null, (_, _) => Guard(() => AddLibraryToFlow(t, flow.Id)));
            if (flowProject.Flows.Count != 1) add.DropDownItems.Add("New Flow…", null, (_, _) => Guard(() => { var name = Dialogs.Text(this, "Flow name", "New Flow"); if (string.IsNullOrWhiteSpace(name)) return; var flow = new FlowDefinition { Name = name.Trim() }; CommitLibrary(p => { p.Flows.Add(flow); MessageLibrary.AddToFlow(flow, p.Library.Single(x => x.Id == t.Id)); }); ShowFlow(flow.Id); }));
            Item("Duplicate", () => DuplicateLibraryMessage(t)); Item("Delete", () => DeleteLibraryMessage(t));
            Item("Export as SML", () => ExportLibraryDialog(false, true)); Item("Export as XML", () => ExportLibraryDialog(true, true));
        }
        else
        {
            byte stream = library.SelectedNode?.Tag is byte s ? s : (byte)1;
            Item("Add Message", () => AddLibraryMessage(stream));
            Item("Expand All", () => library.SelectedNode?.ExpandAll()); Item("Collapse All", () => library.SelectedNode?.Collapse());
        }
    }
    void CommitLibrary(Action<Project> change, Func<Project, string?>? select = null)
    {
        if (flowRunner?.Running == true) throw new InvalidOperationException("Stop Flow before editing the Library");
        var next = ProjectStore.Copy(flowProject); change(next); ProjectStore.Validate(next);
        if (!mockMode && flowProjectPath.Length > 0) { next.Connection = ProjectStore.Copy(config); ProjectStore.Save(flowProjectPath, next); }
        string? selectedId = select?.Invoke(next) ?? current?.Id; string? flowId = SelectedFlow?.Id;
        flowProject = next; templates = next.Library; BuildLibrary(); RefreshAutoReplies();
        RefreshFlowList(Math.Max(0, next.Flows.FindIndex(f => f.Id == flowId)));
        var selected = templates.FirstOrDefault(t => t.Id == selectedId) ?? templates.FirstOrDefault();
        if (selected != null) { SelectTemplate(selected); SelectLibraryNode(selected); }
        else { current = null; structure.SetRoot(null); foreach (var field in editFields.Values) field.Clear(); xml.Clear(); UpdateButtons(); }
    }
    void AddLibraryMessage(byte stream)
    {
        var source = new Template { Stream = stream, Function = 1, Root = new(), Name = "", TemplateName = $"S{stream}F1" };
        var edited = MessageDialog.Edit(this, source); if (edited == null) return;
        string? id = null; CommitLibrary(p => id = MessageLibrary.Add(p, edited).Id, _ => id);
    }
    void EditLibraryMessage(Template t)
    {
        var edited = MessageDialog.Edit(this, t); if (edited == null) return;
        CommitLibrary(p => MessageLibrary.Update(p, p.Library.Single(x => x.Id == t.Id), edited), _ => t.Id);
    }
    void DuplicateLibraryMessage(Template t) { string? id = null; CommitLibrary(p => id = MessageLibrary.Duplicate(p, p.Library.Single(x => x.Id == t.Id)).Id, _ => id); }
    void DeleteLibraryMessage(Template t)
    {
        var flows = MessageLibrary.References(flowProject, t.Id);
        string text = $"Delete \"{t.TemplateName}\"?" + (flows.Count == 0 ? "" : "\r\nCurrently referenced by:\r\n- " + string.Join("\r\n- ", flows) + "\r\nRelated steps and their explicit Wait Reply will also be deleted. Delete anyway?");
        if (MessageBox.Show(this, text, "Delete Message", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes) CommitLibrary(p => MessageLibrary.Delete(p, t.Id));
    }
    void AddLibraryToFlow(Template t, string flowId)
    {
        CommitLibrary(p => MessageLibrary.AddToFlow(p.Flows.Single(f => f.Id == flowId), p.Library.Single(x => x.Id == t.Id)), _ => t.Id); ShowFlow(flowId);
    }
    void ShowFlow(string id) { flowChoice.SelectedIndex = flowProject.Flows.FindIndex(f => f.Id == id); if (flowPage?.Parent is TabControl tabs) tabs.SelectedTab = flowPage; }
    void ImportLibraryText(string text, bool xmlFormat) => CommitLibrary(p => LibraryCodec.Import(p, text, xmlFormat), p => p.Library.LastOrDefault()?.Id);
    void ImportLibraryDialog(bool xmlFormat)
    {
        using var dialog = new OpenFileDialog { Filter = xmlFormat ? "XML Library|*.xml" : "SML Library|*.sml" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try { ImportLibraryText(File.ReadAllText(dialog.FileName), xmlFormat); }
        catch (Exception ex) { MessageBox.Show(this, ex.GetBaseException().Message, xmlFormat ? "Import XML Failed" : "Import SML Failed"); }
    }
    void ExportLibraryDialog(bool xmlFormat, bool single)
    {
        if (!structure.TryCommitCell()) return;
        if (single && current == null) throw new InvalidOperationException("Select a message first");
        using var dialog = new SaveFileDialog { Filter = xmlFormat ? "XML Library|*.xml" : "SML Library|*.sml", FileName = single ? "message" : "library" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        var source = single ? new[] { current! } : templates.ToArray(); ProjectStore.AtomicText(dialog.FileName, xmlFormat ? LibraryCodec.WriteXml(source) : LibraryCodec.WriteSml(source));
    }
    void RefreshAutoReplies() => autoReplyTemplates = templates.Where(t => t.Function % 2 == 0 && !t.W && (t.Role == role || t.Role is "" or "Both")).GroupBy(t => (t.Stream, t.Function)).ToDictionary(g => g.Key, g => ProjectStore.Copy(g.OrderByDescending(t => t.Role == role).First()));
    void LibraryBodyChanged()
    {
        if (current == null || !templates.Any(t => ReferenceEquals(t, current))) return;
        current.Root = structure.Root; RefreshXml(); RefreshAutoReplies();
        if (!mockMode) { try { SaveFlowProject(); RefreshFlowRows(); } catch (Exception e) { liveLog?.Warning("Save Project failed: " + e.Message); MessageBox.Show(this, e.Message, "Save Project Failed"); } }
    }
}

