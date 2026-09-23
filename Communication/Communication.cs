using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using Secs4Net;

namespace FastSim;
public sealed class PendingRequest(PrimaryMessageWrapper wrapper, long generation, double timeout)
{
    internal PrimaryMessageWrapper Wrapper { get; } = wrapper;
    internal SemaphoreSlim Gate { get; } = new(1, 1);
    internal long Generation { get; } = generation;
    public DateTime Expires { get; } = DateTime.UtcNow.AddSeconds(timeout);
    public int Id => Wrapper.Id;
    public Template Message { get; } = Sml.FromMessage(wrapper.PrimaryMessage);
    public bool Finished { get; internal set; }
    public override string ToString() => $"{Id:X8} · {Message} · 到期 {Expires.ToLocalTime():HH:mm:ss}";
}
public sealed class Communication(SimLog log) : IAsyncDisposable
{
    HsmsConnection? connection;
    SecsGem? gem;
    CancellationTokenSource? lifetime;
    CancellationTokenSource selected = new();
    Task? receive;
    long generation;
    readonly SemaphoreSlim lifecycle = new(1, 1);
    readonly ConcurrentDictionary<long, Task> handlers = new();
    long handlerNumber;
    public long Generation => Interlocked.Read(ref generation);
    public string LocalEndPoint => connection?.LocalEndPoint ?? "—";
    public string RemoteEndPoint => connection?.RemoteEndPoint ?? "—";
    public bool Connected => connection?.State is ConnectionState.Connected or ConnectionState.Selected;
    public Task LinkTestAsync() => connection?.SendLinkTestAsync(SessionToken) ?? throw new InvalidOperationException("连接未打开");
    public ConcurrentDictionary<int, PendingRequest> Pending { get; } = new();
    public Func<PendingRequest, Task>? PrimaryReceived { get; set; }
    public event Action? Changed;
    public bool IsOpen => connection != null;
    public bool CanSend => connection?.State == ConnectionState.Selected;
    public string State => connection == null ? "已关闭" : connection.State switch { ConnectionState.Selected => "Selected", ConnectionState.Connected => "Connected", _ => connection.IsActive ? "连接中 / 重试" : "监听 / 重试" };
    public CancellationToken SessionToken => selected.Token;
    public async Task OpenAsync(ConnectionConfig config)
    {
        await lifecycle.WaitAsync();
        try
        {
            if (IsOpen) return;
            var options = Options.Create(config.Options()); lifetime = new();
            log.SessionProvider = () => Generation;
            await Task.Run(() =>
            {
                connection = new(options, log) { LinkTestEnabled = config.Linktest };
                gem = new(options, connection, log);
                connection.ConnectionChanged += (_, state) =>
                {
                    Interlocked.Increment(ref generation);
                    selected.Cancel(); selected = new();
                    InvalidatePending(); log.Info("连接状态：" + state); Changed?.Invoke();
                };
                connection.Start(lifetime.Token);
            });
            receive = Task.Run(() => ReceiveLoop(gem!, config.T3, lifetime.Token)); Changed?.Invoke();
        }
        catch { await CloseCore(); throw; }
        finally { lifecycle.Release(); }
    }
    async Task ReceiveLoop(SecsGem local, double timeout, CancellationToken ct)
    {
        try
        {
            await foreach (var incoming in local.GetPrimaryMessageAsync(ct).ConfigureAwait(false))
            {
                var pending = new PendingRequest(incoming, Interlocked.Read(ref generation), timeout);
                if (incoming.PrimaryMessage.ReplyExpected && !Pending.TryAdd(incoming.Id, pending))
                {
                    log.Warning($"Duplicate inbound pending System Bytes {incoming.Id:X8}; original request retained");
                    incoming.PrimaryMessage.Dispose(); continue;
                }
                // Dispatch application handlers independently. A delayed response must
                // not block reception or auto-response of other bidirectional primaries.
                var handlerId = Interlocked.Increment(ref handlerNumber);
                var task = Task.Run(async () =>
                {
                    try { if (PrimaryReceived != null) await PrimaryReceived(pending); }
                    catch (Exception e) { log.Info("自动处理失败，保留待回复请求：" + e.Message); }
                    finally { if (!incoming.PrimaryMessage.ReplyExpected) incoming.PrimaryMessage.Dispose(); }
                });
                handlers[handlerId] = task;
                _ = task.ContinueWith(completed => handlers.TryRemove(handlerId, out _), TaskScheduler.Default);
                Changed?.Invoke();
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception e) { log.Info("接收循环失败：" + e.Message); }
    }
    public void ExpirePending()
    {
        foreach (var p in Pending.Values.Where(p => p.Expires <= DateTime.UtcNow))
            if (p.Gate.Wait(0)) { try { if (Pending.TryRemove(p.Id, out _)) { p.Finished = true; p.Wrapper.PrimaryMessage.Dispose(); log.Info($"待回复请求 {p.Id:X8} 已过期"); } } finally { p.Gate.Release(); } }
    }
    void InvalidatePending()
    {
        // Mark synchronously; release message memory after any active reply exits.
        foreach (var p in Pending.Values) { p.Finished = true; _ = ReleasePending(p); }
        Pending.Clear();
    }
    static async Task ReleasePending(PendingRequest p) { await p.Gate.WaitAsync(); try { p.Wrapper.PrimaryMessage.Dispose(); } finally { p.Gate.Release(); } }
    public async Task<bool> ReplyAsync(PendingRequest p, Template reply, IReadOnlyDictionary<string, Variable> scope, CancellationToken ct = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, SessionToken);
        await p.Gate.WaitAsync(linked.Token);
        try
        {
            if (p.Finished || p.Generation != Interlocked.Read(ref generation) || DateTime.UtcNow >= p.Expires || !CanSend) throw new InvalidOperationException("请求已回复、过期或连接已变更");
            if (!p.Message.W || reply.W || reply.Stream != p.Message.Stream || reply.Function != p.Message.Function + 1 || reply.Function % 2 != 0) throw new FormatException("应答须匹配待处理请求的同 Stream、F+1、W=false");
            using var message = Variables.Message(reply, scope);
            var ok = await p.Wrapper.TryReplyAsync(message, linked.Token);
            p.Finished = true; Pending.TryRemove(p.Id, out _); p.Wrapper.PrimaryMessage.Dispose();
            log.Info($"{p.Id:X8} 原事务应答：{ok}"); Changed?.Invoke(); return ok;
        }
        finally { p.Gate.Release(); }
    }
    public async Task<Template?> SendAsync(Template t, IReadOnlyDictionary<string, Variable> scope, double timeout, CancellationToken ct, TaskCompletionSource? sent = null, int? systemBytes = null, string flowLabel = "")
    {
        if (!CanSend || gem == null) throw new InvalidOperationException("只有 Selected 状态可以发送");
        if (t.Function % 2 == 0) throw new InvalidOperationException("偶数应答必须选择待回复请求，通过原事务发送");
        var sessionToken = SessionToken;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, sessionToken);
        long session = Generation;
        linked.CancelAfter(TimeSpan.FromSeconds(timeout));
        using var message = Variables.Message(t, scope);
        int id = 0;
        void OnSent(SecsMessage m, int value) { if (ReferenceEquals(m, message)) { id = value; sent?.TrySetResult(); } }
        log.Sent += OnSent; var oldFlow = log.FlowLabel.Value; log.FlowLabel.Value = flowLabel;
        try
        {
            log.Info($"发送参数快照 {t.Name}：" + System.Text.Json.JsonSerializer.Serialize(scope.Values));
            using var reply = systemBytes.HasValue ? await gem.SendWithSystemBytesAsync(message, systemBytes.Value, linked.Token) : await gem.SendAsync(message, linked.Token);
            var result = reply == null ? null : Sml.FromMessage(reply);
            if (result != null && (result.Stream != t.Stream || result.Function != t.Function + 1 || result.W)) throw new FormatException("事务回复消息头不匹配");
            log.Outcome(id, t, result == null ? "SENT" : "SUCCESS", session); return result;
        }
        catch (Exception e) { sent?.TrySetException(e); bool timeoutFailure = e.Message.Contains("T3 Timeout", StringComparison.OrdinalIgnoreCase) || e is OperationCanceledException && !ct.IsCancellationRequested && !sessionToken.IsCancellationRequested; log.Outcome(id, t, timeoutFailure ? "TIMEOUT · T3/step timeout" : "ERROR · " + e.Message, session); throw; }
        finally { log.Sent -= OnSent; log.FlowLabel.Value = oldFlow; }
    }
    public async Task CloseAsync() { await lifecycle.WaitAsync(); try { await CloseCore(); } finally { lifecycle.Release(); } }
    async Task CloseCore()
    {
        selected.Cancel(); lifetime?.Cancel(); Interlocked.Increment(ref generation); InvalidatePending();
        if (connection != null) await connection.DisposeAsync();
        gem?.Dispose();
        if (receive != null) await receive;
        await Task.WhenAll(handlers.Values.ToArray());
        connection = null; gem = null; lifetime?.Dispose(); lifetime = null; receive = null; log.Info("端口已关闭"); Changed?.Invoke();
    }
    public async ValueTask DisposeAsync() { await CloseAsync(); selected.Dispose(); }

    public async Task SendFaultSecondaryAsync(Template t, int wrongSystemBytes, CancellationToken ct = default)
    {
        if (!CanSend || gem == null) throw new InvalidOperationException("Selected required");
        if (t.W || t.Function % 2 != 0) throw new InvalidOperationException("故障注入只接受 W=0 Secondary");
        log.Warning($"FAULT INJECTION: {t} SystemBytes={wrongSystemBytes:X8}，不占用待回复请求");
        using var m = Variables.Message(t, new Dictionary<string, Variable>());
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, SessionToken);
        using var response = await gem.SendWithSystemBytesAsync(m, wrongSystemBytes, linked.Token);
    }
}
public sealed class AutoResponder(Communication connection, SimLog log, Func<Project> project, object sync)
{
    public async Task Handle(PendingRequest request)
    {
        Rule? rule; Template? response; Dictionary<string, Variable> scope;
        lock (sync)
        {
            var p = project(); var m = request.Message;
            rule = p.Rules.FirstOrDefault(r => r.Enabled && r.Stream == m.Stream && r.Function == m.Function && r.W == m.W && (r.ConditionValue.Length == 0 || m.Root?.At(r.ConditionPath).Value == r.ConditionValue));
            if (rule == null) return;
            bool accepted = rule.AllowedControl.Split(',').Contains(p.Control);
            var id = accepted ? rule.ReplyId : rule.RejectedReplyId;
            var extracted = accepted ? Variables.Extract(m.Root, rule.Extract) : [];
            scope = Variables.Scope(p);
            if (extracted.ContainsKey("PJID") || extracted.ContainsKey("CJID"))
                foreach (var key in new[] { "PJID", "CJID", "RecipeID", "CarrierID", "Slots", "ProcessJobs" }) scope.Remove(key);
            // These are request-local values; never overwrite global variables.
            foreach (var (k, v) in extracted) scope[k] = v;
            if (accepted && rule.SaveJobs) JobManager.Capture(p, extracted);
            log.Info($"规则 S{m.Stream}F{m.Function}：{(accepted ? "接受" : "控制状态拒绝")}，提取 {extracted.Count} 个请求变量");
            response = p.Library.SingleOrDefault(x => x.Id == id); if (response != null) response = ProjectStore.Copy(response);
        }
        if (request.Message.W && response != null) await connection.ReplyAsync(request, response, scope);
    }
}

