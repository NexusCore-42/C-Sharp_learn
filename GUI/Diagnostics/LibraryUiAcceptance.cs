using System.Runtime.InteropServices;
using System.Text.Json;

namespace FastSim;
public sealed partial class SimulatorForm
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindow(string className, string windowName);
    [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr handle, uint message, IntPtr wParam, IntPtr lParam);
    object LibrarySnapshot() => new { Ok = true, Library = templates, Flows = flowProject.Flows, Path = flowProjectPath };
    object AuditLibraryEditor()
    {
        if (flowRunner!.Running || templates.Any(t => t.Stream == 1 && t.Function == 5)) throw new InvalidOperationException("Fresh idle test project required");
        var checks = new List<string>();
        void Check(bool ok, string name) { if (!ok) throw new InvalidOperationException("Library UI: " + name); checks.Add(name); }
        IEnumerable<Control> Desc(Control c) { foreach (Control child in c.Controls) { yield return child; foreach (var nested in Desc(child)) yield return nested; } }
        void Menu(string name) { RebuildLibraryMenu(); libraryMenu.Items.Cast<ToolStripItem>().Single(i => i.Text == name).PerformClick(); }
        void Dialog(Action<Form> fill, Action open)
        {
            Exception? failure = null;
            using var timer = new System.Windows.Forms.Timer { Interval = 60 };
            timer.Tick += (_, _) =>
            {
                var f = Application.OpenForms.Cast<Form>().FirstOrDefault(x => x.Text.StartsWith("Edit Message") || x.Text.StartsWith("Edit Node"));
                if (f == null) return; timer.Stop();
                try { fill(f); Desc(f).OfType<Button>().Single(b => b.Text == "Apply").PerformClick(); }
                catch (Exception ex) { failure = ex; f.DialogResult = DialogResult.Cancel; }
            };
            timer.Start(); open(); if (failure != null) throw failure;
        }
        void Create()
        {
            library.SelectedNode = library.Nodes.Cast<TreeNode>().Single(n => n.Tag is byte s && s == 1);
            Dialog(f => { ((NumericUpDown)Desc(f).Single(c => c.Name == "Function")).Value = 5; }, () => Menu("Add Message"));
        }
        void Select(string name) => SelectLibraryNode(templates.Single(t => t.TemplateName == name));
        void NodeSelect(Node n)
        {
            TreeNode? Find(TreeNode t) => ReferenceEquals(t.Tag, n) ? t : t.Nodes.Cast<TreeNode>().Select(Find).FirstOrDefault(x => x != null);
            structure.Tree.SelectedNode = structure.Tree.Nodes.Cast<TreeNode>().Select(Find).First(x => x != null);
        }
        void Tool(string title) => structure.Controls.OfType<ToolStrip>().Single().Items.Cast<ToolStripItem>().Single(i => i.Text == title).PerformClick();
        void NodeMenu(string title) => structure.Tree.ContextMenuStrip!.Items.Cast<ToolStripItem>().Single(i => i.Text == title).PerformClick();
        void EditNode(string type, string value, string name = "", string description = "") => Dialog(f =>
        {
            Desc(f).OfType<ComboBox>().Single().SelectedItem = type;
            Desc(f).OfType<TextBox>().Single(t => t.Multiline).Text = value;
            Desc(f).OfType<TextBox>().Single(t => t.PlaceholderText == "Node Name").Text = name;
            Desc(f).OfType<TextBox>().Single(t => t.PlaceholderText == "Description").Text = description;
        }, () => Tool("Edit"));
        void DeleteConfirmed()
        {
            using var timer = new System.Windows.Forms.Timer { Interval = 60 }; bool confirmed = false;
            timer.Tick += (_, _) => { var h = FindWindow("#32770", "Delete Message"); if (h == IntPtr.Zero) return; timer.Stop(); confirmed = true; SendMessage(h, 0x111, 6, IntPtr.Zero); };
            timer.Start(); Menu("Delete"); Check(confirmed, "Delete presents actual confirmation dialog");
        }
        Create(); Create(); Create();
        Check(templates.Count(t => t.Stream == 1 && t.Function == 5) == 3 && current!.TemplateName == "S1F5-2", "Stream Add Message dialog creates S1F5 / S1F5-1 / S1F5-2");
        Select("S1F5-1"); DeleteConfirmed(); Create(); Check(current!.TemplateName == "S1F5-1", "Create reuses deleted suffix 1");
        var root = structure.Root!; Tool("Add Node"); var number = (Node)structure.Tree.SelectedNode!.Tag!; EditNode("U4", "100", "StatusId", "status identifier");
        Check(root.Children[0] == number && number.Value == "100", "LIST Add Node edits real value/name/description");
        Tool("Add Node"); var text = (Node)structure.Tree.SelectedNode!.Tag!; EditNode("A", "STATUS");
        NodeSelect(number); Tool("Add Node"); var inserted = (Node)structure.Tree.SelectedNode!.Tag!;
        Check(root.Children.SequenceEqual([number, inserted, text]), "Child Add Node inserts immediately after selected child and selects new node");
        Tool("Add List"); var list = (Node)structure.Tree.SelectedNode!.Tag!;
        Check(root.Children.SequenceEqual([number, inserted, list, text]) && list.Type == "L", "Child Add List inserts sibling immediately after selected node");
        Tool("Add List"); var nested = (Node)structure.Tree.SelectedNode!.Tag!;
        Check(list.Children.Single() == nested && nested.Type == "L", "Selected LIST Add List appends child LIST");
        Tool("Add Node"); EditNode("U1", "1"); Tool("Add Node"); EditNode("U1", "2");
        Check(nested.Children.Select(n => n.Value).SequenceEqual(["1", "2"]) && structure.Tree.SelectedNode!.Parent!.IsExpanded, "Nested LIST stays expanded and accepts sequential child edits");
        NodeSelect(list); NodeMenu("Copy"); NodeSelect(text); NodeMenu("Paste"); var paste = (Node)structure.Tree.SelectedNode!.Tag!;
        Check(root.Children[^1] == paste && !ReferenceEquals(paste.Children[0], nested) && paste.Children[0].Children[0].Value == "1", "Copy/Paste nested subtree is independent deep copy after scalar");
        NodeMenu("Duplicate"); var copy = (Node)structure.Tree.SelectedNode!.Tag!; copy.Children[0].Children[0].Value = "9";
        Check(paste.Children[0].Children[0].Value == "1" && root.Children[^1] == copy, "Duplicate inserts sibling and does not share descendants");
        NodeMenu("Move Up"); Check(root.Children[^2] == copy, "Move Up changes same-parent model order");
        NodeMenu("Move Down"); Check(root.Children[^1] == copy, "Move Down restores same-parent model order");
        NodeMenu("Delete"); Check(!root.Children.Contains(copy), "Delete removes model node");
        NodeSelect(list); NodeMenu("Paste"); Check(list.Children.Count == 2 && !ReferenceEquals(list.Children[1], paste), "Paste into LIST appends independent subtree");
        var exported = LibraryCodec.WriteSml([current!]); Check(exported.Contains("STATUS") && exported.Contains("StatusId"), "Editor changes appear in exported SML body and metadata");
        var nestedXml = LibraryCodec.WriteXml([current!]); var nestedSml = LibraryCodec.WriteSml([current!]);
        string semantic(Template t) => JsonSerializer.Serialize(t, ProjectStore.Json);
        Check(semantic(LibraryCodec.ReadXml(nestedXml)[0]) == semantic(current!) && semantic(LibraryCodec.ReadSml(nestedSml)[0]) == semantic(current!), "Edited nested message round-trips XML and SML exactly");
        // Keep an independent nested specimen for restart verification, while three F5 variants carry 1/2/3 on wire.
        Menu("Duplicate"); Check(current!.TemplateName == "S1F5-3", "Library Duplicate preserves entire edited body with fresh ID");
        var specimenId = current.Id;
        var flow = new FlowDefinition { Name = "Library Sequence" };
        CommitLibrary(p => p.Flows = [flow, new() { Name = "Other Flow" }]);
        for (int i = 0; i < 3; i++)
        {
            string name = i == 0 ? "S1F5" : "S1F5-" + i; Select(name);
            Dialog(f => { Desc(f).Single(c => c.Name == "MessageName").Text = "Variant " + (i + 1); var editor = Desc(f).OfType<StructureEditor>().Single(); editor.SetRoot(new() { Type = "U4", Value = (i + 1).ToString() }); }, () => Menu("Edit Message"));
            string id = current!.Id;
            RebuildLibraryMenu();
            ((ToolStripMenuItem)libraryMenu.Items.Cast<ToolStripItem>().Single(i => i.Text == "Add to Flow")).DropDownItems.Cast<ToolStripItem>().Single(i => i.Text == "Library Sequence").PerformClick();
            Check(SelectedFlow!.Steps[^1].TemplateId == id && SelectedFlow.Steps[^1].RuntimeTemplate == null, "Context Add to Flow uses TemplateId for " + name);
        }
        Check(flowProject.Flows.Single(f => f.Name == "Other Flow").Steps.Count == 0, "Multiple Flow submenu adds only to chosen Flow");
        CommitLibrary(p => p.Flows.RemoveAll(f => f.Name == "Other Flow"));
        flowGrid.CurrentCell = flowGrid.Rows[2].Cells[0];
        ((Button)flowEditControls.Single(c => c.Text == "Up")).PerformClick(); Check(SelectedFlow!.Steps[1].Name == "S1F5-2", "Flow Up updates execution order");
        ((Button)flowEditControls.Single(c => c.Text == "Down")).PerformClick(); Check(SelectedFlow!.Steps[2].Name == "S1F5-2", "Flow Down restores execution order");
        Select("S1F5-3"); Menu("Add to Flow"); Check(MessageLibrary.References(flowProject, specimenId).Contains("Library Sequence"), "Delete detects referencing Flow");
        // Preserve edited specimen by importing it under a new name before exercising referenced deletion.
        ImportLibraryText(nestedXml, true); var savedSpecimen = current!.Id;
        Select("S1F5-3"); DeleteConfirmed(); Check(SelectedFlow!.Steps.Count == 3 && templates.All(t => t.Id != specimenId), "Confirmed referenced delete removes related Flow step");
        ImportLibraryText(nestedSml, false); Check(current!.Id != savedSpecimen, "UI SML import adds independent template and refreshes tree");
        var restored = ProjectStore.Load(flowProjectPath);
        Check(JsonSerializer.Serialize(restored.Library, ProjectStore.Json) == JsonSerializer.Serialize(templates, ProjectStore.Json), "Save/load preserves edited nodes, order, nested lists and template metadata");
        ShowFlow(flow.Id); return new { Ok = true, Checks = checks, Library = templates, Flows = flowProject.Flows };
    }
}

