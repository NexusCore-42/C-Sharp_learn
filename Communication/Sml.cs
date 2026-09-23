using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Secs4Net;

namespace FastSim;

// FastSim SML dialect: quoted/unquoted headers, // comments, exact list counts,
// optional primitive counts, escaped quoted ASCII, typed scalar/array placeholders.
public static class Sml
{
    public static readonly string[] Types = ["L", "A", "B", "Boolean", "I1", "I2", "I4", "I8", "U1", "U2", "U4", "U8", "F4", "F8"];
    public static string NormalizeType(string type) => type.ToUpperInvariant() switch { "LIST" or "L" => "L", "BINARY" or "B" => "B", "BOOLEAN" => "Boolean", _ => type.ToUpperInvariant() };
    public static List<Template> Parse(string text)
    {
        var r = new Reader(text); var result = new List<Template>();
        while (!r.End)
        {
            var name = ""; var header = r.Token();
            if (r.Peek == ':') { r.Expect(':'); name = header; header = r.Token(); }
            var m = Regex.Match(header, "^S([0-9]+)F([0-9]+)$", RegexOptions.IgnoreCase);
            if (!m.Success || !byte.TryParse(m.Groups[1].Value, out var s) || s > 127 || !byte.TryParse(m.Groups[2].Value, out var f)) r.Fail("消息头必须为 S0–127 F0–255");
            s = byte.Parse(m.Groups[1].Value); f = byte.Parse(m.Groups[2].Value);
            bool w = r.Peek is 'W' or 'w'; if (w) r.Token();
            var root = r.Peek == '<' ? r.Node(0) : null;
            r.Expect('.');
            result.Add(new() { Name = name, Stream = s, Function = f, W = w, Root = root });
        }
        if (result.Count == 0) throw new FormatException("文件没有消息");
        return result;
    }
    public static string Write(Template t) => $"{Quote(t.Name)}: 'S{t.Stream}F{t.Function}'{(t.W ? " W" : "")}\r\n{(t.Root == null ? "" : WriteNode(t.Root, 0) + "\r\n")}.\r\n";
    public static string Quote(string s) => "'" + s.Replace("\\", "\\\\").Replace("'", "\\'").Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t") + "'";
    public static string WriteNode(Node n, int depth)
    {
        var pad = new string(' ', depth * 2);
        return n.Type == "L" ? $"{pad}<L [{n.Children.Count}]\r\n{string.Join("\r\n", n.Children.Select(x => WriteNode(x, depth + 1)))}\r\n{pad}>" : $"{pad}<{n.Type} {(n.Type == "A" ? Quote(n.Value) : n.Value)}>";
    }
    public static Item Build(Node n)
    {
        if (!Types.Contains(n.Type)) throw new FormatException($"不支持类型 {n.Type}");
        if (n.Type == "L")
        {
            if (n.Value.Length != 0) throw new FormatException("List 不能有标量值");
            var children = new List<Item>();
            try { foreach (var child in n.Children) children.Add(Build(child)); return Item.L(children); }
            catch { foreach (var child in children) child.Dispose(); throw; }
        }
        if (n.Children.Count != 0) throw new FormatException("非 List 节点不能有子项");
        var values = n.Value.Split([' ', ',', '\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries);
        var c = CultureInfo.InvariantCulture;
        byte Byte(string v) => v.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? byte.Parse(v[2..], NumberStyles.HexNumber, c) : byte.Parse(v, c);
        switch (n.Type)
        {
            case "A": if (n.Value.Any(x => x > 127)) throw new FormatException("ASCII 只允许 0–127 字符"); return Item.A(n.Value);
            case "B": return Item.B(values.Select(Byte));
            case "Boolean": return Item.Boolean(values.Select(v => v == "1" || v.Equals("true", StringComparison.OrdinalIgnoreCase) ? true : v == "0" || v.Equals("false", StringComparison.OrdinalIgnoreCase) ? false : throw new FormatException("Boolean 应为 true/false/1/0")));
            case "I1": return Item.I1(values.Select(v => sbyte.Parse(v, c)));
            case "I2": return Item.I2(values.Select(v => short.Parse(v, c)));
            case "I4": return Item.I4(values.Select(v => int.Parse(v, c)));
            case "I8": return Item.I8(values.Select(v => long.Parse(v, c)));
            case "U1": return Item.U1(values.Select(Byte));
            case "U2": return Item.U2(values.Select(v => ushort.Parse(v, c)));
            case "U4": return Item.U4(values.Select(v => uint.Parse(v, c)));
            case "U8": return Item.U8(values.Select(v => ulong.Parse(v, c)));
            case "F4": return Item.F4(values.Select(v => { var f = float.Parse(v, c); return float.IsFinite(f) ? f : throw new FormatException("浮点数应为有限值"); }));
            default: return Item.F8(values.Select(v => { var f = double.Parse(v, c); return double.IsFinite(f) ? f : throw new FormatException("浮点数应为有限值"); }));
        }
    }
    public static Node FromItem(Item item)
    {
        string Values<T>() where T : unmanaged, IEquatable<T> => string.Join(" ", item.GetMemory<T>().ToArray().Select(x => Convert.ToString(x, CultureInfo.InvariantCulture)));
        return item.Format switch
        {
            SecsFormat.List => new() { Children = item.Items.Select(FromItem).ToList() },
            SecsFormat.ASCII => new() { Type = "A", Value = item.GetString() },
            SecsFormat.Binary => new() { Type = "B", Value = Values<byte>() },
            SecsFormat.Boolean => new() { Type = "Boolean", Value = Values<bool>() },
            SecsFormat.I1 => new() { Type = "I1", Value = Values<sbyte>() },
            SecsFormat.I2 => new() { Type = "I2", Value = Values<short>() },
            SecsFormat.I4 => new() { Type = "I4", Value = Values<int>() },
            SecsFormat.I8 => new() { Type = "I8", Value = Values<long>() },
            SecsFormat.U1 => new() { Type = "U1", Value = Values<byte>() },
            SecsFormat.U2 => new() { Type = "U2", Value = Values<ushort>() },
            SecsFormat.U4 => new() { Type = "U4", Value = Values<uint>() },
            SecsFormat.U8 => new() { Type = "U8", Value = Values<ulong>() },
            SecsFormat.F4 => new() { Type = "F4", Value = Values<float>() },
            SecsFormat.F8 => new() { Type = "F8", Value = Values<double>() },
            _ => throw new FormatException($"不支持接收类型 {item.Format}")
        };
    }
    public static Template FromMessage(SecsMessage m) => new() { Name = m.Name ?? "", Stream = m.S, Function = m.F, W = m.ReplyExpected, Root = m.SecsItem == null ? null : FromItem(m.SecsItem) };
    private sealed class Reader(string source)
    {
        int pos;
        public void Fail(string reason) => throw new FormatException($"第 {source[..pos].Count(c => c == '\n') + 1} 行，第 {pos - source.LastIndexOf('\n', Math.Max(0, pos - 1), Math.Min(pos, source.Length))} 列：{reason}");
        void Skip() { while (pos < source.Length) { if (char.IsWhiteSpace(source[pos])) pos++; else if (source[pos..].StartsWith("//")) { while (pos < source.Length && source[pos] != '\n') pos++; } else break; } }
        public char Peek { get { Skip(); return pos == source.Length ? '\0' : source[pos]; } }
        public bool End => Peek == '\0';
        public void Expect(char c) { if (Peek != c) Fail($"预期 '{c}'"); pos++; }
        public string Token()
        {
            Skip(); if (End) Fail("意外文件结尾");
            if (Peek is '\'' or '"')
            {
                var q = source[pos++]; var b = new StringBuilder(); bool closed = false;
                while (pos < source.Length) { char c = source[pos++]; if (c == q) { closed = true; break; } if (c == '\\') { if (pos == source.Length) Fail("转义未结束"); c = source[pos++] switch { 'n' => '\n', 'r' => '\r', 't' => '\t', var v => v }; } b.Append(c); }
                if (!closed) Fail("引号未闭合"); return b.ToString();
            }
            int begin = pos;
            while (pos < source.Length && !char.IsWhiteSpace(source[pos]) && !"<>[]:".Contains(source[pos])) pos++;
            if (pos == begin) Fail("缺少值");
            return source[begin..pos];
        }
        public Node Node(int depth)
        {
            if (depth > 64) Fail("嵌套深度超过 64");
            Expect('<'); var n = new Node { Type = NormalizeType(Token()) }; if (n.Type.Equals("BOOLEAN", StringComparison.OrdinalIgnoreCase)) n.Type = "Boolean";
            if (!Types.Contains(n.Type)) Fail($"不支持类型 {n.Type}");
            int? count = null;
            if (Peek == '[') { Expect('['); if (!int.TryParse(Token(), out var i) || i < 0) Fail("长度无效"); count = i; Expect(']'); }
            if (n.Type == "L") { while (Peek == '<') n.Children.Add(Node(depth + 1)); if (count.HasValue && count != n.Children.Count) Fail("List 子项数量与声明不符"); }
            else if (n.Type == "A") n.Value = Peek == '>' ? "" : Token();
            else { var values = new List<string>(); while (Peek != '>') { if (End) Fail("节点未闭合"); values.Add(Token()); } n.Value = string.Join(" ", values); }
            Expect('>');
            if (n.Type != "L" && !n.Value.Contains("${"))
            {
                try { using var item = Build(n); if (count.HasValue && item.Count != count) Fail("数据数量与声明不符"); }
                catch (Exception e) { Fail(e.Message); }
            }
            return n;
        }
    }
}

