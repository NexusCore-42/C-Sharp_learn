using System.Net;
using System.Net.Sockets;

namespace FastSim;
public sealed partial class MainForm
{
    // Explicit diagnostic switch; never runs in normal application startup.
    public async Task RunUiSmoke(string directory)
    {
        Directory.CreateDirectory(directory); var report = new List<string>();
        void Check(bool ok, string name) { if (!ok) throw new Exception(name); report.Add("PASS " + name); }
        void Capture(string name) { using var bitmap = new Bitmap(Width, Height); DrawToBitmap(bitmap, new Rectangle(Point.Empty, Size)); bitmap.Save(Path.Combine(directory, name + ".png")); }
        await using var peerLog = new SimLog(Path.Combine(directory, "peer")); await using var peer = new Communication(peerLog);
        try
        {
            // A launcher may start hidden. Explicit UI diagnostics must render a real window.
            Visible = false; Visible = true; WindowState = FormWindowState.Normal; TopMost = true; BringToFront(); Activate();
            await Task.Delay(300);
            Check(MainMenuStrip!.Items.Cast<ToolStripMenuItem>().Select(x => x.Text).SequenceEqual(new[] { "文件", "端口", "库", "信息", "设置", "帮助" }), "顶层菜单顺序");
            Check(library.RowCount == 5 && selected != null && editor.Text.Contains("S1F1"), "消息库和编辑区初始化");
            Capture("main-default");
            Size = new(1100, 760); await Task.Delay(100); Capture("main-small"); Check(send.Visible && one.Visible && trace.Visible, "缩小窗口关键控件可见"); Size = new(1560, 980); await Task.Delay(100); Capture("main-large");
            editor.Text = "edited: 'S1F1' W <L [1] <A 'test'>> ."; Check(CommitEditor() && tree.Nodes[0].Nodes.Count == 1, "SML 编辑同步嵌套结构树");
            var root = selected!.Root!; root.Children.Add(new() { Type = "U1", Value = "1 2 3" }); SetEditor(); Check(Sml.Parse(editor.Text)[0].Root!.Children.Count == 2, "树模型修改同步 SML");
            bool inspected = false;
            using (var probe = new System.Windows.Forms.Timer { Interval = 150 })
            {
                probe.Tick += (_, _) =>
                {
                    var dialog = Application.OpenForms.Cast<Form>().FirstOrDefault(f => f.Text.StartsWith("一键发送配置")); if (dialog == null) return;
                    IEnumerable<Control> Walk(Control c) { foreach (Control child in c.Controls) { yield return child; foreach (var sub in Walk(child)) yield return sub; } }
                    var grid = Walk(dialog).OfType<DataGridView>().Single(); var count = Walk(dialog).OfType<NumericUpDown>().Single();
                    count.Value = 4; Check(grid.RowCount == 4, "发送条数 N=4 生成4个步骤");
                    using var image = new Bitmap(dialog.Width, dialog.Height); dialog.DrawToBitmap(image, new Rectangle(0, 0, dialog.Width, dialog.Height)); image.Save(Path.Combine(directory, "flow-dialog.png"));
                    inspected = true; probe.Stop(); dialog.DialogResult = DialogResult.Cancel; dialog.Close();
                };
                probe.Start(); await ConfigureFlow();
            }
            Check(inspected && OneClickOpenCount == 1 && history.Count == 0 && !communication!.IsOpen, "一键发送只打开配置且不发消息");
            var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); project.Connection.Port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
            await OpenPort(); Check(!send.Enabled && status.Text?.Contains("监听") == true, "监听状态禁用发送");
            var peerConfig = ProjectStore.Copy(project.Connection); peerConfig.Active = true; await peer.OpenAsync(peerConfig);
            var peerProject = Project.Demo(); var auto = new AutoResponder(peer, peerLog, () => peerProject, new object()); peer.PrimaryReceived = auto.Handle;
            using (var timeout = new CancellationTokenSource(7000)) while (!communication.CanSend || !peer.CanSend) await Task.Delay(20, timeout.Token);
            UpdateState(); Check(send.Enabled && port.Text?.Contains("关闭") == true && configItems.All(x => !x.Enabled), "Selected 按钮状态与参数锁定");
            await SendCurrent(); await Task.Delay(150); Drain(); Check(history.Any(x => x.Direction == "TX") && history.Any(x => x.Direction == "RX"), "真实 UI 单条发送与 Traffic 更新");
            Capture("main-connected");
            Activate(); await Task.Delay(100);
            using (var screen = new Bitmap(Width, Height)) { using var graphics = Graphics.FromImage(screen); graphics.CopyFromScreen(Location, Point.Empty, Size); screen.Save(Path.Combine(directory, "screen-connected.png")); }
            Check(status.Text?.Contains("Selected") == true && status.Bounds.Width > 200, "底部状态栏连接状态与有效布局");
            await ClosePort(); Check(!send.Enabled && configItems.All(x => x.Enabled), "关闭端口恢复配置操作");
            path = Path.Combine(directory, "ui-project.xml"); Check(Save(false) && ProjectStore.Load(path).Library[0].Root!.Children.Count == 2, "UI 保存结构修改到项目");
            report.Add("TOTAL " + report.Count + " PASS");
        }
        catch (Exception e) { report.Add("FAIL " + e); Environment.ExitCode = 1; }
        finally { await peer.CloseAsync(); dirty = false; editorDirty = false; File.WriteAllLines(Path.Combine(directory, "ui-results.txt"), report); Close(); }
    }
}
