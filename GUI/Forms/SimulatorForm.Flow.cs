namespace FastSim;

public sealed partial class SimulatorForm
{
    FlowRunner? flowRunner;
    Project flowProject = new();
    string flowProjectPath = "";
    TabPage? flowPage;
    readonly ComboBox flowChoice = new() { Width = 235, DropDownStyle = ComboBoxStyle.DropDownList };
    readonly DataGridView flowGrid = new() { Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false, RowHeadersVisible = false, MultiSelect = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect, BackgroundColor = Color.White, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill };
    readonly Label flowStatus = new() { Dock = DockStyle.Top, Height = 30, Text = "Step 0 / 0 · Pending", Padding = new(4) };
    readonly TextBox flowPreview = CodeBox();
    readonly List<Control> flowEditControls = [];
    Button? flowRunButton, flowStopButton, flowResetButton;
    bool refreshingFlowView;
    FlowDefinition? SelectedFlow => flowChoice.SelectedIndex >= 0 && flowChoice.SelectedIndex < flowProject.Flows.Count ? flowProject.Flows[flowChoice.SelectedIndex] : null;
    int StepIndex => flowGrid.CurrentRow?.Index ?? -1;
    Control BuildFlowPanel()
    {
        var panel = new Panel { Dock = DockStyle.Fill };
        var top = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new(3) };
        void EditButton(string title, Action action) { var b = Dialogs.Button(title, () => Guard(action)); top.Controls.Add(b); flowEditControls.Add(b); }
        top.Controls.Add(flowChoice); flowEditControls.Add(flowChoice);
        EditButton("New Flow", () => { var name = Dialogs.Text(this, "Flow name", "New Flow"); if (string.IsNullOrWhiteSpace(name)) return; flowProject.Flows.Add(new() { Name = name.Trim() }); RefreshFlowList(flowProject.Flows.Count - 1); SaveFlowProject(); });
        EditButton("Save Project", SaveFlowProject);
        EditButton("Open Project", () => { using var dialog = new OpenFileDialog { Filter = "FastSim Project|*.xml" }; if (dialog.ShowDialog(this) == DialogResult.OK) LoadFlowProject(dialog.FileName); });
        EditButton("Save As…", () => { using var dialog = new SaveFileDialog { Filter = "FastSim Project|*.xml" }; if (dialog.ShowDialog(this) == DialogResult.OK) { flowProjectPath = dialog.FileName; SaveFlowProject(); } });
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, Padding = new(3) };
        void StepButton(string title, Action action) { var b = Dialogs.Button(title, () => Guard(action)); buttons.Controls.Add(b); flowEditControls.Add(b); }
        StepButton("+ Add Step", () => EditFlowStep(true)); StepButton("Edit Step", () => EditFlowStep(false));
        StepButton("Up", () => MoveFlowStep(-1)); StepButton("Down", () => MoveFlowStep(1));
        StepButton("Enable / Disable", () => { if (SelectedFlow is { } f && StepIndex >= 0) { f.Steps[StepIndex].Enabled = !f.Steps[StepIndex].Enabled; FlowEdited(); } });
        StepButton("Delete", () => { if (SelectedFlow is { } f && StepIndex >= 0) { f.Steps.RemoveAt(StepIndex); FlowEdited(); } });
        flowRunButton = Dialogs.Button("Run Flow", () => LiveAction(RunFlowAsync)); flowStopButton = Dialogs.Button("Stop", () => flowRunner?.Stop()); flowResetButton = Dialogs.Button("Reset", () => { flowRunner?.Reset(); RefreshFlowRows(); });
        buttons.Controls.Add(flowRunButton); buttons.Controls.Add(flowStopButton); buttons.Controls.Add(flowResetButton);
        foreach (var name in new[] { "#", "Template", "Action", "W", "Enabled", "Timeout", "Delay", "Status" }) flowGrid.Columns.Add(name, name);
        flowGrid.Columns[0].FillWeight = 30; flowGrid.Columns[2].FillWeight = 150; flowGrid.Columns[3].FillWeight = 30;
        flowGrid.CurrentCellChanged += (_, _) => { if (SelectedFlow is { } f && StepIndex >= 0 && StepIndex < f.Steps.Count) { var s = f.Steps[StepIndex]; flowPreview.Text = $"{s.Name}\r\nParameters: {s.Parameters}\r\nCEID: {s.Ceid}  Path: {s.CeidPath}\r\n" + Sml.Write(FlowRunner.TemplateFor(flowProject, s)); } };
        flowGrid.CellDoubleClick += (_, _) => { if (flowRunner?.Running != true) Guard(() => EditFlowStep(false)); };
        flowChoice.SelectedIndexChanged += (_, _) => { if (refreshingFlowView) return; flowRunner?.Reset(); RefreshFlowRows(); };
        var split = Split(Orientation.Horizontal, 325, new(660, 650)); split.Panel1MinSize = 100; split.Panel2MinSize = 80;
        split.Panel1.Controls.Add(flowGrid); split.Panel2.Controls.Add(Section("Selected Step · Parameters / SECS-II", flowPreview));
        panel.Controls.Add(split); panel.Controls.Add(flowStatus); panel.Controls.Add(top); panel.Controls.Add(buttons); return panel;
    }
    void InitializeFlow()
    {
        flowRunner = new(transport!, liveLog!);
        flowRunner.Changed += () => { if (IsHandleCreated && !IsDisposed) try { BeginInvoke(UpdateFlowUi); } catch (InvalidOperationException) { } };
        if (flowProjectPath.Length == 0) flowProjectPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FastSim", $"flows-{role}.xml");
        if (File.Exists(flowProjectPath))
        {
            try { LoadFlowProject(flowProjectPath); return; }
            catch (Exception ex)
            {
                liveLog!.Warning("Project load failed: " + ex.Message);
                // Preserve the failed source file: defaults must never overwrite it on exit.
                flowProjectPath = "";
                Shown += (_, _) => MessageBox.Show(this, ex.GetBaseException().Message + "\r\nOriginal file preserved. Use Save As to save the new workspace.", "Open Project Failed");
            }
        }
        flowProject = new() { Library = templates };
        var basic = new FlowDefinition { Name = "GEM Initialization" };
        var initialization = role == "EAP" ? new byte[] { 13, 14, 3, 4 } : new byte[] { 13, 14 };
        foreach (byte f in initialization)
        {
            string sender = f % 2 == 1 ? role : role == "EAP" ? "EQP" : "EAP";
            basic.Steps.Add(new() { Name = $"S1F{f}", TemplateId = DefaultCatalog.ForRole(templates, 1, f, sender).Id, Kind = f % 2 == 1 ? "Send Primary" : "Wait Reply" });
        }
        flowProject.Flows.Add(basic); RefreshFlowList();
    }
    void LoadFlowProject(string path)
    {
        if (flowRunner?.Running == true || connectionBusy) throw new InvalidOperationException("Stop Flow / wait for connection operation before loading a project");
        var loaded = ProjectStore.Load(path);
        if (transport?.IsOpen == true && loaded.SimulationRole.Length > 0 && loaded.SimulationRole != role) throw new InvalidOperationException("Close the connection before opening a project with a different role");
        if (loaded.Flows.Count == 0) loaded.Flows.Add(new() { Name = "Imported Sequence", Steps = loaded.Steps });
        flowProject = loaded; templates = loaded.Library; current = null; structure.SetRoot(null); BuildLibrary(); RefreshAutoReplies(); flowProjectPath = Path.GetFullPath(path);
        if (transport?.IsOpen != true) { config = ProjectStore.Copy(loaded.Connection); if (loaded.SimulationRole.Length > 0) role = loaded.SimulationRole; SyncRoleControls(); RefreshAutoReplies(); UpdateLiveState(); }
        RefreshFlowList(); if (templates.Count > 0) { SelectTemplate(templates[0]); SelectLibraryNode(templates[0]); }
        else { foreach (var field in editFields.Values) field.Clear(); xml.Clear(); UpdateButtons(); }
    }
    void SaveFlowProject()
    {
        if (!structure.TryCommitCell()) throw new FormatException(structure.EditError);
        if (flowProjectPath.Length == 0) return;
        flowProject.SimulationRole = role; flowProject.Connection = ProjectStore.Copy(config); ProjectStore.Save(flowProjectPath, flowProject); liveLog?.Info("Flow project saved: " + flowProjectPath);
    }
    void RefreshFlowList(int index = 0)
    {
        refreshingFlowView = true;
        try
        {
            flowChoice.Items.Clear(); flowChoice.Items.AddRange(flowProject.Flows.Select(f => (object)f.Name).ToArray());
            if (flowChoice.Items.Count > 0) flowChoice.SelectedIndex = Math.Clamp(index, 0, flowChoice.Items.Count - 1);
            flowRunner?.Reset();
        }
        finally { refreshingFlowView = false; }
        RefreshFlowRows();
    }
    void RefreshFlowRows()
    {
        refreshingFlowView = true;
        try
        {
            int selected = StepIndex; flowGrid.Rows.Clear();
            if (SelectedFlow is { } f) foreach (var s in f.Steps) { var t = FlowRunner.TemplateFor(flowProject, s); flowGrid.Rows.Add(flowGrid.Rows.Count + 1, (t.TemplateName.Length > 0 ? t.TemplateName : $"S{t.Stream}F{t.Function}"), s.Kind, t.W ? "W" : "—", s.Enabled ? "On" : "Off", s.Timeout, s.Interval, s.Enabled ? "Pending" : "Skipped"); }
            if (selected >= 0 && selected < flowGrid.Rows.Count) flowGrid.CurrentCell = flowGrid.Rows[selected].Cells[0];
        }
        finally { refreshingFlowView = false; }
        UpdateFlowUi();
    }
    void UpdateFlowUi()
    {
        if (flowRunner == null || refreshingFlowView) return;
        var steps = SelectedFlow?.Steps.ToArray() ?? [];
        // A queued timer/runner callback can arrive between model replacement and grid rebuild.
        // Capture the array once too: Reset may replace StepStates on another callback.
        var states = flowRunner.StepStates;
        flowStatus.Text = $"Step {flowRunner.CurrentStep} / {steps.Length} · {flowRunner.State}";
        foreach (var c in flowEditControls) c.Enabled = !flowRunner.Running && !connectionBusy;
        if (flowRunButton != null) flowRunButton.Enabled = !flowRunner.Running && !connectionBusy && transport?.CanSend == true && SelectedFlow?.Steps.Any(s => s.Enabled) == true;
        if (flowStopButton != null) flowStopButton.Enabled = flowRunner.Running;
        if (flowResetButton != null) flowResetButton.Enabled = !flowRunner.Running;
        if (flowGrid.RowCount != steps.Length) return;
        if (flowRunner.Running && flowRunner.CurrentStep > 0 && flowRunner.CurrentStep <= flowGrid.RowCount) flowGrid.CurrentCell = flowGrid.Rows[flowRunner.CurrentStep - 1].Cells[0];
        for (int i = 0; i < flowGrid.Rows.Count; i++)
        {
            var state = i < states.Length ? states[i] : steps[i].Enabled ? "Pending" : "Skipped";
            flowGrid.Rows[i].Cells[7].Value = state;
            flowGrid.Rows[i].DefaultCellStyle.BackColor = state == "Running" ? Color.LightGoldenrodYellow : state == "Success" ? Color.Honeydew : state is "Failed" or "Timeout" ? Color.MistyRose : Color.White;
        }
    }
    void FlowEdited() { flowRunner?.Reset(); RefreshFlowRows(); SaveFlowProject(); }
    void MoveFlowStep(int delta) { if (SelectedFlow is not { } f || StepIndex < 0) return; int from = StepIndex, to = from + delta; if (to < 0 || to >= f.Steps.Count) return; (f.Steps[from], f.Steps[to]) = (f.Steps[to], f.Steps[from]); FlowEdited(); flowGrid.CurrentCell = flowGrid.Rows[to].Cells[0]; }
    async Task RunFlowAsync()
    {
        if (flowRunner == null || SelectedFlow == null) return;
        var project = ProjectStore.Copy(flowProject); project.Steps = ProjectStore.Copy(SelectedFlow.Steps);
        SaveFlowProject(); await flowRunner.ExecuteAsync(project); DrainLive(); UpdateFlowUi();
    }
    void EditFlowStep(bool add)
    {
        if (SelectedFlow is not { } flow || !add && StepIndex < 0) return;
        if (add && templates.Count == 0) throw new InvalidOperationException("Add a Message to the Library first");
        int index = StepIndex;
        var step = add ? new Step { Name = current?.Name ?? "Step", TemplateId = current?.Id ?? templates[0].Id } : ProjectStore.Copy(flow.Steps[index]);

        using var dialog = Dialogs.Window("Flow Step · Runtime Message", 850, 760);
        var fields = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 2, AutoSize = true };
        fields.ColumnStyles.Add(new(SizeType.Absolute, 150)); fields.ColumnStyles.Add(new(SizeType.Percent, 100));
        void Row(string title, Control c) { int row = fields.RowCount++; fields.Controls.Add(new Label { Text = title, AutoSize = true, Padding = new(0, 5, 0, 0) }, 0, row); c.Dock = DockStyle.Fill; fields.Controls.Add(c, 1, row); }
        var name = new TextBox { Text = step.Name }; var kind = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList }; kind.Items.AddRange(["Send Primary", "Wait Reply", "Wait For Message"]); kind.SelectedItem = step.Kind;
        var templateBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList }; templateBox.Items.AddRange(flowProject.Library.Cast<object>().ToArray()); templateBox.SelectedItem = flowProject.Library.FirstOrDefault(t => t.Id == step.TemplateId);
        if (add && current != null) step.RuntimeTemplate = ProjectStore.Copy(current);
        var runtime = ProjectStore.Copy(FlowRunner.TemplateFor(flowProject, step)); if (templateBox.SelectedItem == null) { templateBox.Items.Add(runtime); templateBox.SelectedItem = runtime; } var tree = new StructureEditor(); tree.SetRoot(runtime.Root);
        var w = new CheckBox { Checked = runtime.W }; var enabled = new CheckBox { Checked = step.Enabled };
        var timeout = new NumericUpDown { DecimalPlaces = 3, Minimum = .001m, Maximum = 86400, Value = (decimal)Math.Min(step.Timeout, 86400) };
        var delay = new NumericUpDown { DecimalPlaces = 3, Maximum = 86400, Value = (decimal)step.Interval };
        var parameters = new TextBox { Text = step.Parameters, PlaceholderText = "RCMD=PPSELECT;PPID=XXX · Item value uses ${RCMD} / ${PPID}" };
        var ceid = new TextBox { Text = step.Ceid }; var ceidPath = new TextBox { Text = step.CeidPath };
        Row("Name", name); Row("Action", kind); Row("Library Template", templateBox); Row("W-Bit", w); Row("Enabled", enabled); Row("Timeout (seconds)", timeout); Row("Delay (seconds)", delay); Row("Parameters", parameters); Row("CEID (optional)", ceid); Row("CEID item path", ceidPath);
        void ParameterizeCommand()
        {
            if (runtime.Stream == 2 && runtime.Function == 41)
            {
                runtime.Root = Sml.Parse("'S2F41' W <L [2] <A '${RCMD}'> <L [1] <L [2] <A 'PPID'> <A '${PPID}'>>>> .")[0].Root;
                if (parameters.Text.Length == 0) parameters.Text = "RCMD=START;PPID=DEMO";
                tree.SetRoot(runtime.Root);
            }
        }
        if (add) ParameterizeCommand();
        templateBox.SelectedIndexChanged += (_, _) => { if (templateBox.SelectedItem is Template t) { runtime = ProjectStore.Copy(t); tree.SetRoot(runtime.Root); w.Checked = t.W; ParameterizeCommand(); } };
        var actions = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true };
        actions.Controls.Add(Dialogs.Button("Add Node", () => Guard(() => tree.AddNode(new() { Type = "A" })))); actions.Controls.Add(Dialogs.Button("Delete Node", tree.DeleteNode)); actions.Controls.Add(Dialogs.Button("Edit Node", () => tree.EditSelected(dialog)));
        actions.Controls.Add(Dialogs.Button("Apply", () => { try { if (!tree.TryCommitCell()) return; step.Name = name.Text; step.Kind = kind.Text; step.TemplateId = ((Template)templateBox.SelectedItem!).Id; runtime.Root = tree.Root; runtime.W = w.Checked; step.RuntimeTemplate = runtime; step.WaitReply = runtime.W; step.Enabled = enabled.Checked; step.Timeout = (double)timeout.Value; step.Interval = (double)delay.Value; step.Parameters = parameters.Text; step.Ceid = ceid.Text; step.CeidPath = ceidPath.Text; FlowRunner.ValidateStep(step, runtime); if (add) flow.Steps.Add(step); else flow.Steps[index] = step; FlowEdited(); dialog.DialogResult = DialogResult.OK; } catch (Exception e) { MessageBox.Show(dialog, e.Message); } }));
        actions.Controls.Add(Dialogs.Button("Cancel", () => dialog.DialogResult = DialogResult.Cancel));
        dialog.Controls.Add(tree); dialog.Controls.Add(fields); dialog.Controls.Add(actions); dialog.ShowDialog(this);
    }
}





