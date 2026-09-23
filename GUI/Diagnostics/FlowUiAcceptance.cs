namespace FastSim;
public sealed partial class SimulatorForm
{
    async Task<object> AuditFlowEditor()
    {
        if (SelectedFlow == null || flowRunner!.Running) throw new InvalidOperationException("Idle flow required");
        ((TabControl)flowPage!.Parent!).SelectedTab = flowPage;
        var checks = new List<string>();
        void Check(bool ok, string name) { if (!ok) throw new InvalidOperationException("Flow UI: " + name); checks.Add(name); }
        var saved = ProjectStore.Copy(SelectedFlow); var original = Sml.Write(templates.Single(t => t.Stream == 2 && t.Function == 41));
        try
        {
            SelectLibraryNode(templates.Single(t => t.Stream == 2 && t.Function == 41));
            int count = SelectedFlow.Steps.Count;
            using var timer = new System.Windows.Forms.Timer { Interval = 100 };
            timer.Tick += (_, _) =>
            {
                var dialog = Application.OpenForms.Cast<Form>().FirstOrDefault(f => f.Text == "Flow Step · Runtime Message"); if (dialog == null) return;
                timer.Stop();
                var fields = dialog.Controls.OfType<TableLayoutPanel>().Single();
                fields.Controls.OfType<TextBox>().Single(t => t.PlaceholderText.StartsWith("RCMD=")).Text = "RCMD=PPSELECT;PPID=UI_RECIPE";
                dialog.Controls.OfType<FlowLayoutPanel>().Single().Controls.OfType<Button>().Single(b => b.Text == "Apply").PerformClick();
            };
            timer.Start(); ((Button)flowEditControls.Single(c => c.Text == "+ Add Step")).PerformClick();
            Check(SelectedFlow.Steps.Count == count + 1 && SelectedFlow.Steps[^1].Parameters.Contains("UI_RECIPE"), "Add Step dialog applies per-step parameters");
            Check(Sml.Write(templates.Single(t => t.Stream == 2 && t.Function == 41)) == original && Sml.Write(SelectedFlow.Steps[^1].RuntimeTemplate!).Contains("${RCMD}"), "Runtime Item tree uses placeholders without mutating library");
            flowGrid.CurrentCell = flowGrid.Rows[count].Cells[0];
            ((Button)flowEditControls.Single(c => c.Text == "Up")).PerformClick();
            Check(SelectedFlow.Steps[count - 1].Parameters.Contains("UI_RECIPE"), "Up changes execution order");
            ((Button)flowEditControls.Single(c => c.Text == "Down")).PerformClick();
            Check(SelectedFlow.Steps[count].Parameters.Contains("UI_RECIPE"), "Down changes execution order");
            ((Button)flowEditControls.Single(c => c.Text == "Enable / Disable")).PerformClick();
            Check(!SelectedFlow.Steps[count].Enabled && flowGrid.Rows[count].Cells[7].Value?.ToString() == "Skipped", "Disable displays Skipped");
            ((Button)flowEditControls.Single(c => c.Text == "Delete")).PerformClick();
            Check(SelectedFlow.Steps.Count == count, "Delete removes selected step");
            Size old = Size;
            try { Size = new(1100, 800); await Task.Delay(100); Check(flowGrid.Height > 60 && flowRunButton!.Parent!.ClientRectangle.Contains(flowRunButton.Bounds), "Minimum window retains Flow grid and Run controls"); }
            finally { Size = old; }
            return new { Ok = true, Checks = checks };
        }
        finally { flowProject.Flows[flowChoice.SelectedIndex] = saved; FlowEdited(); }
    }
}
