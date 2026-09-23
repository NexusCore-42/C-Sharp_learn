using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Runtime.InteropServices;

namespace FastSim;

public sealed partial class SimulatorForm
{
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool PrintWindow(IntPtr window, IntPtr deviceContext, uint flags);
    // Opt-in, same-user UI automation for two real EXE processes. This is a control
    // channel only; every SECS operation calls the normal UI/Communication path.
    public void StartAutomation(string pipeName)
    {
        if (mockMode) throw new InvalidOperationException("Live acceptance requires live UI");
        _ = Task.Run(async () =>
        {
            while (!formLifetime.IsCancellationRequested)
            {
                var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                try { await pipe.WaitForConnectionAsync(formLifetime.Token); }
                catch (OperationCanceledException) { pipe.Dispose(); break; }
                _ = HandlePipe(pipe);
            }
        });
    }
    async Task HandlePipe(NamedPipeServerStream pipe)
    {
        using (pipe)
        {
            try
            {
                using var reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, true);
                using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, true) { AutoFlush = true };
                var line = await reader.ReadLineAsync(formLifetime.Token) ?? throw new FormatException("Empty command");
                using var doc = JsonDocument.Parse(line); var command = doc.RootElement.Clone();
                var completion = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
                BeginInvoke(new Action(async () =>
                {
                    try { completion.TrySetResult(await ExecuteAutomation(command)); }
                    catch (Exception e) { completion.TrySetResult(new { Ok = false, Error = e.Message }); }
                }));
                await writer.WriteLineAsync(JsonSerializer.Serialize(await completion.Task));
            }
            catch (Exception e) when (e is IOException or OperationCanceledException or InvalidOperationException) { }
        }
    }
    async Task<object> ExecuteAutomation(JsonElement command)
    {
        string Get(string key, string fallback = "") => command.TryGetProperty(key, out var v) ? v.GetString() ?? fallback : fallback;
        double Num(string key, double fallback) => command.TryGetProperty(key, out var v) ? v.GetDouble() : fallback;
        bool Bool(string key, bool fallback) => command.TryGetProperty(key, out var v) ? v.GetBoolean() : fallback;
        Template TemplateFor() => Get("TemplateId").Length > 0 ? templates.Single(x => x.Id == Get("TemplateId")) : templates.First(x => x.Stream == (byte)Num("S", 1) && x.Function == (byte)Num("F", 1));
        DrainLive();
        switch (Get("Action"))
        {
            case "roleui":
                if (Get("Via") == "Menu") roleMenuItems.Single(i => (string)i.Tag! == Get("Role")).PerformClick();
                else roleBox.SelectedIndex = Get("Role") == "EAP" ? 0 : 1;
                while (switchingRole) await Task.Delay(10);
                return Snapshot();
            case "configure":
                if (transport!.IsOpen) throw new InvalidOperationException("Close first");
                config.IP = Get("IP", "127.0.0.1"); config.Port = (int)Num("Port", 5000); config.Active = Bool("Active", role == "EAP");
                config.T3 = Num("T3", 1); config.T5 = Num("T5", .2); config.T6 = Num("T6", 1); config.T7 = Num("T7", 2); config.T8 = Num("T8", 1);
                config.Linktest = Bool("Linktest", true); config.LinktestSeconds = Num("LinktestSeconds", 60); config.Options(); modeBox.SelectedIndex = config.Active ? 0 : 1; UpdateLiveState(); break;
            case "set":
                automaticBox.Checked = Bool("Auto", autoReply); suppressBox.Checked = Bool("SuppressS1F3", suppressS1F3); delayBox.Value = (decimal)Num("Delay", replyDelay); break;
            case "open": await OpenLiveAsync(); break;
            case "close": await CloseLiveAsync(); break;
            case "linktest": await transport!.LinkTestAsync(); break;
            case "send":
                var t = Get("Sml").Length > 0 ? Sml.Parse(Get("Sml")).Single() : TemplateFor();
                if (templates.Contains(t)) SelectLibraryNode(t); else SelectTemplate(t);
                int? sb = command.TryGetProperty("SB", out var sbValue) ? unchecked((int)sbValue.GetUInt32()) : null;
                var reply = await SendLiveAsync(t, sb); DrainLive();
                return new { Ok = true, Reply = reply == null ? null : new { reply.Stream, reply.Function, reply.W, Sml = Sml.Write(reply) } };
            case "reply":
                uint id = (uint)Num("SB", 0);
                selectedTransaction = items.Last(x => x.SystemBytes == id && x.PrimaryDirection == "RX" && x.Session == transport!.Generation);
                SelectLibraryNode(TemplateFor()); await ReplyLiveAsync(); break;
            case "fault": await transport!.SendFaultSecondaryAsync(ProjectStore.Copy(TemplateFor()), unchecked((int)(uint)Num("SB", 0)), formLifetime.Token); break;
            case "libraryaudit": return AuditLibraryEditor();
            case "librarysnapshot": return LibrarySnapshot();
            case "structureaudit": return await AuditStructureEditing();
            case "libraryimport": ImportLibraryText(Get("Text"), Bool("Xml", true)); break;
            case "flowuiaudit": return await AuditFlowEditor();
            case "flowconfigure":
                if (flowRunner!.Running) throw new InvalidOperationException("Flow running");
                var definition = command.GetProperty("Flow").Deserialize<FlowDefinition>()!;
                flowProject.Flows = [definition]; RefreshFlowList(); SaveFlowProject(); break;
            case "flowrun": ((TabControl)flowPage!.Parent!).SelectedTab = flowPage; flowRunButton!.PerformClick(); break;
            case "flowstop": flowStopButton!.PerformClick(); break;
            case "flowreset": flowResetButton!.PerformClick(); break;
            case "flowreload": LoadFlowProject(flowProjectPath); break;
            case "flowsnapshot": return new { Ok = true, State = flowRunner!.State, Step = flowRunner.CurrentStep, States = flowRunner.StepStates, StatusText = flowStatus.Text, Rows = flowGrid.RowCount, Path = flowProjectPath, Flows = flowProject.Flows, Running = flowRunner.Running };
            case "snapshot": return Snapshot();
            case "uiaudit": return await AuditLiveUi();
            case "capture":
                if (Get("View") == "Library")
                {
                    ((TabControl)flowPage!.Parent!).SelectedIndex = 0;
                    var selectedTemplate = templates.FirstOrDefault(t => t.TemplateName == Get("TemplateName"));
                    if (selectedTemplate != null) SelectLibraryNode(selectedTemplate);
                }
                if (command.TryGetProperty("Width", out var width)) Size = new(width.GetInt32(), (int)Num("Height", 800));
                Visible = false; Visible = true; TopMost = true; BringToFront(); Activate();
                if (items.Count > 0)
                {
                    var match = items.LastOrDefault(x => x.SystemBytes == (uint)Num("SB", 101)) ?? items[^1];
                    var root = transactions.Nodes.Cast<TreeNode>().Single(x => ReferenceEquals(x.Tag, match)); root.Expand();
                    transactions.SelectedNode = root.Nodes.Count > 1 ? root.Nodes[1] : root;
                }
                await Task.Delay(150);
                var path = Path.GetFullPath(Get("Path")); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                using (var bmp = new Bitmap(Width, Height))
                {
                    using var graphics = Graphics.FromImage(bmp); var dc = graphics.GetHdc(); bool captured;
                    try { captured = PrintWindow(Handle, dc, 2); } finally { graphics.ReleaseHdc(dc); }
                    if (!captured) throw new InvalidOperationException("PrintWindow failed; will not capture a different foreground window");
                    bmp.Save(path);
                }
                TopMost = false; break;
            case "quit": BeginInvoke(Close); break;
            default: throw new FormatException("Unknown automation command");
        }
        DrainLive(); UpdateLiveState(); return new { Ok = true };
    }
    object Snapshot()
    {
        DrainLive(); UpdateLiveState();
        return new
        {
            Ok = true, Role = role, Active = config.Active, IsOpen = transport!.IsOpen, Connected = transport.Connected, Selected = transport.CanSend,
            State = transport.State, Session = transport.Generation, Local = transport.LocalEndPoint, Remote = transport.RemoteEndPoint, UiStatus = connectionSummary.Text,
            Pending = transport.Pending.Values.Select(x => new { SB = unchecked((uint)x.Id), S = x.Message.Stream, F = x.Message.Function }).ToArray(),
            Transactions = items.Select(x => new { x.Number, SB = x.SystemBytes, x.Session, x.PrimaryDirection, x.Status, FlowLabel = x.Primary?.FlowLabel, Duration = x.ResponseTime, Messages = x.Messages.Select(m => new { m.Type, m.Direction, m.SF, W = m.Template.W, m.Time, SB = m.SystemBytes, Raw = m.Raw }).ToArray() }).ToArray(),
            Logs = controlHistory.ToArray(), TraceRows = trace.RowCount
        };
    }
}




