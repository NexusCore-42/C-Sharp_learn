namespace FastSim;

public record MonitorMessage(string Type, string Direction, uint SystemBytes, DateTime Time, Template Template)
{
    public long Session { get; init; } public string FlowLabel { get; init; } = "";
    public string SF => $"S{Template.Stream}F{Template.Function}";
    public string Raw => Sml.Write(Template);
}
public sealed class MonitorTransaction(int number, uint systemBytes, long session = 0, string primaryDirection = "TX")
{
    public long Session { get; } = session;
    public string PrimaryDirection { get; } = primaryDirection;
    public string? Outcome { get; set; }
    public DateTime? Finished { get; set; }
    public int Number { get; } = number;
    public uint SystemBytes { get; } = systemBytes;
    public List<MonitorMessage> Messages { get; } = [];
    public MonitorMessage? Primary => Messages.FirstOrDefault(x => x.Type == "P");
    public MonitorMessage? Secondary => Messages.FirstOrDefault(x => x.Type == "S");
    public string Status => Outcome ?? (Secondary != null && Primary != null ? "SUCCESS" : Primary?.Template.W == true ? "WAITING" : Primary == null ? "UNMATCHED" : "SENT");
    public string ResponseTime => Primary != null && (Secondary != null || Finished != null) ? $"{Math.Max(0, ((Secondary?.Time ?? Finished!.Value) - Primary.Time).TotalMilliseconds):0} ms" : "—";
}


