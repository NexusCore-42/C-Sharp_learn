
namespace FastSim;

// Existing industrial workspace; connection and flow UI are in the adjacent SimulatorForm partial files.
public sealed partial class SimulatorForm : Form
{
    readonly TreeView library = new() { Dock = DockStyle.Fill, HideSelection = false, ShowPlusMinus = true, ShowLines = true, ItemHeight = 24, BorderStyle = BorderStyle.FixedSingle };
    readonly StructureEditor structure = new();
    readonly TreeView transactions = new() { Dock = DockStyle.Fill, HideSelection = false, ShowPlusMinus = true, ItemHeight = 25, BorderStyle = BorderStyle.FixedSingle };
    readonly TreeView parsed = new() { Dock = DockStyle.Fill, HideSelection = false, BorderStyle = BorderStyle.FixedSingle };
    readonly TextBox raw = CodeBox();
    readonly TextBox xml = CodeBox();
    readonly TextBox monitor = CodeBox();
    readonly DataGridView trace = new() { Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false, RowHeadersVisible = false, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, SelectionMode = DataGridViewSelectionMode.FullRowSelect, MultiSelect = false, BackgroundColor = Color.White, BorderStyle = BorderStyle.FixedSingle };
    readonly Dictionary<string, TextBox> editFields = [];
    readonly Dictionary<string, TextBox> detailFields = [];
    readonly ToolStripStatusLabel status = new() { Text = "MOCK DATA · 通信未启用", ForeColor = Color.DarkOrange };
    readonly ToolStripStatusLabel counts = new() { Text = "TX: 0   RX: 0" };
    List<Template> templates;
    readonly List<MonitorTransaction> items = [];
    Template? current;
    Node? originalRoot;
    uint nextBytes = 0x2000;
    MonitorTransaction? selectedTransaction;
    readonly Button sendPrimary;
    readonly Button sendSecondary;
    static TextBox CodeBox() => new() { Dock = DockStyle.Fill, ReadOnly = true, Multiline = true, ScrollBars = ScrollBars.Both, WordWrap = false, Font = new("Consolas", 9) };
    public SimulatorForm(bool mockMode = false, string role = "EQP", string? projectPath = null)
    {
        flowProjectPath = projectPath ?? ""; this.mockMode = mockMode; this.role = role == "EAP" ? "EAP" : "EQP";
        Text = "SECS/GEM Simulator - EAP Communication Test" + (mockMode ? " [MOCK UI]" : " · " + this.role);
        Font = new("Segoe UI", 9); AutoScaleMode = AutoScaleMode.Dpi; Size = new(1440, 1030); MinimumSize = new(1100, 800); StartPosition = FormStartPosition.CenterScreen;
        templates = Sml.Parse("'Are You There': 'S1F1' W .\n'On Line Data': 'S1F2' <L [2] <A 'FastSim'> <A '1.0'>> .\n'Selected Equipment Status Request': 'S1F3' W <L [1] <U4 1>> .\n'Selected Equipment Status Data': 'S1F4' <L [1] <A 'IDLE'>> .\n'Host Command Send': 'S2F41' W <L [2] <A 'START'> <L [1] <L [2] <A 'RCMD'> <A 'START'>>>> .\n'Host Command Ack': 'S2F42' <L [1] <B 0>> .\n'Event Report Send': 'S6F11' W <L [3] <U4 1> <U4 42> <L [0]>> .\n'Event Report Ack': 'S6F12' <B 0> .\n'Process Program Request': 'S7F19' W <A 'DEMO'> .\n'Process Program Data': 'S7F20' <L [0]> .");
        templates.AddRange(Sml.Parse("'Establish Communication': 'S1F13' W <L [2] <A 'FastSim'> <A '1.0'>> .\n'Establish Communication Ack': 'S1F14' <L [2] <B 0> <L [0]>> ."));
        templates.AddRange(Sml.Parse("'Alarm Report Send': 'S5F1' W <L [3] <B 128> <U4 1> <A 'Demo Alarm'>> .\n'Alarm Report Ack': 'S5F2' <B 0> ."));
        sendPrimary = Dialogs.Button(mockMode ? "Mock Send Primary" : "Send Primary", () => { if (mockMode) Guard(AddPrimary); else LiveAction(() => SendLiveAsync(current)); });
        sendSecondary = Dialogs.Button(mockMode ? "Mock Send Secondary" : "Send Secondary", () => { if (mockMode) Guard(AddSecondary); else LiveAction(ReplyLiveAsync); });
        if (!mockMode) templates = DefaultCatalog.Load();
        MessageLibrary.NormalizeLegacy(templates); flowProject.Library = templates;
        Build(); BuildLibrary(); if (mockMode) Seed(); else InitializeLive();
        library.BeforeSelect += (_, e) => { if (!structure.TryCommitCell()) e.Cancel = true; };
        library.AfterSelect += (_, e) => { if (e.Node?.Tag is Template t) SelectTemplate(t); };
        transactions.AfterSelect += (_, e) =>
        {
            var n = e.Node; while (n != null && n.Tag is not MonitorTransaction) n = n.Parent;
            selectedTransaction = n?.Tag as MonitorTransaction;
            if (e.Node?.Tag is MonitorMessage m) ShowDetail(m);
            else if (selectedTransaction?.Primary != null) ShowDetail(selectedTransaction.Primary);
            UpdateButtons();
        };
        structure.Changed += LibraryBodyChanged;
        structure.Tree.NodeMouseDoubleClick += (_, _) => structure.EditSelected(this);
        trace.CurrentCellChanged += (_, _) => { if (trace.CurrentRow?.Tag is MonitorMessage m) SelectTraceMessage(m); };
        if (templates.Count > 0) SelectTemplate(templates.FirstOrDefault(t => t.Stream == 2 && t.Function == 41) ?? templates[0]);
        if (current != null) SelectLibraryNode(current);
        if (transactions.Nodes.Count > 0) { transactions.Nodes[0].Expand(); transactions.SelectedNode = transactions.Nodes[0].Nodes[1]; }
    }
    void Guard(Action action) { try { action(); } catch (Exception e) { MessageBox.Show(this, e.Message, "SECS/GEM Simulator"); } }
    static Control Section(string title, Control body)
    {
        var panel = new Panel { Dock = DockStyle.Fill, Padding = new(3), BackColor = Color.FromArgb(210, 221, 231) };
        var label = new Label { Text = title, Dock = DockStyle.Top, Height = 25, Font = new("Segoe UI", 9, FontStyle.Bold), ForeColor = Color.FromArgb(25, 45, 75), Padding = new(5, 4, 0, 0) };
        panel.Controls.Add(body); panel.Controls.Add(label); return panel;
    }
    static SplitContainer Split(Orientation orientation, int distance, Size size) => new() { Dock = DockStyle.Fill, Orientation = orientation, Size = size, SplitterDistance = distance, SplitterWidth = 6, BackColor = SystemColors.Control };
    void Build()
    {
        var menu = new MenuStrip(); MainMenuStrip = menu;
        ToolStripMenuItem Top(string name) { var m = new ToolStripMenuItem(name); menu.Items.Add(m); return m; }
        void Add(ToolStripMenuItem p, string name, Action action) => p.DropDownItems.Add(name, null, (_, _) => Guard(action));
        var file = Top("File"); Add(file, "Export current SML…", ExportSml); Add(file, "Exit", Close);
        var port = Top("Port"); if (mockMode) port.DropDownItems.Add(new ToolStripMenuItem("通信未启用 · Mock Data") { Enabled = false }); else BuildPortMenu(port);
        var lib = Top("Library"); Add(lib, "Expand All", library.ExpandAll); Add(lib, "Collapse All", library.CollapseAll); SetupLibraryMenus(file, lib);
        var messages = Top("Messages"); if (mockMode) { Add(messages, "Add mock primary", AddPrimary); Add(messages, "Reply to selected mock transaction", AddSecondary); Add(messages, "Reload Mock Data", Seed); } else { Add(messages, "Send Primary", () => LiveAction(() => SendLiveAsync(current))); Add(messages, "Reply to selected transaction", () => LiveAction(ReplyLiveAsync)); Add(messages, "Fault injection (explicit)…", FaultDialog); }
        var setting = Top("Setting"); Add(setting, "Reset current editor", ResetEditor);
        var help = Top("Help"); Add(help, "About", () => MessageBox.Show(this, "Secs4Net 3.1.0 + FastSim fixes\nP = Primary；S = Secondary\nTX = Send；RX = Receive\n事务按连接会话、发起方向和 System Bytes 配对。\n" + (mockMode ? "Mock Data only" : "真实 HSMS 双向通信；报文级验证，非完整 GEM/E40/E94")));
        var statusBar = new StatusStrip(); statusBar.Items.Add(status); statusBar.Items.Add(new ToolStripStatusLabel(mockMode ? " | EQP ↔ EAP (Mock) | " : " | HSMS / SECS-II | ")); statusBar.Items.Add(counts); statusBar.Items.Add(new ToolStripStatusLabel { Spring = true }); statusBar.Items.Add(new ToolStripStatusLabel(mockMode ? "Ready · UI only" : "Ready"));
        var outer = Split(Orientation.Horizontal, 785, new(1400, 955)); outer.Panel1MinSize = 480; outer.Panel2MinSize = 120;
        var workspace = Split(Orientation.Vertical, 670, new(1400, 780)); workspace.Panel1MinSize = 420; workspace.Panel2MinSize = 480;
        var left = Split(Orientation.Horizontal, 325, new(670, 780)); left.Panel1MinSize = 160; left.Panel2MinSize = 260;
        var right = Split(Orientation.Horizontal, 415, new(724, 780)); right.Panel1MinSize = 180; right.Panel2MinSize = 240;
        left.Panel1.Controls.Add(Section("Message Library", library));
        var edit = new Panel { Dock = DockStyle.Fill }; var fields = Fields(editFields, ["Message", "Name", "Type", "Reply"], 2);
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 82, Padding = new(2), AutoScroll = true };
        buttons.Controls.Add(Dialogs.Button("Add Node", () => Guard(() => structure.AddNode(new Node { Type = "A", Value = "" }))));
        buttons.Controls.Add(Dialogs.Button("Delete Node", structure.DeleteNode)); buttons.Controls.Add(Dialogs.Button("Edit Node", () => structure.EditSelected(this))); buttons.Controls.Add(Dialogs.Button("Reset", ResetEditor)); buttons.Controls.Add(sendPrimary); buttons.Controls.Add(sendSecondary);
        edit.Controls.Add(structure); edit.Controls.Add(buttons); edit.Controls.Add(fields); left.Panel2.Controls.Add(Section("Message Editor", edit));
        right.Panel1.Controls.Add(Section("SECS Transaction Monitor" + (mockMode ? " · MOCK DATA" : " · LIVE"), transactions));
        var details = new Panel { Dock = DockStyle.Fill }; var metadata = Fields(detailFields, ["Type", "Direction", "SxFy", "W-Bit", "System Bytes", "Time", "Name"], 2);
        var body = Split(Orientation.Vertical, 340, new(700, 210)); body.Panel1.Controls.Add(Section("Parsed Structure", parsed)); body.Panel2.Controls.Add(Section("Raw SECS-II · SML", raw));
        details.Controls.Add(body); details.Controls.Add(metadata); right.Panel2.Controls.Add(Section("Selected Message Detail", details));
        if (mockMode) workspace.Panel1.Controls.Add(left); else { var navigation = new TabControl { Dock = DockStyle.Fill }; var messagesPage = new TabPage("Message Library / Editor"); messagesPage.Controls.Add(left); flowPage = new TabPage("Flow / Sequence"); flowPage.Controls.Add(BuildFlowPanel()); navigation.TabPages.Add(messagesPage); navigation.TabPages.Add(flowPage); workspace.Panel1.Controls.Add(navigation); } workspace.Panel2.Controls.Add(right); outer.Panel1.Controls.Add(workspace);
        var tabs = new TabControl { Dock = DockStyle.Fill }; foreach (var (name, bodyControl) in new (string, Control)[] { ("Trace", trace), ("Monitor", monitor), ("XML", xml) }) { var page = new TabPage(name); page.Controls.Add(bodyControl); tabs.TabPages.Add(page); }
        foreach (var name in new[] { "Time", "Dir", "P/S", "SxFy", "W", "SystemBytes", "Info" }) trace.Columns.Add(name, name);
        trace.Columns[0].FillWeight = 110; trace.Columns[1].FillWeight = 35; trace.Columns[2].FillWeight = 35; trace.Columns[3].FillWeight = 60; trace.Columns[4].FillWeight = 30; trace.Columns[5].FillWeight = 100; trace.Columns[6].FillWeight = 300;
        outer.Panel2.Controls.Add(tabs); Controls.Add(outer); Controls.Add(statusBar); if (!mockMode) Controls.Add(BuildConnectionPanel()); Controls.Add(menu);
    }
    static TableLayoutPanel Fields(Dictionary<string, TextBox> boxes, string[] names, int columns)
    {
        int rows = (names.Length + columns - 1) / columns;
        var panel = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = columns * 2, RowCount = rows, Height = rows * 29 + 8, Padding = new(3) };
        for (int c = 0; c < columns; c++) { panel.ColumnStyles.Add(new(SizeType.Absolute, 110)); panel.ColumnStyles.Add(new(SizeType.Percent, 100f / columns)); }
        for (int r = 0; r < rows; r++) panel.RowStyles.Add(new(SizeType.Absolute, 29));
        for (int i = 0; i < names.Length; i++)
        {
            var text = new TextBox { Dock = DockStyle.Fill, ReadOnly = true, BackColor = Color.White, Margin = new(2) }; boxes[names[i]] = text;
            panel.Controls.Add(new Label { Text = names[i] + ":", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true, Margin = new(2) }, (i % columns) * 2, i / columns); panel.Controls.Add(text, (i % columns) * 2 + 1, i / columns);
            if (i == names.Length - 1 && i % columns == 0) panel.SetColumnSpan(text, columns * 2 - 1);
        }
        return panel;
    }
    void BuildLibrary()
    {
        library.BeginUpdate(); library.Nodes.Clear();
        foreach (var group in templates.GroupBy(t => t.Stream).OrderBy(g => g.Key))
        {
            var root = new TreeNode($"S{group.Key} - {DefaultCatalog.StreamName(group.Key)}") { Tag = group.Key };
            var attached = new HashSet<string>();
            TreeNode MessageNode(Template t) => new(t.TemplateName + (t.Name.Length > 0 ? "   " + t.Name : "")) { Tag = t, ToolTipText = t.Description };
            foreach (var primary in group.Where(t => t.Function % 2 == 1).OrderBy(t => t.Function))
            {
                var node = MessageNode(primary); root.Nodes.Add(node); attached.Add(primary.Id);
                foreach (var secondary in group.Where(t => t.Function == primary.Function + 1 && !attached.Contains(t.Id))) { node.Nodes.Add(MessageNode(secondary)); attached.Add(secondary.Id); }
            }
            foreach (var other in group.Where(t => !attached.Contains(t.Id)).OrderBy(t => t.Function)) root.Nodes.Add(MessageNode(other));
            library.Nodes.Add(root); root.Expand();
        }
        library.ShowNodeToolTips = true; library.EndUpdate();
    }    void SelectLibraryNode(Template template)
    {
        foreach (TreeNode group in library.Nodes) foreach (TreeNode p in group.Nodes)
        { if (ReferenceEquals(p.Tag, template)) { library.SelectedNode = p; return; } foreach (TreeNode s in p.Nodes) if (ReferenceEquals(s.Tag, template)) { p.Expand(); library.SelectedNode = s; return; } }
    }
    void SelectTemplate(Template t)
    {
        current = t; originalRoot = t.Root?.Clone(); structure.SetRoot(t.Root);
        editFields["Message"].Text = $"S{t.Stream}F{t.Function}"; editFields["Name"].Text = t.TemplateName;
        editFields["Type"].Text = t.Function % 2 == 1 ? "Primary" : "Secondary";
        editFields["Reply"].Text = t.Function % 2 == 1 ? $"S{t.Stream}F{t.Function + 1}" : "—";
        UpdateButtons(); RefreshXml();
    }
    void ResetEditor() { if (current == null) return; current.Root = originalRoot?.Clone(); structure.SetRoot(current.Root); LibraryBodyChanged(); }
    void RefreshXml() { xml.Text = current == null ? "" : LibraryCodec.WriteXml([current]); }
    void Seed()
    {
        items.Clear(); trace.Rows.Clear(); transactions.Nodes.Clear(); selectedTransaction = null;
        var time = DateTime.Now.AddSeconds(-45);
        Template T(byte s, byte f) => templates.Single(x => x.Stream == s && x.Function == f);
        AddMessage(new("P", "TX", 0x1235, time.AddMilliseconds(120), ProjectStore.Copy(T(2, 41))));
        AddMessage(new("S", "RX", 0x1235, time.AddMilliseconds(159), ProjectStore.Copy(T(2, 42))));
        AddMessage(new("P", "RX", 0x1236, time.AddSeconds(10), ProjectStore.Copy(T(1, 1))));
        AddMessage(new("S", "TX", 0x1236, time.AddSeconds(10).AddMilliseconds(34), ProjectStore.Copy(T(1, 2))));
        AddMessage(new("P", "RX", 0x1237, time.AddSeconds(20), ProjectStore.Copy(T(7, 19))));
        monitor.Text = "MOCK DATA ONLY — 无 Socket / HSMS / SECS 通信\r\n\r\n#1001: EQP TX Primary → EAP RX Secondary (39ms)\r\n#1002: EQP RX Primary → EAP TX Secondary (34ms)\r\n#1003: RX Primary waiting for mock TX Secondary\r\n\r\nP/S 表示消息类型，TX/RX 表示相对模拟 EQP 的方向，二者独立。\r\n右侧数据均为固定示例或 Mock 按钮产生，不代表实际通信。";
        if (transactions.Nodes.Count > 0) transactions.Nodes[0].Expand();
    }
    public void AddMessage(MonitorMessage message)
    {
        if (!mockMode) { AddLiveMessage(message); return; }
        var transaction = items.SingleOrDefault(x => x.SystemBytes == message.SystemBytes);
        if (transaction == null) { transaction = new(1001 + items.Count, message.SystemBytes); items.Add(transaction); }
        if (transaction.Messages.Any(x => x.Type == message.Type)) throw new InvalidOperationException("同一 Mock 事务的 P 或 S 不可重复。");
        transaction.Messages.Add(message);
        int i = trace.Rows.Add(message.Time.ToString("HH:mm:ss.fff"), message.Direction, message.Type, message.SF, message.Template.W ? "W" : "", message.SystemBytes.ToString("X8"), "[MOCK] " + message.Template.Name); trace.Rows[i].Tag = message;
        RefreshTransaction(transaction);
        counts.Text = $"TX: {items.SelectMany(x => x.Messages).Count(x => x.Direction == "TX")}   RX: {items.SelectMany(x => x.Messages).Count(x => x.Direction == "RX")}";
    }
    void RefreshTransaction(MonitorTransaction t)
    {
        var root = transactions.Nodes.Cast<TreeNode>().FirstOrDefault(x => ReferenceEquals(x.Tag, t)); bool expanded = root?.IsExpanded == true;
        var expandedPaths = new HashSet<string>();
        string? selectionPath = null;
        void Save(TreeNode node, string path)
        {
            if (node.IsExpanded) expandedPaths.Add(path);
            if (ReferenceEquals(node, transactions.SelectedNode)) selectionPath = path;
            for (int i = 0; i < node.Nodes.Count; i++) Save(node.Nodes[i], path + "/" + i);
        }
        if (root != null) Save(root, "root");
        transactions.BeginUpdate();
        if (root == null) { root = new TreeNode { Tag = t }; transactions.Nodes.Add(root); }
        root.Text = (t.Primary?.FlowLabel.Length > 0 ? t.Primary.FlowLabel + " | " : "") + $"#{t.Number}   {t.Status,-9}   SystemBytes {t.SystemBytes:X8}   Duration: {t.ResponseTime}" + (mockMode ? "  [MOCK]" : "");
        root.ForeColor = t.Status == "SUCCESS" || t.Status == "SENT" ? Color.DarkGreen : t.Status.StartsWith("ERROR") ? Color.Firebrick : Color.DarkOrange;
        root.Nodes.Clear(); root.Nodes.Add(new TreeNode($"Summary   SystemBytes: {t.SystemBytes:X8}   ResponseTime: {t.ResponseTime}") { Tag = t });
        foreach (var m in t.Messages.OrderBy(x => x.Type))
        {
            var node = new TreeNode($"{m.Type} / {m.Direction} / {m.SF} / {(m.Template.W ? "W" : "W=0")}   {m.Template.Name}   {m.Time:HH:mm:ss.fff}") { Tag = m };
            TreeNode Build(Node n) { var a = new TreeNode(n.ToString()) { Tag = m }; foreach (var c in n.Children) a.Nodes.Add(Build(c)); return a; }
            if (m.Template.Root != null) node.Nodes.Add(Build(m.Template.Root)); else node.Nodes.Add(new TreeNode("(No SECS-II body)") { Tag = m });
            root.Nodes.Add(node);
        }
        if (t.Secondary == null && t.Primary?.Template.W == true) root.Nodes.Add(new TreeNode("S / — / " + t.Status) { Tag = t });
        void Restore(TreeNode node, string path)
        {
            if (expandedPaths.Contains(path)) node.Expand();
            if (path == selectionPath) transactions.SelectedNode = node;
            for (int i = 0; i < node.Nodes.Count; i++) Restore(node.Nodes[i], path + "/" + i);
        }
        Restore(root, "root");
        if (expanded) root.Expand();
        transactions.EndUpdate();
    }
    void SelectTraceMessage(MonitorMessage message)
    {
        var transaction = items.FirstOrDefault(t => t.Messages.Contains(message));
        var root = transactions.Nodes.Cast<TreeNode>().FirstOrDefault(n => ReferenceEquals(n.Tag, transaction));
        if (root != null)
        {
            root.Expand();
            transactions.SelectedNode = root.Nodes.Cast<TreeNode>().FirstOrDefault(n => ReferenceEquals(n.Tag, message)) ?? root;
        }
        selectedTransaction = transaction; ShowDetail(message); UpdateButtons();
    }
    void ShowDetail(MonitorMessage m)
    {
        detailFields["Type"].Text = m.Type == "P" ? "Primary (P)" : "Secondary (S)";
        detailFields["Direction"].Text = m.Direction; detailFields["SxFy"].Text = m.SF;
        detailFields["W-Bit"].Text = m.Template.W.ToString(); detailFields["System Bytes"].Text = m.SystemBytes.ToString("X8"); detailFields["Time"].Text = m.Time.ToString("HH:mm:ss.fff"); detailFields["Name"].Text = m.Template.Name;
        parsed.Nodes.Clear(); TreeNode Build(Node n) { var t = new TreeNode(n.ToString()); foreach (var c in n.Children) t.Nodes.Add(Build(c)); return t; }
        if (m.Template.Root != null) parsed.Nodes.Add(Build(m.Template.Root)); parsed.ExpandAll(); raw.Text = m.Raw;
    }
    void UpdateButtons() { sendPrimary.Enabled = (mockMode || transport?.CanSend == true) && current?.Function % 2 == 1; sendSecondary.Enabled = (mockMode || transport?.CanSend == true && selectedTransaction?.Session == transport.Generation && transport.Pending.ContainsKey(unchecked((int)selectedTransaction.SystemBytes))) && current is { } selected && selected.Function % 2 == 0 && selectedTransaction is { Secondary: null, Primary: { Direction: "RX", Template.W: true } primary } && selected.Stream == primary.Template.Stream && selected.Function == primary.Template.Function + 1; }
    void AddPrimary()
    {
        if (current == null || current.Function % 2 == 0) throw new InvalidOperationException("请选择 Primary 模板。");
        if (current.Root != null) { using var item = Sml.Build(current.Root); }
        AddMessage(new("P", "TX", nextBytes++, DateTime.Now, ProjectStore.Copy(current)));
        transactions.SelectedNode = transactions.Nodes[^1]; transactions.Nodes[^1].Expand();
    }
    void AddSecondary()
    {
        UpdateButtons(); if (!sendSecondary.Enabled || selectedTransaction?.Primary == null || current == null) throw new InvalidOperationException("先选择一个等待中的 RX Primary 事务，再选择匹配的 Secondary 模板。");
        if (current.Root != null) { using var item = Sml.Build(current.Root); }
        AddMessage(new("S", "TX", selectedTransaction.SystemBytes, DateTime.Now, ProjectStore.Copy(current))); UpdateButtons();
    }
    void ExportSml() { ExportLibraryDialog(false, true); }

    public async Task RunSmoke(string directory)
    {
        Directory.CreateDirectory(directory); var report = new List<string>();
        void Check(bool ok, string text) { if (!ok) throw new Exception(text); report.Add("PASS " + text); }
        try
        {
            Visible = false; Visible = true; TopMost = true; Activate(); await Task.Delay(200);
            Check(library.Nodes.Cast<TreeNode>().SelectMany(x => x.Nodes.Cast<TreeNode>()).All(x => x.Tag is Template t && t.Function % 2 == 1 && x.Nodes.Count > 0), "Library Primary +/- and nested Secondary");
            SelectLibraryNode(templates.Single(x => x.Stream == 2 && x.Function == 42)); Check(editFields["Type"].Text == "Secondary", "Secondary selection updates editor");
            SelectLibraryNode(templates.Single(x => x.Stream == 2 && x.Function == 41)); structure.Tree.SelectedNode = structure.Tree.Nodes[0]; var before = current!.Root!.Children.Count; structure.AddNode(new() { Type = "U1", Value = "7" }); Check(current.Root.Children.Count == before + 1, "Add Node changes selected template"); structure.DeleteNode(); Check(current.Root.Children.Count == before, "Delete Node preserves hierarchy");
            structure.Tree.SelectedNode = structure.Tree.Nodes[0].Nodes[0];
            using (var editTimer = new System.Windows.Forms.Timer { Interval = 100 })
            {
                editTimer.Tick += (_, _) => { var dialog = Application.OpenForms.Cast<Form>().FirstOrDefault(x => x.Text.StartsWith("Edit Node")); if (dialog == null) return; dialog.Controls.OfType<TextBox>().Single(x => x.Multiline).Text = "STOP"; editTimer.Stop(); dialog.Controls.OfType<FlowLayoutPanel>().Single().Controls.OfType<Button>().Single(x => x.Text == "Apply").PerformClick(); };
                editTimer.Start(); structure.EditSelected(this);
            }
            Check(current.Root.Children[0].Value == "STOP", "Edit Node dialog applies typed value"); ResetEditor();
            var inbound = items[1].Primary!; ShowDetail(inbound); Check(detailFields["Type"].Text == "Primary (P)" && detailFields["Direction"].Text == "RX", "P/S independent of TX/RX"); ShowDetail(items[1].Secondary!); Check(detailFields["Direction"].Text == "TX" && detailFields["Type"].Text == "Secondary (S)", "TX Secondary detail");
            Check(detailFields["Name"].Bounds.Bottom <= detailFields["Name"].Parent!.ClientSize.Height && detailFields["Name"].Text.Length > 0, "Name metadata stays visible in detail panel");
            Check(items[0].Messages.All(x => x.SystemBytes == 0x1235) && items[0].ResponseTime == "39 ms", "System Bytes pairing and response time");
            selectedTransaction = items[2]; SelectTemplate(templates.Single(x => x.Stream == 7 && x.Function == 20)); AddSecondary(); Check(items[2].Status == "SUCCESS", "Mock Secondary completes selected waiting transaction");
            SelectTemplate(templates.Single(x => x.Stream == 2 && x.Function == 41)); AddPrimary(); Check(items.Count == 4 && items[^1].Status == "WAITING", "Mock Primary adds waiting transaction without network");
            Check(trace.Columns.Count == 7 && trace.Rows.Count == 7, "Trace uses independent seven columns");
            Seed(); SelectLibraryNode(templates.Single(x => x.Stream == 2 && x.Function == 41)); transactions.Nodes[0].Expand(); transactions.SelectedNode = transactions.Nodes[0].Nodes[1]; transactions.Nodes[0].Nodes[1].ExpandAll();
            await Task.Delay(200); using (var bitmap = new Bitmap(Width, Height)) { using var g = Graphics.FromImage(bitmap); g.CopyFromScreen(Location, Point.Empty, Size); bitmap.Save(Path.Combine(directory, "mock-workspace.png")); }
            Size = new(1120, 820); await Task.Delay(100); Check(structure.Width > 300 && transactions.Width > 300 && trace.Height > 60, "Resizable split panes retain usable minimum layout");
            using (var bitmap = new Bitmap(Width, Height)) { DrawToBitmap(bitmap, new(Point.Empty, Size)); bitmap.Save(Path.Combine(directory, "mock-small.png")); }
            report.Add($"TOTAL {report.Count} PASS");
        }
        catch (Exception e) { report.Add("FAIL " + e); Environment.ExitCode = 1; }
        finally { File.WriteAllLines(Path.Combine(directory, "mock-ui-results.txt"), report); Close(); }
    }
}







