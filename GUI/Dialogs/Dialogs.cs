using System.ComponentModel;
using System.Text.Json;

namespace FastSim;
public static class Dialogs
{
    public static Form Window(string title, int width = 820, int height = 610) => new() { Text = title, Size = new(width, height), MinimumSize = new(580, 400), StartPosition = FormStartPosition.CenterParent, Font = new("Microsoft YaHei UI", 10), AutoScaleMode = AutoScaleMode.Dpi };
    public static Button Button(string text, Action click) { var b = new Button { Text = text, AutoSize = true, MinimumSize = new(92, 32) }; b.Click += (_, _) => click(); return b; }
    public static FlowLayoutPanel Bar() => new() { Dock = DockStyle.Bottom, Height = 48, Padding = new(6), AutoSize = false };
    public static string? Text(IWin32Window owner, string title, string value)
    {
        using var f = Window(title, 620, 430); var input = new TextBox { Dock = DockStyle.Fill, Multiline = true, ScrollBars = ScrollBars.Both, Text = value };
        var bar = Bar(); bar.Controls.Add(Button("确定", () => f.DialogResult = DialogResult.OK)); bar.Controls.Add(Button("取消", () => f.DialogResult = DialogResult.Cancel)); f.Controls.Add(input); f.Controls.Add(bar);
        return f.ShowDialog(owner) == DialogResult.OK ? input.Text : null;
    }
    public static T? JsonEdit<T>(IWin32Window owner, string title, T value, string help, Action<T> validate) where T : class
    {
        using var f = Window(title, 980, 740); var input = new TextBox { Dock = DockStyle.Fill, Multiline = true, ScrollBars = ScrollBars.Both, AcceptsTab = true, Font = new("Consolas", 11), Text = JsonSerializer.Serialize(value, ProjectStore.Json) };
        var instructions = new TextBox { Dock = DockStyle.Top, Height = 95, Multiline = true, ReadOnly = true, Text = help, BackColor = Color.AliceBlue, ScrollBars = ScrollBars.Vertical };
        var bar = Bar(); T? result = null;
        bar.Controls.Add(Button("校验并保存", () => { try { result = JsonSerializer.Deserialize<T>(input.Text, ProjectStore.Json) ?? throw new FormatException("内容为空"); validate(result); f.DialogResult = DialogResult.OK; } catch (Exception e) { MessageBox.Show(f, e.Message, "配置错误"); } }));
        bar.Controls.Add(Button("取消", () => f.DialogResult = DialogResult.Cancel));
        f.Controls.Add(input); f.Controls.Add(instructions); f.Controls.Add(bar);
        return f.ShowDialog(owner) == DialogResult.OK ? result : null;
    }
    public static ConnectionConfig? Connection(IWin32Window owner, ConnectionConfig source)
    {
        using var f = Window("连接配置 · 所有时间单位为秒", 660, 690);
        var config = ProjectStore.Copy(source); var panel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, AutoScroll = true, Padding = new(18) };
        panel.ColumnStyles.Add(new(SizeType.Percent, 46)); panel.ColumnStyles.Add(new(SizeType.Percent, 54));
        var active = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Top }; active.Items.AddRange(["Passive 本地监听", "Active 连接 EAP"]); active.SelectedIndex = config.Active ? 1 : 0;
        var ipLabel = new Label { AutoSize = true }; var portLabel = new Label { AutoSize = true };
        void Labels() { ipLabel.Text = active.SelectedIndex == 1 ? "EAP 远端 IP" : "本地监听 IP"; portLabel.Text = active.SelectedIndex == 1 ? "EAP 远端端口" : "本地监听端口"; }
        active.SelectedIndexChanged += (_, _) => Labels(); Labels();
        void Row(Control label, Control c) { int row = panel.RowCount++; panel.RowStyles.Add(new(SizeType.Absolute, 40)); panel.Controls.Add(label, 0, row); panel.Controls.Add(c, 1, row); }
        Control L(string s) => new Label { Text = s, AutoSize = true };
        NumericUpDown Num(decimal value, decimal max, decimal min = 0, int decimals = 0) => new() { Minimum = min, Maximum = max, DecimalPlaces = decimals, Value = value, Dock = DockStyle.Top };
        Row(L("HSMS 模式"), active); var ip = new TextBox { Text = config.IP, Dock = DockStyle.Top }; Row(ipLabel, ip);
        var port = Num(config.Port, 65535, 1); Row(portLabel, port); var device = Num(config.DeviceId, 65535); Row(L("Device ID"), device);
        var link = new CheckBox { Text = "启用 Linktest", Checked = config.Linktest, AutoSize = true }; Row(L("链路检测"), link);
        var numbers = new Dictionary<string, NumericUpDown>();
        foreach (var name in new[] { "LinktestSeconds", "T3", "T5", "T6", "T7", "T8" }) { var n = Num((decimal)(double)typeof(ConnectionConfig).GetProperty(name)!.GetValue(config)!, 2147483, .001m, 3); numbers[name] = n; Row(L(name == "LinktestSeconds" ? "Linktest Timer（秒）" : name + "（秒）"), n); }
        var bar = Bar(); bar.Controls.Add(Button("保存", () => { try { config.Active = active.SelectedIndex == 1; config.IP = ip.Text.Trim(); config.Port = (int)port.Value; config.DeviceId = (ushort)device.Value; config.Linktest = link.Checked; foreach (var (name, n) in numbers) typeof(ConnectionConfig).GetProperty(name)!.SetValue(config, (double)n.Value); config.Options(); f.DialogResult = DialogResult.OK; } catch (Exception e) { MessageBox.Show(f, e.Message); } }));
        bar.Controls.Add(Button("取消", () => f.DialogResult = DialogResult.Cancel)); f.Controls.Add(panel); f.Controls.Add(bar);
        return f.ShowDialog(owner) == DialogResult.OK ? config : null;
    }
    public static Node? EditNode(IWin32Window owner, Node source)
    {
        using var f = Window("编辑 SECS 节点", 610, 440); var node = source.Clone(); var type = new ComboBox { Dock = DockStyle.Top, DropDownStyle = ComboBoxStyle.DropDownList }; type.Items.AddRange(Sml.Types); type.SelectedItem = node.Type;
        var value = new TextBox { Dock = DockStyle.Fill, Text = node.Value, Multiline = true, ScrollBars = ScrollBars.Both };
        var bar = Bar(); bar.Controls.Add(Button("确定", () => { try { node.Type = type.Text; node.Value = value.Text; if (node.Type != "L" && node.Children.Count > 0) throw new FormatException("先删除子项再改为非 List 类型"); Sml.Parse(Sml.Write(new Template { Root = node })); f.DialogResult = DialogResult.OK; } catch (Exception e) { MessageBox.Show(f, e.Message); } }));
        f.Controls.Add(value); f.Controls.Add(type); f.Controls.Add(bar); return f.ShowDialog(owner) == DialogResult.OK ? node : null;
    }
    public static List<T>? Records<T>(IWin32Window owner, string title, List<T> source, string help, Action<List<T>> validate) where T : class, new()
    {
        using var f = Window(title, 1080, 690); var records = new BindingList<T>(ProjectStore.Copy(source));
        var grid = new DataGridView { Dock = DockStyle.Fill, AutoGenerateColumns = false, AllowUserToAddRows = false, AllowUserToDeleteRows = false, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.DisplayedCells, DataSource = records, SelectionMode = DataGridViewSelectionMode.FullRowSelect, MultiSelect = false, BackgroundColor = Color.White };
        var labels = new Dictionary<string, string> { ["Name"] = "名称", ["Type"] = "SECS 类型", ["Value"] = "值", ["Kind"] = "作业种类 PJ/CJ", ["Id"] = "作业 ID", ["State"] = "状态", ["RecipeID"] = "Recipe ID", ["CarrierID"] = "Carrier ID", ["Slots"] = "Slot 列表（空格分隔）", ["ProcessJobs"] = "关联 PJ（逗号分隔）" };
        foreach (var property in typeof(T).GetProperties().Where(x => x.PropertyType == typeof(string)))
        {
            if (property.Name is "Type" or "Kind") grid.Columns.Add(new DataGridViewComboBoxColumn { DataPropertyName = property.Name, HeaderText = labels.GetValueOrDefault(property.Name, property.Name), DataSource = property.Name == "Type" ? Sml.Types.Where(x => x != "L").ToArray() : new[] { "PJ", "CJ" } });
            else grid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = property.Name, HeaderText = labels.GetValueOrDefault(property.Name, property.Name) });
        }
        grid.DataError += (_, e) => { e.ThrowException = false; MessageBox.Show(f, "字段值无效"); };
        var instructions = new TextBox { Text = help, ReadOnly = true, Multiline = true, Dock = DockStyle.Top, Height = 74, BackColor = Color.AliceBlue };
        var bar = Bar(); bar.Controls.Add(Button("新增记录", () => records.Add(new T()))); bar.Controls.Add(Button("删除记录", () => { if (grid.CurrentRow != null) records.RemoveAt(grid.CurrentRow.Index); }));
        bar.Controls.Add(Button("高级 JSON / 扩展属性", () => { grid.EndEdit(); var next = JsonEdit(f, title + " · 高级", records.ToList(), help, validate); if (next == null) return; records = new BindingList<T>(next); grid.DataSource = records; }));
        bar.Controls.Add(Button("校验并保存", () => { try { grid.EndEdit(); validate(records.ToList()); f.DialogResult = DialogResult.OK; } catch (Exception e) { MessageBox.Show(f, e.Message, "校验失败"); } })); bar.Controls.Add(Button("取消", () => f.DialogResult = DialogResult.Cancel));
        f.Controls.Add(grid); f.Controls.Add(instructions); f.Controls.Add(bar); return f.ShowDialog(owner) == DialogResult.OK ? records.ToList() : null;
    }
    public static List<Step>? Flow(IWin32Window owner, Project p, FlowRunner runner, bool canSend, out bool start)
    {
        using var f = Window("一键发送配置 · 发送条数为步骤数", 1280, 700);
        var rows = new BindingList<Step>(ProjectStore.Copy(p.Steps));
        if (rows.Count == 0) rows.Add(new() { TemplateId = p.Library.FirstOrDefault(x => x.Function % 2 == 1)?.Id ?? "" });
        var top = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 50, Padding = new(7) };
        var count = new NumericUpDown { Minimum = 1, Maximum = 10000, Value = rows.Count, Width = 100 }; top.Controls.Add(new Label { Text = "发送条数", AutoSize = true }); top.Controls.Add(count);
        var grid = new DataGridView { Dock = DockStyle.Fill, AutoGenerateColumns = false, AllowUserToAddRows = false, AllowUserToDeleteRows = false, DataSource = rows, RowHeadersWidth = 60, SelectionMode = DataGridViewSelectionMode.FullRowSelect, BackgroundColor = Color.White, ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize };
        void Col(string property, string title, int width) => grid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = property, HeaderText = title, Width = width });
        Col("Name", "步骤名称", 110);
        grid.Columns.Add(new DataGridViewComboBoxColumn { DataPropertyName = "TemplateId", HeaderText = "消息模板", DataSource = p.Library.Where(x => x.Function % 2 == 1).ToList(), DisplayMember = "Name", ValueMember = "Id", Width = 160 });
        Col("Parameters", "本次参数 Name:Type=value;…", 220); Col("JobId", "作业 ID", 100);
        grid.Columns.Add(new DataGridViewCheckBoxColumn { DataPropertyName = "WaitReply", HeaderText = "等待回复", Width = 85 });
        Col("Expected", "预期 SxFy", 95); Col("Timeout", "超时（秒）", 90); Col("Interval", "发送后间隔（秒）", 125); Col("Extract", "提取 name=0/1", 150); Col("Check", "校验 0/1=0", 140);
        grid.Columns.Add(new DataGridViewCheckBoxColumn { DataPropertyName = "ContinueOnFailure", HeaderText = "失败继续", Width = 85 });
        grid.DataError += (_, e) => { e.ThrowException = false; MessageBox.Show(f, "单元格值无效，请检查数字或模板选择"); };
        grid.RowPostPaint += (_, e) => TextRenderer.DrawText(e.Graphics, (e.RowIndex + 1).ToString(), grid.Font, new Point(e.RowBounds.Left + 10, e.RowBounds.Top + 5), Color.DimGray);
        bool updating = false;
        count.ValueChanged += (_, _) =>
        {
            if (updating) return;
            grid.EndEdit(); int target = (int)count.Value;
            if (target < rows.Count && MessageBox.Show(f, "减少条数将删除末尾步骤，继续？", "确认", MessageBoxButtons.YesNo) != DialogResult.Yes) { updating = true; count.Value = rows.Count; updating = false; return; }
            while (rows.Count > target) rows.RemoveAt(rows.Count - 1);
            while (rows.Count < target) rows.Add(new() { TemplateId = p.Library.FirstOrDefault(x => x.Function % 2 == 1)?.Id ?? "" });
        };
        void SyncCount() { updating = true; count.Value = rows.Count; updating = false; }
        void Move(int offset) { grid.EndEdit(); int i = grid.CurrentRow?.Index ?? -1; int j = i + offset; if (i < 0 || j < 0 || j >= rows.Count) return; var s = rows[i]; rows.RemoveAt(i); rows.Insert(j, s); grid.CurrentCell = grid.Rows[j].Cells[0]; }
        top.Controls.Add(Button("上移", () => Move(-1))); top.Controls.Add(Button("下移", () => Move(1)));
        top.Controls.Add(Button("复制步骤", () => { if (grid.CurrentRow == null) return; grid.EndEdit(); rows.Insert(grid.CurrentRow.Index + 1, ProjectStore.Copy(rows[grid.CurrentRow.Index])); SyncCount(); }));
        top.Controls.Add(Button("删除步骤", () => { if (rows.Count > 1 && grid.CurrentRow != null) { rows.RemoveAt(grid.CurrentRow.Index); SyncCount(); } }));
        bool shouldStart = false; var bar = Bar();
        void Save(bool execute) { try { grid.EndEdit(); var candidate = ProjectStore.Copy(p); candidate.Steps = rows.ToList(); runner.Validate(candidate); shouldStart = execute; f.DialogResult = DialogResult.OK; } catch (Exception e) { MessageBox.Show(f, e.Message, "配置校验失败"); } }
        var go = Button("开始发送", () => Save(true)); go.Enabled = canSend; bar.Controls.Add(go); bar.Controls.Add(Button("保存配置", () => Save(false))); bar.Controls.Add(Button("取消", () => f.DialogResult = DialogResult.Cancel));
        bar.Controls.Add(new Label { AutoSize = true, Text = "根节点路径用 /，子项用 0/1（从零计数）；失败默认停止。", Padding = new(8) });
        f.Controls.Add(grid); f.Controls.Add(top); f.Controls.Add(bar);
        bool ok = f.ShowDialog(owner) == DialogResult.OK; start = shouldStart && ok; return ok ? rows.ToList() : null;
    }
}
