namespace FastSim;

public sealed partial class SimulatorForm
{
    async Task<object> AuditLiveUi()
    {
        var checks = new List<string>();
        void Check(bool ok, string name) { if (!ok) throw new InvalidOperationException("UI audit: " + name); checks.Add(name); }
        Check(!mockMode && transport?.CanSend == true, "Live Selected state");
        Check(connectionSummary.Text.Contains("Connected: True") && connectionSummary.Text.Contains("Selected: True") && connectionSummary.Text.Contains("Local:") && connectionSummary.Text.Contains("Remote:"), "Actual connection metadata visible");
        Check(library.Nodes.Cast<TreeNode>().SelectMany(n => n.Nodes.Cast<TreeNode>()).All(n => n.Tag is Template t && t.Function % 2 == 1 && n.Nodes.Count > 0), "Primary +/- with nested Secondary");
        var transaction = items.First(t => t.Primary?.Template.Root != null && t.Secondary != null && t.Status == "SUCCESS");
        var root = transactions.Nodes.Cast<TreeNode>().Single(n => ReferenceEquals(n.Tag, transaction));
        root.Expand(); root.Nodes[1].ExpandAll(); transactions.SelectedNode = root.Nodes[1];
        RefreshTransaction(transaction);
        Check(root.Nodes[1].IsExpanded && ReferenceEquals(transactions.SelectedNode?.Tag, transaction.Primary), "Refresh preserves expanded structure and selected message");
        var secondary = transaction.Secondary!;
        var row = trace.Rows.Cast<DataGridViewRow>().Single(r => ReferenceEquals(r.Tag, secondary));
        trace.CurrentCell = row.Cells[0];
        Check(ReferenceEquals(selectedTransaction, transaction) && ReferenceEquals(transactions.SelectedNode?.Tag, secondary), "Trace synchronizes transaction selection");
        Check(detailFields["Type"].Text == "Secondary (S)" && detailFields["Direction"].Text == secondary.Direction && raw.Text == secondary.Raw && detailFields["System Bytes"].Text == secondary.SystemBytes.ToString("X8"), "Trace synchronizes real Secondary details and raw body");
        var expired = items.FirstOrDefault(t => t.PrimaryDirection == "RX" && t.Status.StartsWith("TIMEOUT"));
        if (expired != null)
        {
            selectedTransaction = expired;
            SelectLibraryNode(templates.Single(t => t.Stream == expired.Primary!.Template.Stream && t.Function == expired.Primary.Template.Function + 1));
            Check(!sendSecondary.Enabled, "Expired incoming request disables manual reply");
        }
        var previousSize = Size;
        try
        {
            Size = new(1100, 800); await Task.Delay(150);
            var bar = delayBox.Parent!;
            Check(bar.Controls.Cast<Control>().All(c => c.Bounds.Bottom <= bar.ClientSize.Height && c.Bounds.Right <= bar.ClientSize.Width), "Minimum window shows all connection controls without clipping");
            Check(detailFields.Values.All(c => c.Width >= 65 && c.Bottom <= c.Parent!.ClientSize.Height), "Minimum window keeps message metadata usable");
            Check(structure.Height > 60 && raw.Height > 40 && trace.Height > 60, "Minimum window retains editor, detail and Trace panes");
        }
        finally { Size = previousSize; }
        SelectLibraryNode(templates.Single(t => t.Stream == 2 && t.Function == 41));
        return new { Ok = true, Checks = checks };
    }
}
