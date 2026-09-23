using System.Collections.Concurrent;
using System.Threading.Channels;
using Secs4Net;

namespace FastSim;
public record TrafficRecord(DateTime Time, string Direction, string SF, bool W, string SystemBytes, string Name, string Result, string Sml)
{
    public long Session { get; init; } public string FlowLabel { get; init; } = "";
}
public sealed class SimLog : ISecsGemLogger, IAsyncDisposable
{
    public ConcurrentQueue<TrafficRecord> Traffic { get; } = new();
    public ConcurrentQueue<string> Trace { get; } = new();
    readonly Channel<string> disk = Channel.CreateBounded<string>(new BoundedChannelOptions(20000) { FullMode = BoundedChannelFullMode.DropOldest });
    readonly Task writer;
    public LogConfig Config { get; set; } = new();
    public string DirectoryPath { get; }
    public readonly AsyncLocal<string?> FlowLabel = new();
    public event Action<SecsMessage, int>? Sent;
    public event Action<TrafficRecord>? TrafficLogged;
    public event Action<string>? TraceLogged;
    public Func<long>? SessionProvider { get; set; }
    public SimLog(string path) { DirectoryPath = path; Directory.CreateDirectory(path); writer = Task.Run(WriteLoop); }
    public void Info(string text)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {text}"; Trace.Enqueue(line); while (Trace.Count > Config.UiLimit) Trace.TryDequeue(out _); disk.Writer.TryWrite(line); TraceLogged?.Invoke(line);
    }
    public void Debug(string msg) => Info(msg);
    public void Warning(string msg) => Info("警告 " + msg);
    public void Error(string msg, SecsMessage? message, Exception? ex) => Info("错误 " + msg + " " + ex?.Message);
    public void Error(string msg) => Info("错误 " + msg);
    public void MessageIn(SecsMessage msg, int id) => Record(msg, id, "RX");
    public void MessageOut(SecsMessage msg, int id) { Record(msg, id, "TX"); Sent?.Invoke(msg, id); }
    void Record(SecsMessage m, int id, string direction)
    {
        try
        {
            var sml = Sml.Write(Sml.FromMessage(m));
            var record = new TrafficRecord(DateTime.Now, direction, $"S{m.S}F{m.F}", m.ReplyExpected, $"{id:X8}", m.Name ?? "", m.F % 2 == 0 ? "应答" : m.ReplyExpected ? "等待事务应答" : "无回复要求", sml) { Session = SessionProvider?.Invoke() ?? 0, FlowLabel = FlowLabel.Value ?? "" };
            Traffic.Enqueue(record); TrafficLogged?.Invoke(record);
            while (Traffic.Count > Config.UiLimit) Traffic.TryDequeue(out _);
            disk.Writer.TryWrite($"{DateTime.Now:O} {direction} [{id:X8}] {sml}");
        }
        catch (Exception ex) { Info("记录报文失败：" + ex.Message); }
    }
    public void Outcome(int id, Template t, string result, long? session = null)
    {
        var record = new TrafficRecord(DateTime.Now, "结果", $"S{t.Stream}F{t.Function}", t.W, id == 0 ? "未分配" : $"{id:X8}", t.Name, result, "") { Session = session ?? SessionProvider?.Invoke() ?? 0 };
        Traffic.Enqueue(record); TrafficLogged?.Invoke(record);
        while (Traffic.Count > Config.UiLimit) Traffic.TryDequeue(out _);
        Info($"事务 [{id:X8}] {t.Name}：{result}");
    }
    async Task WriteLoop()
    {
        await foreach (var line in disk.Reader.ReadAllAsync())
        {
            try
            {
                var path = Path.Combine(DirectoryPath, "trace.log");
                if (File.Exists(path) && new FileInfo(path).Length > Config.FileMegabytes * 1024L * 1024)
                {
                    for (int i = Config.FileCount - 1; i >= 1; i--) { var older = path + "." + i; if (File.Exists(older)) File.Move(older, path + "." + (i + 1), true); }
                    File.Move(path, path + ".1", true); var extra = path + "." + Config.FileCount; if (File.Exists(extra)) File.Delete(extra);
                }
                await File.AppendAllTextAsync(path, line + Environment.NewLine);
            }
            catch (Exception e) { Trace.Enqueue("日志写盘失败：" + e.Message); }
        }
    }
    public async ValueTask DisposeAsync() { disk.Writer.TryComplete(); await writer; }
}

