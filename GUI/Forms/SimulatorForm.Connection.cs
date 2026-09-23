using System.Collections.Concurrent;
using System.Globalization;

namespace FastSim;

public sealed partial class SimulatorForm
{
    readonly bool mockMode;
    string role;
    ConnectionConfig config = new();
    Communication? transport;
    SimLog? liveLog;
    readonly ConcurrentQueue<TrafficRecord> liveTraffic = new();
    readonly ConcurrentQueue<string> liveTrace = new();
    readonly List<string> controlHistory = [];
    readonly System.Windows.Forms.Timer liveTimer = new() { Interval = 80 };
    readonly ComboBox roleBox = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 170 };
    readonly ComboBox modeBox = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 100 };
    readonly CheckBox automaticBox = new() { Text = "Auto reply", Checked = true, AutoSize = true };
    readonly CheckBox suppressBox = new() { Text = "S1F3 no reply", AutoSize = true };
    readonly NumericUpDown delayBox = new() { Minimum = 0, Maximum = 60000, Width = 75 };
    readonly Label connectionSummary = new() { Dock = DockStyle.Fill, Height = 44, TextAlign = ContentAlignment.MiddleLeft, Padding = new(5, 0, 0, 0), BackColor = Color.FromArgb(235, 243, 250) };
    readonly List<ToolStripItem> portOpenItems = [], portCloseItems = [], portConfigItems = [];
    Button? openButton, closeButton, configButton, linkButton;
    volatile bool autoReply = true, suppressS1F3;
    volatile int replyDelay;
    bool closingLive, connectionBusy;
    bool switchingRole, syncingRole;
    readonly List<ToolStripMenuItem> roleMenuItems = [];
    int transactionNumber = 1000;
    readonly CancellationTokenSource formLifetime = new();

    Control BuildConnectionPanel()
    {
        var panel = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 1, RowCount = 2, Margin = Padding.Empty }; panel.ColumnStyles.Add(new(SizeType.Percent, 100)); panel.RowStyles.Add(new(SizeType.AutoSize)); panel.RowStyles.Add(new(SizeType.Absolute, 44));
        var bar = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new(4), Margin = Padding.Empty };
        roleBox.Items.AddRange(["EAP / Host", "EQP / Equipment"]); roleBox.SelectedIndex = role == "EAP" ? 0 : 1;
        modeBox.Items.AddRange(["Active", "Passive"]); modeBox.SelectedIndex = role == "EAP" ? 0 : 1;
        config.Active = role == "EAP";
        roleBox.SelectedIndexChanged += (_, _) => { if (!syncingRole) { string target = roleBox.SelectedIndex == 0 ? "EAP" : "EQP"; LiveAction(() => SwitchRoleAsync(target)); } };
        modeBox.SelectedIndexChanged += (_, _) => { config.Active = modeBox.SelectedIndex == 0; UpdateLiveState(); };
        automaticBox.CheckedChanged += (_, _) => autoReply = automaticBox.Checked;
        suppressBox.CheckedChanged += (_, _) => suppressS1F3 = suppressBox.Checked;
        delayBox.ValueChanged += (_, _) => replyDelay = (int)delayBox.Value;
        openButton = Dialogs.Button("Open", () => LiveAction(OpenLiveAsync)); closeButton = Dialogs.Button("Close", () => LiveAction(CloseLiveAsync));
        configButton = Dialogs.Button("Connection…", () => Guard(ConfigureLive)); linkButton = Dialogs.Button("Linktest", () => LiveAction(() => transport!.LinkTestAsync()));
        bar.Controls.Add(new Label { Text = "Role:", AutoSize = true, Padding = new(0, 7, 0, 0) }); bar.Controls.Add(roleBox);
        bar.Controls.Add(new Label { Text = "Mode:", AutoSize = true, Padding = new(0, 7, 0, 0) }); bar.Controls.Add(modeBox);
        bar.Controls.Add(configButton); bar.Controls.Add(openButton); bar.Controls.Add(closeButton); bar.Controls.Add(linkButton);
        bar.Controls.Add(automaticBox); bar.Controls.Add(suppressBox); bar.Controls.Add(new Label { Text = "S1F3 delay(ms):", AutoSize = true, Padding = new(0, 7, 0, 0) }); bar.Controls.Add(delayBox);
        panel.Controls.Add(bar, 0, 0); panel.Controls.Add(connectionSummary, 0, 1); return panel;
    }
    void BuildPortMenu(ToolStripMenuItem menu)
    {
        var roles = new ToolStripMenuItem("Simulation Role"); menu.DropDownItems.Add(roles);
        foreach (string target in new[] { "EAP", "EQP" })
        {
            var entry = new ToolStripMenuItem(target == "EAP" ? "EAP / Host (Active)" : "EQP / Equipment (Passive)") { Tag = target };
            entry.Click += (_, _) => LiveAction(() => SwitchRoleAsync(target)); roles.DropDownItems.Add(entry); roleMenuItems.Add(entry);
        }
        portConfigItems.Add(menu.DropDownItems.Add("Connection configuration…", null, (_, _) => Guard(ConfigureLive)));
        portOpenItems.Add(menu.DropDownItems.Add("Open", null, (_, _) => LiveAction(OpenLiveAsync)));
        portCloseItems.Add(menu.DropDownItems.Add("Close", null, (_, _) => LiveAction(CloseLiveAsync)));
        menu.DropDownItems.Add("Linktest.req", null, (_, _) => LiveAction(() => transport!.LinkTestAsync()));
    }
    void InitializeLive()
    {
        string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FastSim", "logs", $"{DateTime.Now:yyyyMMdd-HHmmss}-{role}-{Environment.ProcessId}");
        liveLog = new(path); transport = new(liveLog); InitializeFlow();
        liveLog.TrafficLogged += r => liveTraffic.Enqueue(r); liveLog.TraceLogged += line => liveTrace.Enqueue(line);
        RefreshAutoReplies();
        transport.PrimaryReceived = async pending =>
        {
            var t = pending.Message;
            if (t.Function == 255 || !autoReplyTemplates.TryGetValue((t.Stream, (byte)(t.Function + 1)), out var reply)) { liveLog.Warning($"Unknown primary S{t.Stream}F{t.Function} [{pending.Id:X8}]，保留待手动处理"); return; }
            if (!autoReply || !t.W || suppressS1F3 && t.Stream == 1 && t.Function == 3) { liveLog.Info($"No auto reply: {pending.Id:X8} {t}"); return; }
            var token = transport.SessionToken;
            if (t.Stream == 1 && t.Function == 3 && replyDelay > 0) await Task.Delay(replyDelay, token);

            await transport.ReplyAsync(pending, reply, new Dictionary<string, Variable>(), token);
        };
        transport.Changed += () => { if (IsHandleCreated && !IsDisposed) try { BeginInvoke(UpdateLiveState); } catch (InvalidOperationException) { } };
        liveTimer.Tick += (_, _) => DrainLive(); liveTimer.Start();
        FormClosing += async (_, e) =>
        {
            if (switchingRole) { e.Cancel = true; return; }
            if (closingLive) return; e.Cancel = true;
            try { SaveFlowProject(); } catch (Exception ex) { MessageBox.Show(this, ex.Message + "\r\nUse Save As before closing.", "Save Project Failed"); return; }
            closingLive = true; liveTimer.Stop(); flowRunner?.Stop(); formLifetime.Cancel();
            try { await transport.CloseAsync(); DrainLive(); await liveLog.DisposeAsync(); } catch (Exception ex) { monitor.AppendText(ex.Message); }
            Close();
        };
        UpdateLiveState();
        liveLog.Info("LIVE UI initialized. Role and HSMS Active/Passive are independent; both sides can send Primary.");
    }
    public void OpenOnStart() { if (!mockMode) LiveAction(OpenLiveAsync); }
    void SyncRoleControls()
    {
        syncingRole = true;
        try { roleBox.SelectedIndex = role == "EAP" ? 0 : 1; modeBox.SelectedIndex = config.Active ? 0 : 1; }
        finally { syncingRole = false; }
    }
    async Task SwitchRoleAsync(string target)
    {
        if (target is not ("EAP" or "EQP")) throw new ArgumentException("Role must be EAP or EQP");
        if (switchingRole || connectionBusy || closingLive || target == role) { SyncRoleControls(); return; }
        switchingRole = true; connectionBusy = true; SyncRoleControls(); UpdateLiveState();
        string previous = role; var previousConfig = ProjectStore.Copy(config);
        try
        {
            // Keep the same workspace and protocol engine; end the old session before changing its role.
            SaveFlowProject(); flowRunner?.Stop();
            if (transport != null) await transport.CloseAsync();
            DrainLive(); role = target; config.Active = target == "EAP"; flowProject.SimulationRole = role;
            SyncRoleControls(); RefreshAutoReplies(); SaveFlowProject();
            liveLog?.Info($"Simulation role switched {previous} -> {role}. {(config.Active ? "Active" : "Passive")}; connection closed. Click Open to start the new session. Library and Flow retained.");
        }
        catch { role = previous; config = previousConfig; flowProject.SimulationRole = previous; SyncRoleControls(); RefreshAutoReplies(); throw; }
        finally { switchingRole = false; connectionBusy = false; UpdateLiveState(); }
    }
    async void LiveAction(Func<Task> action)
    {
        try { await action(); }
        catch (Exception e) { liveLog?.Info("操作失败：" + e.Message); MessageBox.Show(this, e.Message, "SECS/GEM Simulator"); }
        finally { DrainLive(); UpdateLiveState(); }
    }
    void ConfigureLive()
    {
        if (transport?.IsOpen == true) throw new InvalidOperationException("Close before editing connection settings");
        var changed = Dialogs.Connection(this, config); if (changed == null) return;
        config = changed; modeBox.SelectedIndex = config.Active ? 0 : 1; UpdateLiveState();
    }
    async Task OpenLiveAsync()
    {
        if (transport == null || connectionBusy) return;
        connectionBusy = true; UpdateLiveState();
        try { await transport.OpenAsync(config); } finally { connectionBusy = false; UpdateLiveState(); }
    }
    async Task CloseLiveAsync()
    {
        if (transport == null || connectionBusy) return;
        connectionBusy = true; UpdateLiveState();
        try { await transport.CloseAsync(); } finally { connectionBusy = false; DrainLive(); UpdateLiveState(); }
    }
    async Task<Template?> SendLiveAsync(Template? t, int? systemBytes = null)
    {
        if (!structure.TryCommitCell()) throw new FormatException(structure.EditError);
        if (t == null || transport == null) throw new InvalidOperationException("请选择模板");
        var snapshot = ProjectStore.Copy(t);
        return await transport.SendAsync(snapshot, new Dictionary<string, Variable>(), config.T3 + 1, formLifetime.Token, systemBytes: systemBytes);
    }
    async Task ReplyLiveAsync()
    {
        if (current == null || selectedTransaction == null || transport == null) throw new InvalidOperationException("请选择待回复事务及 Secondary 模板");
        if (selectedTransaction.Session != transport.Generation || selectedTransaction.PrimaryDirection != "RX" || !transport.Pending.TryGetValue(unchecked((int)selectedTransaction.SystemBytes), out var pending)) throw new InvalidOperationException("原请求不再有效");
        await transport.ReplyAsync(pending, ProjectStore.Copy(current), new Dictionary<string, Variable>(), formLifetime.Token);
    }
    void FaultDialog()
    {
        if (current == null || current.Function % 2 != 0 || current.W) throw new InvalidOperationException("先选择 W=0 Secondary 模板");
        var text = Dialogs.Text(this, "FAULT INJECTION · 错误 System Bytes（十进制）", "999999"); if (text == null) return;
        if (!uint.TryParse(text, out uint id)) throw new FormatException("请输入 uint32 十进制 System Bytes");
        if (MessageBox.Show(this, $"故障注入：将 {current} 按 SB={id} 发送，不使用原事务。继续？", "Explicit fault injection", MessageBoxButtons.YesNo) == DialogResult.Yes)
            LiveAction(() => transport!.SendFaultSecondaryAsync(ProjectStore.Copy(current), unchecked((int)id), formLifetime.Token));
    }
    void UpdateLiveState()
    {
        if (mockMode || transport == null) return;
        string state = transport.CanSend ? "Selected" : transport.Connected ? "Connected" : transport.IsOpen ? config.Active ? "Connecting / Retry" : "Listening" : "Disconnected";
        status.Text = $"{role} | {(config.Active ? "Active" : "Passive")} | {state}"; status.ForeColor = transport.CanSend ? Color.DarkGreen : Color.DarkOrange;
        connectionSummary.Text = $"Role: {role}   Mode: {(config.Active ? "Active" : "Passive")}   State: {state}   Connected: {transport.Connected}   Selected: {transport.CanSend}\nLocal: {transport.LocalEndPoint}   Remote: {transport.RemoteEndPoint}   T3: {config.T3}s";
        Text = $"SECS/GEM Simulator · {role} / {(config.Active ? "Active" : "Passive")} · {state} · PID {Environment.ProcessId}";
        roleBox.Enabled = !connectionBusy && !closingLive;
        modeBox.Enabled = !transport.IsOpen && !connectionBusy;
        foreach (var entry in roleMenuItems) { entry.Enabled = !connectionBusy && !closingLive; entry.Checked = (string)entry.Tag! == role; }
        if (configButton != null) configButton.Enabled = !transport.IsOpen && !connectionBusy;
        if (openButton != null) openButton.Enabled = !transport.IsOpen && !connectionBusy;
        if (closeButton != null) closeButton.Enabled = transport.IsOpen && !connectionBusy;
        if (linkButton != null) linkButton.Enabled = transport.CanSend && !connectionBusy;
        foreach (var item in portConfigItems.Concat(portOpenItems)) item.Enabled = !transport.IsOpen && !connectionBusy;
        foreach (var item in portCloseItems) item.Enabled = transport.IsOpen && !connectionBusy;
        foreach (var item in items.Where(x => x.Status == "WAITING" && (x.Session != transport.Generation || !transport.CanSend))) { item.Outcome = "ERROR · Disconnected"; item.Finished = DateTime.Now; RefreshTransaction(item); }
        UpdateButtons(); UpdateFlowUi();
    }
    void DrainLive()
    {
        if (mockMode || transport == null) return;
        transport.ExpirePending();
        for (int i = 0; i < 500 && liveTraffic.TryDequeue(out var r); i++)
        {
            if (!uint.TryParse(r.SystemBytes, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var id)) continue;
            if (r.Direction == "结果")
            {
                var t = items.LastOrDefault(x => x.Session == r.Session && x.SystemBytes == id && x.PrimaryDirection == "TX" && x.Primary != null && x.Primary.SF == r.SF);
                if (t != null) { t.Outcome = r.Result; t.Finished = r.Time; RefreshTransaction(t); }
                continue;
            }
            try
            {
                var template = Sml.Parse(r.Sml)[0];
                if (r.Direction == "RX" && template.Name.Length == 0) template.Name = templates.FirstOrDefault(t => t.Stream == template.Stream && t.Function == template.Function)?.Name ?? "";
                AddLiveMessage(new(template.Function % 2 == 0 || template.Stream == 9 ? "S" : "P", r.Direction, id, r.Time, template) { Session = r.Session, FlowLabel = r.FlowLabel });
            }
            catch (Exception e) { liveLog?.Warning("报文显示失败：" + e.Message); }
        }
        var lines = new List<string>(); while (liveTrace.TryDequeue(out var line)) { lines.Add(line); controlHistory.Add(line); }
        if (lines.Count > 0) { monitor.AppendText(string.Join(Environment.NewLine, lines) + Environment.NewLine); if (monitor.Lines.Length > 3000) monitor.Lines = monitor.Lines.TakeLast(3000).ToArray(); }
        if (controlHistory.Count > 10000) controlHistory.RemoveRange(0, controlHistory.Count - 10000);
        foreach (var t in items.Where(x => x.Status == "WAITING" && x.PrimaryDirection == "RX" && x.Primary != null && (DateTime.Now - x.Primary.Time).TotalSeconds >= config.T3)) { t.Outcome = "TIMEOUT · pending reply expired"; t.Finished = DateTime.Now; RefreshTransaction(t); }
        // UI retention is bounded; log files remain governed by SimLog rotation.
        while (items.Count > 3000) { var oldest = items[0]; items.RemoveAt(0); var n = transactions.Nodes.Cast<TreeNode>().FirstOrDefault(x => ReferenceEquals(x.Tag, oldest)); n?.Remove(); }
        while (trace.Rows.Count > 6000) trace.Rows.RemoveAt(0);
        UpdateButtons(); UpdateFlowUi();
    }
    void AddLiveMessage(MonitorMessage message)
    {
        string primaryDirection = message.Type == "P" ? message.Direction : message.Direction == "RX" ? "TX" : "RX";
        var candidates = items.Where(x => x.Session == message.Session && x.SystemBytes == message.SystemBytes && x.PrimaryDirection == primaryDirection).ToList();
        var t = message.Type == "P" ? null : candidates.LastOrDefault(x => x.Secondary == null && (x.Status == "WAITING" || x.Primary == null));
        bool valid = message.Type == "P" || t?.Primary == null || message.Template.Stream == t.Primary.Template.Stream && message.Template.Function == t.Primary.Template.Function + 1 && !message.Template.W;
        if (!valid) { t = null; liveLog?.Warning($"Monitor: reject header mismatch SB={message.SystemBytes:X8}"); }
        if (t == null) { t = new(++transactionNumber, message.SystemBytes, message.Session, primaryDirection); items.Add(t); }
        t.Messages.Add(message);
        int row = trace.Rows.Add(message.Time.ToString("HH:mm:ss.fff"), message.Direction, message.Type, message.SF, message.Template.W ? "W" : "", message.SystemBytes.ToString("X8"), message.Template.Name + (valid ? "" : " · ERROR header")); trace.Rows[row].Tag = message;
        if (!valid) t.Outcome = "ERROR · unmatched header";
        RefreshTransaction(t);
        counts.Text = $"TX: {items.SelectMany(x => x.Messages).Count(x => x.Direction == "TX")}   RX: {items.SelectMany(x => x.Messages).Count(x => x.Direction == "RX")}";
        if (message.FlowLabel.Length > 0) { var node = transactions.Nodes.Cast<TreeNode>().Single(n => ReferenceEquals(n.Tag, t)); node.Expand(); transactions.SelectedNode = node.Nodes[1]; }
        else if (transactions.SelectedNode == null) transactions.SelectedNode = transactions.Nodes[0];
    }
}






