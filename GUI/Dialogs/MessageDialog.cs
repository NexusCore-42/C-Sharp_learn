namespace FastSim;
public static class MessageDialog
{
    public static Template? Edit(IWin32Window owner, Template source)
    {
        var draft = ProjectStore.Copy(source);
        using var form = Dialogs.Window("Edit Message · Template / Protocol / Body", 900, 800);
        var fields = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 4, AutoSize = true };
        fields.ColumnStyles.Add(new(SizeType.Absolute, 115)); fields.ColumnStyles.Add(new(SizeType.Percent, 50)); fields.ColumnStyles.Add(new(SizeType.Absolute, 115)); fields.ColumnStyles.Add(new(SizeType.Percent, 50));
        int n = 0;
        void Field(string label, Control input) { int row = n / 2, col = n % 2 * 2; fields.RowCount = row + 1; input.Dock = DockStyle.Fill; fields.Controls.Add(new Label { Text = label, AutoSize = true, Padding = new(0, 5, 0, 0) }, col, row); fields.Controls.Add(input, col + 1, row); n++; }
        var templateName = new TextBox { Name = "TemplateName", Text = draft.TemplateName };
        var messageName = new TextBox { Name = "MessageName", Text = draft.Name };
        var stream = new NumericUpDown { Name = "Stream", Maximum = 127, Value = draft.Stream };
        var function = new NumericUpDown { Name = "Function", Maximum = 255, Value = draft.Function };
        var w = new CheckBox { Name = "WBit", Checked = draft.W };
        var kind = new ComboBox { Name = "MessageType", DropDownStyle = ComboBoxStyle.DropDownList }; kind.Items.AddRange(["Primary", "Secondary"]); kind.SelectedIndex = draft.Function % 2 == 1 ? 0 : 1;
        var direction = new ComboBox { Name = "Direction", Text = draft.Direction }; direction.Items.AddRange(["", "TX", "RX"]);
        var role = new ComboBox { Name = "Role", Text = draft.Role }; role.Items.AddRange(["", "EAP", "EQP"]);
        var description = new TextBox { Name = "Description", Text = draft.Description };
        var bodyKind = new ComboBox { Name = "BodyKind", DropDownStyle = ComboBoxStyle.DropDownList }; bodyKind.Items.AddRange(["No Body", "LIST", "Item"]); bodyKind.SelectedIndex = draft.Root == null ? 0 : draft.Root.Type == "L" ? 1 : 2;
        Field("Template Name", templateName); Field("Message Name", messageName); Field("Stream", stream); Field("Function", function); Field("W-Bit", w); Field("Type", kind); Field("Direction", direction); Field("Role", role); Field("Description", description); Field("Initial Body", bodyKind);
        var editor = new StructureEditor { Name = "BodyEditor" }; editor.SetRoot(draft.Root);
        string lastProtocol = MessageLibrary.ProtocolName(draft);
        void ProtocolChanged()
        {
            string next = $"S{stream.Value}F{function.Value}";
            if (templateName.Text.Length == 0 || System.Text.RegularExpressions.Regex.IsMatch(templateName.Text, "^" + lastProtocol + "(?:-[0-9]+)?$")) templateName.Text = next;
            lastProtocol = next; kind.SelectedIndex = function.Value % 2 == 1 ? 0 : 1;
        }
        stream.ValueChanged += (_, _) => ProtocolChanged(); function.ValueChanged += (_, _) => ProtocolChanged();
        kind.SelectedIndexChanged += (_, _) => { int parity = kind.SelectedIndex == 0 ? 1 : 0; if ((int)function.Value % 2 != parity) function.Value = function.Value < 255 ? function.Value + 1 : 254; if (parity == 0) w.Checked = false; };
        bodyKind.SelectedIndexChanged += (_, _) => editor.SetRoot(bodyKind.SelectedIndex == 0 ? null : new() { Type = bodyKind.SelectedIndex == 1 ? "L" : "A" });
        var actions = Dialogs.Bar();
        actions.Controls.Add(Dialogs.Button("Apply", () =>
        {
            try
            {
                if (!editor.TryCommitCell()) return;
                draft.TemplateName = templateName.Text.Trim(); draft.Name = messageName.Text; draft.Stream = (byte)stream.Value; draft.Function = (byte)function.Value; draft.W = w.Checked; draft.Direction = direction.Text; draft.Role = role.Text; draft.Description = description.Text; draft.Root = editor.Root;
                MessageLibrary.ValidateTemplate(draft); form.DialogResult = DialogResult.OK;
            }
            catch (Exception ex) { MessageBox.Show(form, ex.Message, "Invalid Message"); }
        }));
        actions.Controls.Add(Dialogs.Button("Cancel", () => form.DialogResult = DialogResult.Cancel));
        form.Controls.Add(editor); form.Controls.Add(fields); form.Controls.Add(actions);
        return form.ShowDialog(owner) == DialogResult.OK ? draft : null;
    }
}

