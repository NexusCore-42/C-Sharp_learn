using System.Diagnostics;

namespace FastSim;
public sealed partial class MainForm : Form
{
    Project project = Project.Demo();
    readonly object sync = new();
    readonly SimLog log;
    readonly Communication communication;
    readonly FlowRunner flow;
    readonly DataGridView library = Grid();
    readonly DataGridView traffic = Grid();
    readonly TreeView tree = new() { Dock = DockStyle.Fill, HideSelection = false };
    readonly TextBox editor = new() { Dock = DockStyle.Fill, Multiline = true, ScrollBars = ScrollBars.Both, AcceptsTab = true, WordWrap = false, Font = new("Consolas", 11) };
    readonly TextBox trace = new() { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, Font = new("Consolas", 9) };
    readonly TextBox search = new() { Width = 200, PlaceholderText = "搜索名称 / SxFy / 说明" };
    readonly TextBox filter = new() { Width = 170, PlaceholderText = "筛选 S/F、事务、名称" };
    readonly ComboBox pending = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 300 };
    readonly ComboBox job = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 150 };
    readonly Label status = new() { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Padding = new(8, 0, 8, 0), AutoEllipsis = true, BackColor = Color.FromArgb(235, 243, 250), ForeColor = Color.FromArgb(25, 45, 65) };
    readonly ToolStripButton port = new("端口 5000 · 打开");
    readonly Button send;
    readonly Button one;
    readonly Button stop;
    readonly List<TrafficRecord> history = [];
    readonly List<ToolStripItem> configItems = [];
    readonly List<ToolStripItem> openItems = [];
    readonly List<ToolStripItem> closeItems = [];
    readonly System.Windows.Forms.Timer timer = new() { Interval = 120 };
    string? path;
    Template? selected;
    bool loading;
    bool dirty;
    bool editorDirty;
    bool closing;
    bool manual;
    bool busyConnection;
    bool pauseScroll;
    public int OneClickOpenCount { get; private set; }
    static DataGridView Grid() => new() { Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false, AutoGenerateColumns = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect, MultiSelect = false, RowHeadersVisible = false, BackgroundColor = Color.White, BorderStyle = BorderStyle.None, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill };
    public MainForm()
    {
        Text = "FastSim · 半导体机台模拟客户端"; Size = new(1460, 940); MinimumSize = new(1050, 720); StartPosition = FormStartPosition.CenterScreen; Font = new("Microsoft YaHei UI", 10); AutoScaleMode = AutoScaleMode.Dpi;
        log = new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FastSim", "logs", $"{DateTime.Now:yyyyMMdd-HHmmss}-{Environment.ProcessId}"));
        communication = new(log); flow = new(communication, log);
        var responder = new AutoResponder(communication, log, () => project, sync); communication.PrimaryReceived = async r => { await responder.Handle(r); Ui(() => { dirty = true; RefreshJobs(); }); };
        communication.Changed += () => Ui(UpdateState); flow.Changed += () => Ui(UpdateState);
        send = Dialogs.Button("发送", () => SafeAsync(SendCurrent)); one = Dialogs.Button("一键发送", () => SafeAsync(ConfigureFlow)); stop = Dialogs.Button("停止", () => flow.Stop());
        BuildLayout();
        editor.TextChanged += (_, _) => { if (!loading) { editorDirty = true; dirty = true; } };
        library.SelectionChanged += (_, _) => SelectLibrary();
        search.TextChanged += (_, _) => { if (CommitEditor()) RefreshLibrary(); };
        filter.TextChanged += (_, _) => RefreshTraffic();
        traffic.CellDoubleClick += (_, _) => { if (traffic.CurrentRow?.Tag is TrafficRecord r) ShowTraffic(r); };
        tree.NodeMouseDoubleClick += (_, _) => EditTree("edit");
        timer.Tick += (_, _) => Drain(); timer.Start();
        FormClosing += OnClosing;
        RefreshLibrary(); RefreshJobs(); UpdateState();
    }
    void Ui(Action a) { if (IsDisposed || !IsHandleCreated) return; try { BeginInvoke(a); } catch (InvalidOperationException) { } }
    void Safe(Action action) { try { action(); } catch (Exception e) { log.Info(e.ToString()); MessageBox.Show(this, e.Message, "操作失败", MessageBoxButtons.OK, MessageBoxIcon.Error); } }
    async void SafeAsync(Func<Task> action) { try { await action(); } catch (Exception e) { log.Info(e.ToString()); MessageBox.Show(this, e.Message, "操作失败", MessageBoxButtons.OK, MessageBoxIcon.Error); } finally { UpdateState(); } }
    void BuildLayout()
    {
        var menu = new MenuStrip(); MainMenuStrip = menu;
        ToolStripMenuItem Top(string text) { var t = new ToolStripMenuItem(text); menu.Items.Add(t); return t; }
        ToolStripMenuItem Add(ToolStripMenuItem parent, string text, Action action, Keys shortcut = Keys.None) { var item = new ToolStripMenuItem(text) { ShortcutKeys = shortcut }; item.Click += (_, _) => Safe(action); parent.DropDownItems.Add(item); return item; }
        var file = Top("文件"); Add(file, "新建", () => Replace(Project.Demo(), null), Keys.Control | Keys.N); Add(file, "打开", Open, Keys.Control | Keys.O); Add(file, "保存", () => Save(false), Keys.Control | Keys.S); Add(file, "另存为", () => Save(true)); Add(file, "导入 XML / SML", Import); Add(file, "导出 XML / SML", Export); Add(file, "退出", Close);
        var ports = Top("端口"); configItems.Add(Add(ports, "连接配置", ConfigureConnection)); openItems.Add(Add(ports, "打开端口", () => SafeAsync(OpenPort))); closeItems.Add(Add(ports, "关闭端口", () => SafeAsync(ClosePort))); Add(ports, "查看连接状态", () => MessageBox.Show(this, status.Text));
        var lib = Top("库"); Add(lib, "新增命令", NewTemplate); Add(lib, "复制命令", CopyTemplate); Add(lib, "删除命令", DeleteTemplate); Add(lib, "重命名命令", RenameTemplate); Add(lib, "编辑命令", () => editor.Focus()); Add(lib, "搜索命令", () => search.Focus(), Keys.Control | Keys.F);
        var info = Top("信息"); Add(info, "查看 Traffic", () => traffic.Focus()); Add(info, "查看 Trace", () => trace.Focus()); Add(info, "导出日志", ExportLogs); Add(info, "查看当前流程执行详情", () => { lock (flow.Results) Dialogs.Text(this, flow.Progress, string.Join(Environment.NewLine, flow.Results)); });
        var settings = Top("设置"); Add(settings, "机台 Offline / Local / Remote", ControlState); Add(settings, "变量管理 / PJ / CJ", VariablesJobs); Add(settings, "自动回复规则", Rules); Add(settings, "一键发送配置", () => SafeAsync(ConfigureFlow)); Add(settings, "日志设置", LogSettings);
        var help = Top("帮助"); Add(help, "使用说明", Manual); Add(help, "示例项目", () => Replace(Project.Demo(), null)); Add(help, "快捷键", () => MessageBox.Show(this, "Ctrl+N 新建\nCtrl+O 打开\nCtrl+S 保存\nCtrl+F 搜索\nF5 单条发送\nEsc 停止流程\n双击 Traffic 查看完整报文；双击树节点编辑。")); Add(help, "关于与依赖版本", () => MessageBox.Show(this, "FastSim 1.0\nC# WinForms / .NET 10 LTS / Secs4Net 3.1.0\n报文级机台模拟，非完整 GEM / E40 / E94。\nWinSECS XML 精确兼容待实际样本验证。"));
        var tools = new ToolStrip { GripStyle = ToolStripGripStyle.Hidden }; tools.Items.Add("新建", null, (_, _) => Safe(() => Replace(Project.Demo(), null))); tools.Items.Add("打开", null, (_, _) => Safe(Open)); tools.Items.Add("保存", null, (_, _) => Safe(() => Save(false))); tools.Items.Add(new ToolStripSeparator()); tools.Items.Add(port); port.Click += (_, _) => SafeAsync(() => communication.IsOpen ? ClosePort() : OpenPort()); var cfg = tools.Items.Add("连接配置", null, (_, _) => Safe(ConfigureConnection)); configItems.Add(cfg); tools.Items.Add("变量 / 作业", null, (_, _) => Safe(VariablesJobs)); tools.Items.Add("Offline / Local / Remote", null, (_, _) => Safe(ControlState));
        var statusBar = new Panel { Dock = DockStyle.Bottom, Height = 30 }; statusBar.Controls.Add(status);
        var vertical = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, Size = new(1400, 800), SplitterDistance = 610, Panel1MinSize = 350, Panel2MinSize = 110 };
        var horizontal = new SplitContainer { Dock = DockStyle.Fill, Size = new(1400, 610), SplitterDistance = 630, Panel1MinSize = 320, Panel2MinSize = 420 };
        vertical.Panel1.Controls.Add(horizontal); vertical.Panel2.Controls.Add(Group("Trace · 运行日志", trace));
        foreach (var (name, title) in new[] { ("Time", "时间"), ("Direction", "方向"), ("SF", "S/F"), ("W", "W"), ("SystemBytes", "System Bytes"), ("Name", "名称"), ("Result", "结果") }) traffic.Columns.Add(name, title);
        var trafficPanel = new Panel { Dock = DockStyle.Fill }; trafficPanel.Controls.Add(traffic); var trafficBar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 40 }; trafficBar.Controls.Add(filter); trafficBar.Controls.Add(Dialogs.Button("暂停滚动", () => { pauseScroll = !pauseScroll; })); trafficBar.Controls.Add(Dialogs.Button("清空显示", () => { history.Clear(); RefreshTraffic(); })); trafficPanel.Controls.Add(trafficBar); horizontal.Panel1.Controls.Add(Group("Traffic · 实际收发 / 事务结果（双击详情）", trafficPanel));
        var right = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, Size = new(740, 610), SplitterDistance = 215, Panel1MinSize = 130, Panel2MinSize = 240 };
        foreach (var name in new[] { "名称", "Stream", "Function", "W-bit", "说明" }) library.Columns.Add(name, name);
        library.Columns[0].FillWeight = 120; library.Columns[1].FillWeight = 50; library.Columns[2].FillWeight = 55; library.Columns[3].FillWeight = 40; library.Columns[4].FillWeight = 160;
        traffic.Columns[4].FillWeight = 140; traffic.Columns[3].FillWeight = 35; traffic.Columns[1].FillWeight = 55;
        var libPanel = new Panel { Dock = DockStyle.Fill }; libPanel.Controls.Add(library); var libbar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 40 }; libbar.Controls.Add(search); libbar.Controls.Add(Dialogs.Button("新增", () => Safe(NewTemplate))); libbar.Controls.Add(Dialogs.Button("复制", () => Safe(CopyTemplate))); libPanel.Controls.Add(libbar); right.Panel1.Controls.Add(Group("WinSECS Library · 消息库", libPanel));
        var editSplit = new SplitContainer { Dock = DockStyle.Fill, Size = new(740, 390), SplitterDistance = 235, Panel1MinSize = 150, Panel2MinSize = 210 }; editSplit.Panel1.Controls.Add(tree); editSplit.Panel2.Controls.Add(editor);
        var nodeMenu = new ContextMenuStrip(); foreach (var (text, mode) in new[] { ("添加子项", "add"), ("编辑类型 / 值", "edit"), ("删除节点", "delete"), ("上移", "up"), ("下移", "down") }) nodeMenu.Items.Add(text, null, (_, _) => Safe(() => EditTree(mode))); tree.ContextMenuStrip = nodeMenu;
        var editPanel = new Panel { Dock = DockStyle.Fill }; editPanel.Controls.Add(editSplit); var editBar = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 42 }; editBar.Controls.Add(Dialogs.Button("保存修改 / 同步树", () => Safe(() => CommitEditor()))); editBar.Controls.Add(Dialogs.Button("预览变量报文", () => Safe(Preview))); editPanel.Controls.Add(editBar); right.Panel2.Controls.Add(editPanel);
        var rightPanel = new Panel { Dock = DockStyle.Fill }; rightPanel.Controls.Add(right); var sendbar = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 91, Padding = new(4), AutoScroll = true }; sendbar.Controls.Add(send); sendbar.Controls.Add(one); sendbar.Controls.Add(stop); sendbar.Controls.Add(new Label { Text = "作业：", AutoSize = true, Padding = new(0, 8, 0, 0) }); sendbar.Controls.Add(job); sendbar.SetFlowBreak(job, true); sendbar.Controls.Add(new Label { Text = "偶数应答选择原请求：", AutoSize = true, Padding = new(0, 8, 0, 0) }); sendbar.Controls.Add(pending); rightPanel.Controls.Add(sendbar); horizontal.Panel2.Controls.Add(rightPanel);
        Controls.Add(vertical); Controls.Add(statusBar); Controls.Add(tools); Controls.Add(menu);
    }
    static GroupBox Group(string title, Control control) { var g = new GroupBox { Text = title, Dock = DockStyle.Fill, Padding = new(7) }; g.Controls.Add(control); return g; }
    public bool CommitEditor()
    {
        if (!editorDirty || selected == null) return true;
        try
        {
            var parsed = Sml.Parse(editor.Text); if (parsed.Count != 1) throw new FormatException("编辑区只允许一条消息");
            var t = parsed[0]; t.Id = selected.Id; t.Description = selected.Description;
            int index = project.Library.FindIndex(x => x.Id == selected.Id); if (index >= 0) project.Library[index] = t; selected = t; editorDirty = false; dirty = true;
            RefreshTree(); UpdateLibraryRow(); return true;
        }
        catch (Exception e) { log.Info(e.Message); MessageBox.Show(this, e.Message + "\n原输入已保留，请修正后继续。", "SML 解析失败"); return false; }
    }
    void UpdateLibraryRow() { if (selected == null) return; foreach (DataGridViewRow r in library.Rows) if ((r.Tag as Template)?.Id == selected.Id) { r.Tag = selected; r.SetValues(selected.Name, selected.Stream, selected.Function, selected.W, selected.Description); } }
    void RefreshLibrary(string? selectId = null)
    {
        loading = true; selectId ??= selected?.Id; library.Rows.Clear();
        foreach (var t in project.Library.Where(x => ($"{x.Name} S{x.Stream}F{x.Function} {x.Description}").Contains(search.Text, StringComparison.OrdinalIgnoreCase)).OrderBy(x => x.Stream).ThenBy(x => x.Function))
        { int i = library.Rows.Add(t.Name, t.Stream, t.Function, t.W, t.Description); library.Rows[i].Tag = t; if (t.Id == selectId) library.CurrentCell = library.Rows[i].Cells[0]; }
        loading = false; SelectLibrary();
    }
    void SelectLibrary()
    {
        if (loading) return; var next = library.CurrentRow?.Tag as Template; if (next?.Id == selected?.Id) return;
        if (!CommitEditor()) { loading = true; foreach (DataGridViewRow r in library.Rows) if ((r.Tag as Template)?.Id == selected?.Id) library.CurrentCell = r.Cells[0]; loading = false; return; }
        selected = next; SetEditor();
    }
    void SetEditor() { loading = true; editor.Text = selected == null ? "" : Sml.Write(selected); editorDirty = false; loading = false; RefreshTree(); }
    void RefreshTree() { tree.BeginUpdate(); tree.Nodes.Clear(); TreeNode Build(Node n) { var t = new TreeNode(n.ToString()) { Tag = n }; foreach (var c in n.Children) t.Nodes.Add(Build(c)); return t; } if (selected?.Root != null) tree.Nodes.Add(Build(selected.Root)); tree.ExpandAll(); tree.EndUpdate(); }
    void EditTree(string mode)
    {
        if (!CommitEditor() || selected == null) return;
        var t = tree.SelectedNode; var n = t?.Tag as Node;
        if (mode == "add") { var child = Dialogs.EditNode(this, new Node { Type = "A" }); if (child == null) return; if (selected.Root == null) selected.Root = child; else if (n?.Type == "L") n.Children.Add(child); else throw new FormatException("请选择 List 节点"); }
        else if (n != null)
        {
            var parent = t!.Parent?.Tag as Node; int index = parent?.Children.IndexOf(n) ?? -1;
            if (mode == "edit") { var changed = Dialogs.EditNode(this, n); if (changed == null) return; if (parent == null) selected.Root = changed; else parent.Children[index] = changed; }
            if (mode == "delete") { if (parent == null) selected.Root = null; else parent.Children.RemoveAt(index); }
            if (mode is "up" or "down" && parent != null) { int target = index + (mode == "up" ? -1 : 1); if (target >= 0 && target < parent.Children.Count) { parent.Children.RemoveAt(index); parent.Children.Insert(target, n); } }
        }
        dirty = true; SetEditor();
    }
    void NewTemplate() { if (!CommitEditor()) return; var t = new Template(); project.Library.Add(t); dirty = true; RefreshLibrary(t.Id); }
    void CopyTemplate() { if (!CommitEditor() || selected == null) return; var t = ProjectStore.Copy(selected); t.Id = Guid.NewGuid().ToString("N"); t.Name += " 副本"; project.Library.Add(t); dirty = true; RefreshLibrary(t.Id); }
    void RenameTemplate() { if (!CommitEditor() || selected == null) return; var name = Dialogs.Text(this, "命令名称", selected.Name); if (name == null) return; selected.Name = name; dirty = true; UpdateLibraryRow(); SetEditor(); }
    void DeleteTemplate() { if (!CommitEditor() || selected == null) return; if (project.Steps.Any(x => x.TemplateId == selected.Id) || project.Rules.Any(x => x.ReplyId == selected.Id || x.RejectedReplyId == selected.Id)) throw new InvalidOperationException("先移除引用此模板的流程步骤或自动回复规则"); if (MessageBox.Show(this, "删除所选命令？", "确认", MessageBoxButtons.YesNo) != DialogResult.Yes) return; project.Library.Remove(selected); selected = null; dirty = true; RefreshLibrary(); }
    Dictionary<string, Variable> Scope() { lock (sync) return Variables.Scope(project, jobId: job.SelectedIndex > 0 ? job.Text : ""); }
    void Preview() { if (!CommitEditor() || selected == null) return; using var msg = Variables.Message(selected, Scope()); Dialogs.Text(this, "变量解析后的最终报文（仅预览）", Sml.Write(Sml.FromMessage(msg))); }
    async Task SendCurrent()
    {
        if (flow.Running || manual || !CommitEditor() || selected == null) return;
        var snapshot = ProjectStore.Copy(selected); var scope = Scope(); manual = true; UpdateState();
        try { if (snapshot.Function % 2 == 0) { if (pending.SelectedItem is not PendingRequest p) throw new FormatException("请先选择对应的待回复请求"); await communication.ReplyAsync(p, snapshot, scope); } else await communication.SendAsync(snapshot, scope, project.Connection.T3, CancellationToken.None); }
        finally { manual = false; UpdateState(); }
    }
    async Task ConfigureFlow()
    {
        if (flow.Running || manual || !CommitEditor()) return;
        OneClickOpenCount++; var steps = Dialogs.Flow(this, project, flow, communication.CanSend, out var start); if (steps == null) return;
        project.Steps = steps; dirty = true;
        if (start) await flow.ExecuteAsync(project);
    }
    void ConfigureConnection() { if (communication.IsOpen) throw new InvalidOperationException("关闭连接后才能修改参数"); var cfg = Dialogs.Connection(this, project.Connection); if (cfg != null) { project.Connection = cfg; dirty = true; UpdateState(); } }
    async Task OpenPort() { if (busyConnection) return; busyConnection = true; UpdateState(); try { await communication.OpenAsync(project.Connection); } finally { busyConnection = false; UpdateState(); } }
    async Task ClosePort() { if (busyConnection) return; busyConnection = true; UpdateState(); try { flow.Stop(); await communication.CloseAsync(); } finally { busyConnection = false; UpdateState(); } }
    void ControlState()
    {
        using var f = Dialogs.Window("机台控制状态（与 HSMS Active / Passive 独立）", 680, 420); var panel = new FlowLayoutPanel { Dock = DockStyle.Fill, Padding = new(25) };
        panel.Controls.Add(new Label { Text = "规则 AllowedControl 决定接受状态；拒绝时使用 RejectedReplyId。\n状态变化本身不发送未配置的 GEM 事件。", AutoSize = true });
        foreach (var state in new[] { "Offline", "Local", "Remote" }) panel.Controls.Add(Dialogs.Button(state, () => { lock (sync) project.Control = state; dirty = true; log.Info("机台状态：" + state); UpdateState(); f.Close(); })); f.Controls.Add(panel); f.ShowDialog(this);
    }
    void VariablesJobs()
    {
        if (!CommitEditor()) return;
        using var f = Dialogs.Window("变量与 ProcessJob / ControlJob 管理", 820, 520); var panel = new FlowLayoutPanel { Dock = DockStyle.Fill, Padding = new(20), FlowDirection = FlowDirection.TopDown };
        panel.Controls.Add(new Label { AutoSize = true, Text = "全局变量 < 流程变量 < 选定作业 < 本次参数。\nEAP 提取值仅属于原请求；保存作业后按作业 ID 使用，避免串数据。\n列表支持新增、修改、删除；高级 JSON 可编辑扩展属性。保存前执行类型与作业规则校验。" });
        panel.Controls.Add(Dialogs.Button("全局变量列表", () => Safe(() => { var next = Dialogs.Records(this, "全局变量", project.Variables, "每行记录包含 Name、Type、Value；支持 A、U1、U4 等类型。数值数组 Value 用空格分隔。", x => { var p = ProjectStore.Copy(project); p.Variables = x; ProjectStore.Validate(p); }); if (next != null) { lock (sync) project.Variables = next; dirty = true; } })));
        panel.Controls.Add(Dialogs.Button("PJ / CJ 作业列表", () => Safe(() => { var next = Dialogs.Records(this, "PJ / CJ 作业列表", project.Jobs, "Kind=PJ/CJ；Id 唯一；RecipeID、CarrierID、Slots（空格分隔）；CJ 的 ProcessJobs 用英文逗号列 PJ ID。Properties 保存扩展变量。", x => { var p = ProjectStore.Copy(project); p.Jobs = x; JobManager.Validate(p, project.Jobs); }); if (next != null) { lock (sync) { var p = ProjectStore.Copy(project); p.Jobs = next; JobManager.Validate(p, project.Jobs); project.Jobs = next; } dirty = true; RefreshJobs(); } })));
        panel.Controls.Add(Dialogs.Button("作业状态 / 属性规则", () => Safe(() => { var next = Dialogs.JsonEdit(this, "作业规则", project.JobPolicy, "States 用逗号分隔；Transitions 使用 Created>Running 等转换；AllowRecipeChangeWhileRunning 控制运行中修改 Recipe。", x => { var p = ProjectStore.Copy(project); p.JobPolicy = x; JobManager.Validate(p, []); }); if (next != null) { project.JobPolicy = next; dirty = true; } })));
        panel.Controls.Add(Dialogs.Button("关闭", f.Close)); f.Controls.Add(panel); f.ShowDialog(this);
    }
    void Rules()
    {
        if (!CommitEditor()) return;
        var next = Dialogs.JsonEdit(this, "自动回复规则", project.Rules, "按 Stream、Function、W 和可选 ConditionPath/ConditionValue 匹配。ReplyId / RejectedReplyId 使用下方模板 ID。Extract=[{Name,Path}]，路径从0计数。SaveJobs=true 保存 PJ/CJ。未命中不回复。\r\n" + string.Join("；", project.Library.Select(x => x.Name + "=" + x.Id)), x => { var p = ProjectStore.Copy(project); p.Rules = x; ProjectStore.Validate(p); });
        if (next != null) { lock (sync) project.Rules = next; dirty = true; }
    }
    void LogSettings() { var next = Dialogs.JsonEdit(this, "日志设置", project.Logs, "UiLimit=界面上限；FileMegabytes=单文件大小；FileCount=保留文件数。日志目录：" + log.DirectoryPath, x => { var p = ProjectStore.Copy(project); p.Logs = x; ProjectStore.Validate(p); }); if (next != null) { project.Logs = next; log.Config = next; dirty = true; } }
    void RefreshJobs() { var current = job.Text; job.Items.Clear(); job.Items.Add("全局 / 无作业"); lock (sync) foreach (var j in project.Jobs) job.Items.Add(j.Id); job.SelectedIndex = Math.Max(0, job.Items.IndexOf(current)); }
    void UpdateState()
    {
        port.Text = $"端口 {project.Connection.Port} · {(communication.IsOpen ? "关闭" : "打开")}"; port.Enabled = !busyConnection;
        send.Enabled = communication.CanSend && !flow.Running && !manual && !busyConnection; one.Enabled = !flow.Running && !manual; stop.Enabled = flow.Running;
        foreach (var x in configItems) x.Enabled = !communication.IsOpen && !busyConnection;
        foreach (var x in openItems) x.Enabled = !communication.IsOpen && !busyConnection;
        foreach (var x in closeItems) x.Enabled = communication.IsOpen && !busyConnection;
        status.Text = $"{project.Connection.IP}:{project.Connection.Port} | {(project.Connection.Active ? "Active" : "Passive")} | {communication.State} | {(project.Control == "Offline" ? "Offline" : "Online / " + project.Control)} | {flow.Progress}";
        var previous = (pending.SelectedItem as PendingRequest)?.Id; pending.Items.Clear(); foreach (var p in communication.Pending.Values.OrderBy(x => x.Expires)) { pending.Items.Add(p); if (p.Id == previous) pending.SelectedItem = p; }
    }
    void Drain()
    {
        communication.ExpirePending(); bool added = false;
        for (int i = 0; i < 250 && log.Traffic.TryDequeue(out var record); i++) { history.Add(record); added = true; }
        if (history.Count > project.Logs.UiLimit) history.RemoveRange(0, history.Count - project.Logs.UiLimit);
        if (added) AppendTraffic();
        var lines = new List<string>(); for (int i = 0; i < 250 && log.Trace.TryDequeue(out var line); i++) lines.Add(line);
        if (lines.Count > 0) { trace.AppendText(string.Join(Environment.NewLine, lines) + Environment.NewLine); if (trace.Lines.Length > project.Logs.UiLimit) trace.Lines = trace.Lines.TakeLast(project.Logs.UiLimit).ToArray(); }
        if (communication.Pending.Values.Any(p => p.Expires <= DateTime.UtcNow) || pending.Items.Count != communication.Pending.Count) UpdateState();
    }
    void RefreshTraffic()
    {
        traffic.SuspendLayout(); traffic.Rows.Clear(); foreach (var r in history.Where(x => ($"{x.SF} {x.SystemBytes} {x.Name} {x.Direction} {x.Result}").Contains(filter.Text, StringComparison.OrdinalIgnoreCase))) { int i = traffic.Rows.Add(r.Time.ToString("HH:mm:ss.fff"), r.Direction, r.SF, r.W, r.SystemBytes, r.Name, r.Result); traffic.Rows[i].Tag = r; }
        if (!pauseScroll && traffic.RowCount > 0) traffic.FirstDisplayedScrollingRowIndex = traffic.RowCount - 1; traffic.ResumeLayout();
    }
    void AppendTraffic()
    {
        // Append only new records; rebuilding thousands of rows per timer tick stalls WinForms.
        var last = traffic.Rows.Count > 0 ? traffic.Rows[^1].Tag as TrafficRecord : null;
        int index = last == null ? -1 : history.IndexOf(last);
        if (last != null && index < 0) { RefreshTraffic(); return; }
        foreach (var r in history.Skip(index + 1).Where(x => ($"{x.SF} {x.SystemBytes} {x.Name} {x.Direction} {x.Result}").Contains(filter.Text, StringComparison.OrdinalIgnoreCase)))
        { int i = traffic.Rows.Add(r.Time.ToString("HH:mm:ss.fff"), r.Direction, r.SF, r.W, r.SystemBytes, r.Name, r.Result); traffic.Rows[i].Tag = r; }
        while (traffic.Rows.Count > project.Logs.UiLimit) traffic.Rows.RemoveAt(0);
        if (!pauseScroll && traffic.RowCount > 0) traffic.FirstDisplayedScrollingRowIndex = traffic.RowCount - 1;
    }
    void ShowTraffic(TrafficRecord r)
    {
        using var f = Dialogs.Window("Traffic · 事务 " + r.SystemBytes, 980, 720); var tabs = new TabControl { Dock = DockStyle.Fill }; var page = new TabPage("完整消息 / 同事务"); page.Controls.Add(new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, Font = editor.Font, Text = string.Join("\r\n", history.Where(x => x.SystemBytes == r.SystemBytes).Select(x => $"{x.Time:O} {x.Direction} {x.Result}\r\n{x.Sml}")) }); tabs.TabPages.Add(page);
        if (r.Sml.Length > 0) { var t = new TreeView { Dock = DockStyle.Fill }; TreeNode Build(Node n) { var a = new TreeNode(n.ToString()); foreach (var c in n.Children) a.Nodes.Add(Build(c)); return a; } var m = Sml.Parse(r.Sml)[0]; if (m.Root != null) t.Nodes.Add(Build(m.Root)); t.ExpandAll(); var treePage = new TabPage("结构树"); treePage.Controls.Add(t); tabs.TabPages.Add(treePage); } f.Controls.Add(tabs); f.ShowDialog(this);
    }
    bool ConfirmSave() { if (!dirty && !editorDirty) return true; var answer = MessageBox.Show(this, "项目有未保存修改，是否保存？", "未保存修改", MessageBoxButtons.YesNoCancel); return answer == DialogResult.No || answer == DialogResult.Yes && Save(false); }
    bool Save(bool asNew)
    {
        if (!CommitEditor()) return false; var destination = path;
        if (asNew || destination == null) { using var d = new SaveFileDialog { Filter = "FastSim 项目 XML|*.xml", FileName = "project.xml" }; if (d.ShowDialog(this) != DialogResult.OK) return false; destination = d.FileName; }
        lock (sync) ProjectStore.Save(destination, project); path = destination; dirty = false; log.Info("项目已保存：" + path); return true;
    }
    void Replace(Project next, string? nextPath)
    {
        if (communication.IsOpen || flow.Running || manual) throw new InvalidOperationException("先关闭端口和流程再替换项目");
        ProjectStore.Validate(next); if (!ConfirmSave()) return;
        lock (sync) project = next; path = nextPath; selected = null; editorDirty = false; dirty = false; log.Config = project.Logs; RefreshLibrary(); RefreshJobs(); UpdateState();
    }
    void Open() { using var d = new OpenFileDialog { Filter = "FastSim 项目 XML|*.xml" }; if (d.ShowDialog(this) == DialogResult.OK) Replace(ProjectStore.Load(d.FileName), d.FileName); }
    void Import()
    {
        using var d = new OpenFileDialog { Filter = "XML / SML|*.xml;*.sml" }; if (d.ShowDialog(this) != DialogResult.OK) return;
        if (Path.GetExtension(d.FileName).Equals(".xml", StringComparison.OrdinalIgnoreCase)) { Replace(ProjectStore.Load(d.FileName), d.FileName); return; }
        var parsed = Sml.Parse(File.ReadAllText(d.FileName)); if (!CommitEditor()) return; project.Library.AddRange(parsed); dirty = true; RefreshLibrary(parsed[0].Id);
    }
    void Export()
    {
        if (!CommitEditor()) return; using var d = new SaveFileDialog { Filter = "FastSim 项目 XML（非 WinSECS）|*.xml|SML 模板（保留变量）|*.sml|SML 具体报文（解析变量）|*.sml", FileName = "export" }; if (d.ShowDialog(this) != DialogResult.OK) return;
        if (d.FilterIndex == 1) { lock (sync) ProjectStore.Save(d.FileName, project); return; }
        var output = project.Library.Select(t => { if (d.FilterIndex == 2) return Sml.Write(t); using var m = Variables.Message(t, Scope()); return Sml.Write(Sml.FromMessage(m)); }).ToArray(); ProjectStore.AtomicText(d.FileName, string.Join("\r\n", output));
    }
    void ExportLogs() { using var d = new SaveFileDialog { Filter = "文本日志|*.txt", FileName = "traffic-trace.txt" }; if (d.ShowDialog(this) == DialogResult.OK) ProjectStore.AtomicText(d.FileName, string.Join("\r\n", history.Select(x => $"{x.Time:O} {x.Direction} {x.SystemBytes} {x.Result}\r\n{x.Sml}")) + "\r\nTrace\r\n" + trace.Text); }
    void Manual() { var doc = Path.Combine(AppContext.BaseDirectory, "中文使用说明.md"); if (File.Exists(doc)) Process.Start(new ProcessStartInfo(doc) { UseShellExecute = true }); else MessageBox.Show(this, "请阅读交付目录 docs/中文使用说明.md。\n先配置端口并等待 Selected；单条发送默认发送当前模板。\n一键发送只打开步骤配置，点击开始发送后才执行。\nWinSECS XML 待实际样本验证。"); }
    async void OnClosing(object? sender, FormClosingEventArgs e)
    {
        if (closing) return; e.Cancel = true;
        try { if (!ConfirmSave()) return; timer.Stop(); flow.Stop(); await communication.CloseAsync(); while (flow.Running || manual) await Task.Delay(50); await log.DisposeAsync(); closing = true; Close(); }
        catch (Exception ex) { timer.Start(); MessageBox.Show(this, ex.Message); }
    }
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData) { if (keyData == Keys.F5) { SafeAsync(SendCurrent); return true; } if (keyData == Keys.Escape && flow.Running) { flow.Stop(); return true; } return base.ProcessCmdKey(ref msg, keyData); }
}
