using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Threading.Channels;

namespace FastSim;
public sealed class FlowRunner(Communication communication, SimLog log)
{
    CancellationTokenSource? run;
    bool stopped;
    public bool Running => run != null;
    public string Progress { get; private set; } = "就绪";
    public string State { get; private set; } = "Pending";
    public int CurrentStep { get; private set; }
    public string[] StepStates { get; private set; } = [];
    public List<string> Results { get; } = [];
    public event Action? Changed;
    public void Stop() { stopped = true; run?.Cancel(); }
    public void Reset() { if (Running) throw new InvalidOperationException("Stop before Reset"); State = "Pending"; CurrentStep = 0; StepStates = []; Results.Clear(); Progress = "就绪"; Changed?.Invoke(); }
    public static Template TemplateFor(Project p, Step step) => step.RuntimeTemplate ?? p.Library.SingleOrDefault(t => t.Id == step.TemplateId) ?? throw new FormatException("步骤未选择模板");
    public static void ValidateStep(Step s, Template t)
    {
        if (s.Kind is not ("Send Primary" or "Wait Reply" or "Wait For Message")) throw new FormatException("未知步骤类型");
        if (!double.IsFinite(s.Timeout) || s.Timeout < .001 || s.Timeout > 2147483 || !double.IsFinite(s.Interval) || s.Interval < 0 || s.Interval > 86400) throw new FormatException("超时或间隔无效");
        if (s.Kind == "Send Primary")
        {
            if (t.Function % 2 == 0) throw new FormatException("顺序发送仅接受奇数主消息；应答请使用待回复列表");
            if (s.WaitReply && !t.W) throw new FormatException("等待回复步骤所选模板必须带 W-bit");
            if (!t.W && (s.Expected.Length > 0 || s.Extract.Length > 0 || s.Check.Length > 0)) throw new FormatException("无 W-bit 的消息不能配置回复条件");
        }
        if (s.Kind == "Wait Reply" && (t.Function % 2 != 0 || t.W)) throw new FormatException("Wait Reply 必须选择 W=0 Secondary");
        if (s.Kind == "Wait For Message" && t.Function % 2 == 0) throw new FormatException("异步等待请选择 Primary；Secondary 使用 Wait Reply 原事务匹配");
        if (s.Ceid.Length > 0 && (t.Stream != 6 || t.Function != 11 || !uint.TryParse(s.Ceid, out _))) throw new FormatException("CEID 条件仅支持 S6F11 的无符号整数");
        if (s.Expected.Length > 0 && !Regex.IsMatch(s.Expected, "^S[0-9]+F[0-9]+$")) throw new FormatException("预期回复格式为 S1F2");
        _ = Variables.Pairs(s.Parameters).ToList(); _ = Variables.Pairs(s.Extract).ToList(); _ = Variables.Pairs(s.Check).ToList();
    }
    public void Validate(Project p)
    {
        if (!p.Steps.Any(s => s.Enabled)) throw new FormatException("至少一个启用步骤");
        var known = Variables.Scope(p); Step? previous = null;
        foreach (var step in p.Steps.Where(s => s.Enabled))
        {
            var template = TemplateFor(p, step); ValidateStep(step, template);
            if (step.Kind == "Wait Reply")
            {
                if (previous?.Kind != "Send Primary") throw new FormatException("Wait Reply 必须紧随 Send Primary");
                var request = TemplateFor(p, previous);
                if (!request.W || template.Stream != request.Stream || template.Function != request.Function + 1) throw new FormatException("Wait Reply 必须为前一 Primary 的同 Stream F+1");
            }
            var scope = Variables.Scope(p, known, step.JobId, step.Parameters);
            if (step.Kind == "Send Primary") { using var msg = Variables.Message(template, scope); }
            foreach (var (name, path) in Variables.Pairs(step.Extract))
            {
                var response = step.Kind == "Send Primary" ? p.Library.FirstOrDefault(x => $"S{x.Stream}F{x.Function}" == (step.Expected.Length > 0 ? step.Expected : $"S{template.Stream}F{template.Function + 1}")) : template;
                var node = response?.Root?.At(path) ?? throw new FormatException("预校验回复提取需消息库内的预期回复模板");
                if (node.Type == "L") throw new FormatException("不能提取 List");
                if (step.Kind == "Send Primary" && !step.WaitReply) throw new FormatException("提取供后续步骤使用的变量时必须等待回复");
                known[name] = new() { Name = name, Type = node.Type, Value = node.Type == "A" ? "preflight" : "0" };
            }
            previous = step;
        }
    }
    public async Task ExecuteAsync(Project source)
    {
        if (Running) throw new InvalidOperationException("流程正在运行");
        var p = ProjectStore.Copy(source); Validate(p);
        if (!communication.CanSend) throw new InvalidOperationException("连接尚未 Selected");
        run = CancellationTokenSource.CreateLinkedTokenSource(communication.SessionToken); stopped = false;
        var token = run.Token; var flow = new Dictionary<string, Variable>(); var background = new List<Task>();
        var inbox = Channel.CreateUnbounded<Template>(); long session = communication.Generation;
        void Receive(TrafficRecord r)
        {
            if (r.Direction != "RX" || r.Session != session) return;
            try { var t = Sml.Parse(r.Sml)[0]; if (t.Function % 2 == 1 && t.Stream != 9) inbox.Writer.TryWrite(t); } catch (Exception ex) { log.Warning("Flow receive: " + ex.Message); }
        }
        log.TrafficLogged += Receive;
        Results.Clear(); StepStates = p.Steps.Select(s => s.Enabled ? "Pending" : "Skipped").ToArray(); State = "Running";
        var timer = Stopwatch.StartNew(); Changed?.Invoke(); Exception? asynchronousError = null;
        Task<Template?>? pendingReply = null;
        void Status(int index, string state) { StepStates[index] = state; Changed?.Invoke(); }
        void CheckReply(Step step, Template reply)
        {
            if (step.Expected.Length > 0 && step.Expected != $"S{reply.Stream}F{reply.Function}") throw new FormatException("预期回复不匹配");
            foreach (var (path, expected) in Variables.Pairs(step.Check)) if (reply.Root?.At(path).Value != expected) throw new FormatException($"业务结果校验失败：{path} 应为 {expected}");
            foreach (var (k, v) in Variables.Extract(reply.Root, Variables.Pairs(step.Extract).Select(x => new Extraction { Name = x.Key, Path = x.Value }))) flow[k] = v;
        }
        static bool TimeoutError(Exception e) => e is TimeoutException || e.Message.Contains("T3 Timeout", StringComparison.OrdinalIgnoreCase) || e is OperationCanceledException;
        try
        {
            for (int i = 0; i < p.Steps.Count; i++)
            {
                token.ThrowIfCancellationRequested(); var step = p.Steps[i]; if (!step.Enabled) continue;
                int index = i; CurrentStep = i + 1; var template = TemplateFor(p, step);
                Progress = $"当前第 {i + 1} / {p.Steps.Count} 条 · {step.Name} · {timer.Elapsed.TotalSeconds:F1}s"; log.Info("FLOW " + Progress); Status(i, "Running");
                if (step.Kind == "Wait Reply")
                {
                    var reply = await (pendingReply ?? throw new InvalidOperationException("缺少原事务")).WaitAsync(TimeSpan.FromSeconds(step.Timeout), token);
                    if (reply == null || reply.Stream != template.Stream || reply.Function != template.Function) throw new FormatException("原事务回复不匹配");
                    CheckReply(step, reply); pendingReply = null; Status(i, "Success");
                }
                else if (step.Kind == "Wait For Message")
                {
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(step.Timeout));
                    while (true)
                    {
                        var received = await inbox.Reader.ReadAsync(deadline.Token);
                        if (received.Stream != template.Stream || received.Function != template.Function || received.W != template.W) continue;
                        if (step.Ceid.Length > 0)
                        {
                            try { if (received.Root?.At(step.CeidPath).Value != step.Ceid) continue; } catch (Exception) { continue; }
                        }
                        CheckReply(step, received); break;
                    }
                    Status(i, "Success");
                }
                else
                {
                    var scope = Variables.Scope(p, flow, step.JobId, step.Parameters);
                    var sent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    var next = p.Steps.Skip(i + 1).FirstOrDefault(s => s.Enabled);
                    if (next?.Kind == "Wait Reply")
                    {
                        pendingReply = communication.SendAsync(template, scope, Math.Min(step.Timeout, next.Timeout), token, sent, flowLabel: $"FLOW Step {i + 1} · {step.Name}");
                        background.Add(pendingReply); await sent.Task.WaitAsync(token); Status(i, "Success");
                    }
                    else
                    {
                        async Task SendAndCheck()
                        {
                            try
                            {
                                var reply = await communication.SendAsync(template, scope, step.Timeout, token, sent, flowLabel: $"FLOW Step {index + 1} · {step.Name}");
                                if (reply != null) CheckReply(step, reply);
                                lock (Results) Results.Add(step.Name + "：成功"); Status(index, "Success");
                            }
                            catch (Exception e)
                            {
                                sent.TrySetException(e); lock (Results) Results.Add(step.Name + "：失败 " + e.Message);
                                Status(index, !token.IsCancellationRequested && TimeoutError(e) ? "Timeout" : "Failed"); log.Info(step.Name + " 失败：" + e.Message);
                                if (!step.ContinueOnFailure) { asynchronousError = e; run?.Cancel(); throw; }
                            }
                        }
                        var task = SendAndCheck();
                        if (step.WaitReply) await task;
                        else { background.Add(task); try { await sent.Task.WaitAsync(token); } catch when (step.ContinueOnFailure && !token.IsCancellationRequested) { } }
                    }
                }
                await Task.Delay(TimeSpan.FromSeconds(step.Interval), token);
            }
            await Task.WhenAll(background); State = "Completed"; Progress = $"流程完成 · {timer.Elapsed.TotalSeconds:F1}s";
        }
        catch (Exception e)
        {
            bool timeout = !token.IsCancellationRequested && TimeoutError(e);
            if (CurrentStep > 0 && StepStates[CurrentStep - 1] == "Running") Status(CurrentStep - 1, stopped ? "Skipped" : timeout ? "Timeout" : "Failed");
            run.Cancel(); State = stopped ? "Stopped" : "Failed";
            Progress = asynchronousError != null ? "流程失败：" + asynchronousError.Message : stopped ? "流程已停止 / 连接中断" : "流程失败：" + e.Message;
        }
        finally
        {
            log.TrafficLogged -= Receive; try { await Task.WhenAll(background); } catch { }
            log.Info(Progress); run.Dispose(); run = null; Changed?.Invoke();
        }
    }
}
