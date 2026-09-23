using System.ComponentModel;
using System.Globalization;

namespace FastSim;

public sealed partial class StructureEditor
{
    // Native TreeView raises double-click only over its text, not owner-drawn cells.
    sealed class CellTreeView : TreeView
    {
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x0203)
            {
                long xy = m.LParam.ToInt64(); int x = (short)(xy & 65535), y = (short)((xy >> 16) & 65535);
                if ((HitTest(x,y).Location & TreeViewHitTestLocations.PlusMinus) == 0)
                { OnMouseDoubleClick(new MouseEventArgs(MouseButtons.Left,2,x,y,0)); return; }
            }
            base.WndProc(ref m);
        }
    }
    public enum EditField { Name, Type, Value, Description }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool ScopeRootLocked { get; set; }
    public EditField CurrentField { get; private set; } = EditField.Value;
    readonly PropertyGrid properties = new() { Dock = DockStyle.Fill, ToolbarVisible = false, HelpVisible = false, PropertySort = PropertySort.NoSort };
    readonly Label error = new() { Dock = DockStyle.Bottom, AutoSize = false, Height = 0, ForeColor = Color.Firebrick };
    readonly ToolTip nodeTip = new() { AutoPopDelay = 30000, InitialDelay = 500, ReshowDelay = 100 };
    string hoverText = "";
    Control? cellEditor;
    Node? editingNode;
    bool committing;
    public string EditError => error.Text;
    public bool IsCellEditing => cellEditor != null;

    void InitializeCellEditing(ToolStrip tools)
    {
        var split = new SplitContainer { Dock = DockStyle.Fill, Size = new(850, 400), SplitterDistance = 610, Panel1MinSize = 220, Panel2MinSize = 150 };
        Controls.Remove(Tree); Controls.Remove(header);
        split.Panel1.Controls.Add(Tree); split.Panel1.Controls.Add(header); split.Panel1.Controls.Add(error);
        split.Panel2.Controls.Add(properties); split.Panel2.Controls.Add(new Label { Dock = DockStyle.Top, Height = 23, Text = "Node Properties", BackColor = header.BackColor });
        Controls.Add(split); split.BringToFront();
        tools.Items.Add("Expand All", null, (_, _) => { if (TryCommitCell()) Tree.ExpandAll(); });
        tools.Items.Add("Collapse All", null, (_, _) => { if (TryCommitCell()) Tree.CollapseAll(); });
        tools.Items.Add("Properties", null, (_, _) => { if (TryCommitCell()) split.Panel2Collapsed = !split.Panel2Collapsed; });
        // Native node tips only cover the native text label; these also cover drawn cells.
        Tree.ShowNodeToolTips = false;
        Tree.MouseMove += (_, e) =>
        {
            var row = VisibleRows().FirstOrDefault(t => e.Y >= t.Bounds.Y && e.Y < t.Bounds.Y + Tree.ItemHeight);
            string tip = row?.Tag is Node n ? FieldAt(e.X) == EditField.Description ? n.Description : $"Name: {n.Name}\nType: {n.Type}\nValue: {n.Value}" : "";
            if (tip != hoverText) { hoverText = tip; nodeTip.SetToolTip(Tree, tip); }
        };
        Tree.AfterSelect += (_, _) => RefreshProperties();
        Tree.BeforeSelect += (_, e) => { if (!committing && !TryCommitCell()) e.Cancel = true; };
        Tree.BeforeCollapse += (_, e) => { if (!TryCommitCell()) e.Cancel = true; };
        Tree.MouseDown += (_, e) =>
        {
            if (e.Button != MouseButtons.Left || !TryCommitCell()) return;
            var hit = VisibleRows().FirstOrDefault(t => e.Y >= t.Bounds.Y && e.Y < t.Bounds.Y + Tree.ItemHeight);
            if (hit == null) return;
            Tree.SelectedNode = hit;
            CurrentField = FieldAt(e.X); Tree.Invalidate();
        };
        Tree.MouseDoubleClick += (_, e) =>
        {
            // Native +/- glyphs retain their normal expansion action.
            if (Tree.SelectedNode is { } row && e.X >= row.Bounds.X) BeginCellEdit(FieldAt(e.X));
        };
        Tree.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.F2) { BeginCellEdit(CurrentField); e.SuppressKeyPress = true; }
            else if (e.KeyCode == Keys.Enter) { BeginCellEdit(CurrentField); e.SuppressKeyPress = true; }
        };
        Tree.KeyPress += (_, e) => { if (!char.IsControl(e.KeyChar)) { BeginCellEdit(CurrentField, e.KeyChar.ToString()); e.Handled = true; } };
        Tree.Resize += (_, _) => { if (cellEditor != null && Tree.SelectedNode is { } row) cellEditor.Location = CellBounds(row, CurrentField).Location; header.Invalidate(); Tree.Invalidate(); };
        Tree.MouseWheel += (_, _) => { if (TryCommitCell()) Tree.Focus(); };
        Tree.DrawNode += (_, e) =>
        {
            if (e.Node?.IsSelected == true && cellEditor == null)
            {
                var rect = CellBounds(e.Node, CurrentField); rect.Inflate(-1, -1);
                if (rect.Width > 0) ControlPaint.DrawFocusRectangle(e.Graphics, rect);
            }
        };
    }

    EditField FieldAt(int x) => x >= DescriptionX ? EditField.Description : x >= ValueX ? EditField.Value : x >= ValueX - 58 ? EditField.Type : EditField.Name;
    IEnumerable<TreeNode> VisibleRows() { for (var t = Tree.TopNode; t != null; t = t.NextVisibleNode) yield return t; }
    bool Editable(Node n, EditField f) => !(f == EditField.Value && n.Type == "L") && !(f == EditField.Type && ScopeRootLocked && ReferenceEquals(n, Root));
    Rectangle CellBounds(TreeNode row, EditField f)
    {
        int left = f switch { EditField.Name => row.Bounds.X, EditField.Type => ValueX - 58, EditField.Value => ValueX, _ => DescriptionX };
        int right = f switch { EditField.Name => ValueX - 58, EditField.Type => ValueX, EditField.Value => DescriptionX, _ => Tree.ClientSize.Width - 2 };
        // Deeply indented names can use a wider overlay while editing.
        if (f == EditField.Name && right - left < 45) left = 20;
        return new(left, row.Bounds.Y, Math.Max(45, right - left), Tree.ItemHeight);
    }
    static string FieldValue(Node n, EditField f) => f switch { EditField.Name => n.Name, EditField.Type => n.Type, EditField.Value => n.Value, _ => n.Description };
    public void BeginCellEdit(EditField field, string? initial = null)
    {
        if (!TryCommitCell() || Tree.SelectedNode?.Tag is not Node n || !Editable(n, field)) return;
        CurrentField = field; editingNode = n; Tree.SelectedNode.EnsureVisible();
        var bounds = CellBounds(Tree.SelectedNode, field);
        if (field == EditField.Type || field == EditField.Value && n.Type == "Boolean")
        {
            var combo = new ComboBox { DropDownStyle = field == EditField.Type ? ComboBoxStyle.DropDownList : ComboBoxStyle.DropDown, Bounds = bounds };
            combo.Items.AddRange(field == EditField.Type ? Sml.Types : new[] { "true", "false" });
            combo.Text = FieldValue(n, field); cellEditor = combo;
        }
        else
        {
            var text = new TextBox { Text = FieldValue(n, field), Bounds = bounds, BorderStyle = BorderStyle.FixedSingle };
            if (field == EditField.Description) { text.Multiline = true; text.ScrollBars = ScrollBars.Vertical; text.Height = Math.Min(90, Math.Max(50, Tree.ClientSize.Height - bounds.Y)); }
            cellEditor = text;
        }
        cellEditor.Name = "StructureCellEditor";
        Tree.Controls.Add(cellEditor); cellEditor.BringToFront(); cellEditor.Focus();
        if (initial != null && cellEditor is not ComboBox { DropDownStyle: ComboBoxStyle.DropDownList }) cellEditor.Text = initial;
        if (cellEditor is TextBox box) { if (initial == null) box.SelectAll(); else box.SelectionStart = box.TextLength; }
        cellEditor.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Escape) { CancelCellEdit(); Tree.Focus(); e.SuppressKeyPress = true; }
            else if (e.KeyCode == Keys.Enter && !e.Shift) { FinishAndNavigate(false, false); e.SuppressKeyPress = true; }
        };
        var active = cellEditor;
        active.Leave += (_, _) => { if (IsHandleCreated && !IsDisposed) BeginInvoke(() => { if (ReferenceEquals(cellEditor, active) && !active.ContainsFocus) TryCommitCell(); }); };
    }
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData is Keys.Tab or (Keys.Shift | Keys.Tab) && (Tree.Focused || cellEditor?.ContainsFocus == true))
        {
            FinishAndNavigate(true, keyData.HasFlag(Keys.Shift)); return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }
    void FinishAndNavigate(bool tab, bool backwards)
    {
        var field = CurrentField; var row = Tree.SelectedNode;
        if (!TryCommitCell() || row == null) return;
        if (!tab && field != EditField.Value) { Tree.Focus(); return; }
        int index = (int)field;
        while (row != null)
        {
            if (tab)
            {
                index += backwards ? -1 : 1;
                if (index < 0 || index > 3) { row = backwards ? row.PrevVisibleNode : row.NextVisibleNode; index = backwards ? 3 : 0; }
            }
            else { row = row.NextVisibleNode; index = (int)EditField.Value; }
            if (row?.Tag is Node n && Editable(n, (EditField)index)) { Tree.SelectedNode = row; BeginCellEdit((EditField)index); return; }
        }
        Tree.Focus();
    }
    public bool TryCommitCell()
    {
        if (cellEditor == null || editingNode == null || committing) return true;
        var node = editingNode; string value = cellEditor.Text; var field = CurrentField;
        try
        {
            // Validate a detached candidate; invalid text never touches the live model.
            var candidate = Candidate(node, field, value);
            committing = true;
            CancelCellEdit();
            Assign(node, candidate); RefreshProperties(); Tree.Invalidate(); Changed?.Invoke();
            return true;
        }
        catch (Exception ex) { error.Text = $"{node.Type} / {field}: {ex.GetBaseException().Message}"; error.Height = 38; if (cellEditor != null) { cellEditor.BackColor = Color.MistyRose; cellEditor.Focus(); } return false; }
        finally { committing = false; }
    }
    Node Candidate(Node node, EditField field, string value)
    {
        if (!Editable(node, field)) throw new InvalidOperationException("此属性为只读");
        var candidate = node.Clone();
        switch (field)
        {
            case EditField.Name: candidate.Name = value; break;
            case EditField.Type: candidate.Type = value; if (value == "L") candidate.Value = ""; break;
            case EditField.Value: candidate.Value = value; break;
            case EditField.Description: candidate.Description = value; break;
        }
        MessageLibrary.ValidateNode(candidate); return candidate;
    }
    static void Assign(Node target, Node source)
    {
        target.Name = source.Name; target.Type = source.Type; target.Value = source.Value; target.Description = source.Description;
        // Single-cell edits retain child identities and ordering.
    }
    public void CancelCellEdit()
    {
        var old = cellEditor; cellEditor = null; editingNode = null;
        if (old != null) { Tree.Controls.Remove(old); old.Dispose(); }
        error.Text = ""; error.Height = 0;
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing) { CancelCellEdit(); nodeTip.Dispose(); }
        base.Dispose(disposing);
    }
    void RefreshProperties()
    {
        properties.SelectedObject = Tree.SelectedNode?.Tag is Node n ? new NodeProperties(this, n) : null;
        if (Tree.SelectedNode?.Tag is Node node) Tree.SelectedNode.ToolTipText = $"Name: {node.Name}\nType: {node.Type}\nValue: {node.Value}\nDescription: {node.Description}";
    }
    sealed class NodeProperties(StructureEditor owner, Node node) : CustomTypeDescriptor
    {
        public override PropertyDescriptorCollection GetProperties() => GetProperties(null);
        public override PropertyDescriptorCollection GetProperties(Attribute[]? attributes) => new(new PropertyDescriptor[] {
            new FieldProperty(owner,node,EditField.Name), new FieldProperty(owner,node,EditField.Type),
            new FieldProperty(owner,node,EditField.Value), new FieldProperty(owner,node,EditField.Description), new CountProperty(node) });
        public override object GetPropertyOwner(PropertyDescriptor? pd) => this;
    }
    sealed class FieldProperty(StructureEditor owner, Node node, EditField propertyField) : PropertyDescriptor(propertyField.ToString(), null)
    {
        public override Type ComponentType => typeof(NodeProperties);
        public override Type PropertyType => typeof(string);
        public override bool IsReadOnly => !owner.Editable(node,propertyField);
        public override object GetValue(object? component) => FieldValue(node,propertyField);
        public override bool CanResetValue(object component) => false;
        public override void ResetValue(object component) { }
        public override bool ShouldSerializeValue(object component) => false;
        public override TypeConverter Converter => propertyField==EditField.Type ? new OptionsConverter(Sml.Types) : propertyField==EditField.Value && node.Type=="Boolean" ? new OptionsConverter(["true","false"],false) : base.Converter;
        public override string Description => propertyField==EditField.Value && node.Type=="L" ? "LIST Value 为只读；使用 Add Node / Add List 修改子项。" : "修改后验证并同步真实节点。Description 支持在树单元格中多行编辑。";
        public override void SetValue(object? component, object? value)
        {
            if (!owner.TryCommitCell()) throw new FormatException(owner.EditError);
            var candidate=owner.Candidate(node,propertyField,Convert.ToString(value,CultureInfo.InvariantCulture)??"");
            Assign(node,candidate); owner.Tree.Invalidate(); owner.Changed?.Invoke(); owner.RefreshProperties();
        }
    }
    sealed class OptionsConverter(string[] values, bool exclusive=true) : StringConverter
    {
        public override bool GetStandardValuesSupported(ITypeDescriptorContext? context) => true;
        public override bool GetStandardValuesExclusive(ITypeDescriptorContext? context) => exclusive;
        public override StandardValuesCollection GetStandardValues(ITypeDescriptorContext? context) => new(values);
    }
    sealed class CountProperty(Node node) : PropertyDescriptor("Child Count", null)
    {
        public override Type ComponentType => typeof(NodeProperties);
        public override Type PropertyType => typeof(int);
        public override bool IsReadOnly => true;
        public override object GetValue(object? component) => node.Children.Count;
        public override bool CanResetValue(object component) => false;
        public override void ResetValue(object component) { }
        public override void SetValue(object? component, object? value) { }
        public override bool ShouldSerializeValue(object component) => false;
    }
}

