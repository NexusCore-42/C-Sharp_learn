namespace FastSim;

/// <summary>Public-reference SECS-II skeletons; values require equipment ICD configuration.</summary>
public static class DefaultCatalog
{
    public static List<Template> Load()
    {
        using var stream = typeof(DefaultCatalog).Assembly.GetManifestResourceStream("FastSim.SEMI.Library.xml")
            ?? throw new InvalidOperationException("Default SECS library resource is missing");
        using var reader = new StreamReader(stream);
        return LibraryCodec.ReadXml(reader.ReadToEnd());
    }

    public static Template ForRole(IEnumerable<Template> library, byte stream, byte function, string senderRole)
    {
        var candidates = library.Where(t => t.Stream == stream && t.Function == function).ToList();
        return candidates.FirstOrDefault(t => t.Role == senderRole)
            ?? candidates.FirstOrDefault(t => t.Role is "" or "Both")
            ?? throw new InvalidOperationException($"No S{stream}F{function} template for {senderRole}");
    }

    public static string StreamName(byte stream) => stream switch
    {
        1 => "Equipment Status", 2 => "Equipment Control", 3 => "Material / Carrier Management",
        4 => "Material Handoff", 5 => "Alarms / Exceptions", 6 => "Data Collection / Events",
        7 => "Process Programs", 8 => "Program Loading", 9 => "Protocol Errors",
        10 => "Terminal Services", 11 => "Obsolete File Services", 12 => "Wafer Maps",
        13 => "Data Set Transfer", 14 => "Object Services", 15 => "Recipe Management",
        16 => "Process / Control Job Management", _ => "Messages"
    };
}
