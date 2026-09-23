using System.Xml;
using System.Xml.Serialization;
using Secs4Net;

namespace FastSim;

public class Node {
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string Type { get; set; } = "L";
    public string Value { get; set; } = "";
    public List<Node> Children { get; set; } = [];
    public Node Clone() => new() { Name = Name, Description = Description, Type = Type, Value = Value, Children = Children.Select(x => x.Clone()).ToList() };
    public Node At(string path)
    {
        var n = this;
        foreach (var p in path.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries)) n = n.Children[int.Parse(p)];
        return n;
    }
    public override string ToString() => Type == "L" ? $"L [{Children.Count}]" : $"{Type} {Value}";
}
public class Template
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "新命令";
    public string TemplateName { get; set; } = "";
    public string Direction { get; set; } = "";
    public string Role { get; set; } = "";
    public byte Stream { get; set; } = 1;
    public byte Function { get; set; } = 1;
    public bool W { get; set; } = true;
    public string Description { get; set; } = "";
    [XmlElement(IsNullable = true)] public Node? Root { get; set; }
    public override string ToString() => $"{(TemplateName.Length > 0 ? TemplateName : Name)} · S{Stream}F{Function}{(W ? " W" : "")}";
}
public class ConnectionConfig
{
    public bool Active { get; set; }
    public string IP { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 5000;
    public ushort DeviceId { get; set; }
    public bool Linktest { get; set; } = true;
    public double LinktestSeconds { get; set; } = 60;
    public double T3 { get; set; } = 45;
    public double T5 { get; set; } = 10;
    public double T6 { get; set; } = 5;
    public double T7 { get; set; } = 10;
    public double T8 { get; set; } = 5;
    public SecsGemOptions Options()
    {
        if (!System.Net.IPAddress.TryParse(IP, out var address) || address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) throw new FormatException("请输入 IPv4 地址（Secs4Net 3.1.0 当前使用 IPv4 Socket）");
        if (Port is < 1 or > 65535) throw new FormatException("端口应为 1–65535");
        static int Ms(double s) => double.IsFinite(s) && s >= .001 && s <= int.MaxValue / 1000d ? checked((int)(s * 1000)) : throw new FormatException("时间应为 0.001–2147483.647 秒");
        return new() { IsActive = Active, IpAddress = IP, Port = Port, DeviceId = DeviceId, T3 = Ms(T3), T5 = Ms(T5), T6 = Ms(T6), T7 = Ms(T7), T8 = Ms(T8), LinkTestInterval = Ms(LinktestSeconds) };
    }
}
public class Variable
{
    public string Name { get; set; } = "PJID";
    public string Type { get; set; } = "A";
    public string Value { get; set; } = "PJ001";
}
public class Job
{
    public string Kind { get; set; } = "PJ";
    public string Id { get; set; } = "PJ001";
    public string State { get; set; } = "Created";
    public string RecipeID { get; set; } = "R001";
    public string CarrierID { get; set; } = "C001";
    public string Slots { get; set; } = "1 2 3";
    public string ProcessJobs { get; set; } = "";
    public List<Variable> Properties { get; set; } = [];
}
public class JobPolicy
{
    public string States { get; set; } = "Created,Running,Completed,Aborted";
    public string Transitions { get; set; } = "Created>Running,Created>Aborted,Running>Completed,Running>Aborted";
    public bool AllowRecipeChangeWhileRunning { get; set; }
}
public class Extraction
{
    public string Name { get; set; } = "PJID";
    public string Path { get; set; } = "0";
}
public class Rule
{
    public bool Enabled { get; set; } = true;
    public byte Stream { get; set; } = 1;
    public byte Function { get; set; } = 1;
    public bool W { get; set; } = true;
    public string ConditionPath { get; set; } = "";
    public string ConditionValue { get; set; } = "";
    public string ReplyId { get; set; } = "";
    public string AllowedControl { get; set; } = "Offline,Local,Remote";
    public string RejectedReplyId { get; set; } = "";
    public List<Extraction> Extract { get; set; } = [];
    public bool SaveJobs { get; set; }
}
public class FlowDefinition { public string Id { get; set; } = Guid.NewGuid().ToString("N"); public string Name { get; set; } = "New Flow"; public List<Step> Steps { get; set; } = []; }
public class Step
{
    public string Name { get; set; } = "发送";
    public string Kind { get; set; } = "Send Primary";
    public bool Enabled { get; set; } = true;
    public Template? RuntimeTemplate { get; set; }
    public string Ceid { get; set; } = "";
    public string CeidPath { get; set; } = "1";
    public string TemplateId { get; set; } = "";
    public string Parameters { get; set; } = "";
    public string JobId { get; set; } = "";
    public bool WaitReply { get; set; } = true;
    public string Expected { get; set; } = "";
    public double Timeout { get; set; } = 45;
    public double Interval { get; set; } = 0;
    public string Extract { get; set; } = "";
    public string Check { get; set; } = "";
    public bool ContinueOnFailure { get; set; }
}
public class LogConfig
{
    public int UiLimit { get; set; } = 3000;
    public int FileMegabytes { get; set; } = 10;
    public int FileCount { get; set; } = 5;
}
[XmlRoot("FastSimProject")]
public class Project
{
    public string SimulationRole { get; set; } = "";
    [XmlAttribute] public int Version { get; set; } = 1;
    public ConnectionConfig Connection { get; set; } = new();
    public string Control { get; set; } = "Remote";
    public List<Template> Library { get; set; } = [];
    public List<Variable> Variables { get; set; } = [];
    public List<Job> Jobs { get; set; } = [];
    public JobPolicy JobPolicy { get; set; } = new();
    public List<Rule> Rules { get; set; } = [];
    public List<Step> Steps { get; set; } = [];
    public List<FlowDefinition> Flows { get; set; } = [];
    public LogConfig Logs { get; set; } = new();
    [XmlAnyElement] public XmlElement[]? UnknownElements { get; set; }
    [XmlAnyAttribute] public XmlAttribute[]? UnknownAttributes { get; set; }
    public static Project Demo()
    {
        var p = new Project();
        p.Library = Sml.Parse("AreYouThere: 'S1F1' W .\nIdentity: 'S1F2' <L [2] <A 'FastSim'> <A '1.0'>> .\nDemoJob: 'S3F1' W <L [3] <A '${PJID}'> <A '${CJID}'> <A '${RecipeID}'>> .\nDemoJobAck: 'S3F2' <B 0> .\nDemoRejected: 'S3F2' <B 1> .");
        p.Library.ForEach(t => t.Description = "演示配置；作业报文编号及 ACK 仅供回环测试，非 E40/E94 定义");
        p.Variables = [new(), new() { Name = "CJID", Value = "CJ001" }, new() { Name = "RecipeID", Value = "R001" }];
        p.Rules = [new() { ReplyId = p.Library[1].Id }, new() { Stream = 3, Function = 1, ReplyId = p.Library[3].Id, RejectedReplyId = p.Library[4].Id, AllowedControl = "Remote", SaveJobs = true, Extract = [new() { Name = "PJID", Path = "0" }, new() { Name = "CJID", Path = "1" }, new() { Name = "RecipeID", Path = "2" }] }];
        p.Steps = [new() { Name = "查询身份", TemplateId = p.Library[0].Id, Expected = "S1F2" }, new() { Name = "演示作业", TemplateId = p.Library[2].Id, Expected = "S3F2", Check = "/=0" }];
        return p;
    }
}





