namespace FastSim;

// Native TreeView retains its own expand/collapse glyphs and hierarchy lines.
// OwnerDrawText supplies the Value and Description columns, without a UI framework.
public sealed partial class StructureEditor : UserControl
{
    public TreeView Tree { get; } = new CellTreeView() { Dock = DockStyle.Fill, HideSelection = false, FullRowSelect = true, ShowLines = true, ShowPlusMinus = true, DrawMode = TreeViewDrawMode.OwnerDrawText, ItemHeight = 25, BorderStyle = BorderStyle.FixedSingle };
    readonly Panel header = new() { Dock = DockStyle.Top, Height = 25, BackColor = Color.FromArgb(236, 240, 244) };
    static Node? copiedNode;
    public Node? Root { get; private set; }
    public event Action? Changed;
    int ValueX => Math.Max(145, Tree.ClientSize.Width * 36 / 100);
    int DescriptionX => Math.Max(ValueX + 75, Tree.ClientSize.Width * 61 / 100);
    public StructureEditor()
    {
        Dock = DockStyle.Fill; Controls.Add(Tree); Controls.Add(header);
        var tools = new ToolStrip { Dock = DockStyle.Top, GripStyle = ToolStripGripStyle.Hidden };
        void Safe(Action action) { try { action(); } catch (Exception e) { MessageBox.Show(this, e.Message, "Structure Editor"); } }
        tools.Items.Add("Add Node", null, (_, _) => Safe(() => AddNode(new() { Type = "A" })));
        tools.Items.Add("Add List", null, (_, _) => Safe(AddList));
        tools.Items.Add("Edit", null, (_, _) => Safe(() => EditSelected(this)));
        Controls.Add(tools);
        var menu = new ContextMenuStrip(); Tree.ContextMenuStrip = menu;
        void Menu(string title, Action action) => menu.Items.Add(title, null, (_, _) => Safe(action));
        Menu("Add Node", () => AddNode(new() { Type = "A" })); Menu("Add List", AddList);
        Menu("Edit", () => EditSelected(this)); Menu("Duplicate", DuplicateNode); Menu("Copy", CopyNode); Menu("Paste", PasteNode); Menu("Delete", DeleteNode); Menu("Move Up", () => MoveNode(-1)); Menu("Move Down", () => MoveNode(1));
        Tree.NodeMouseClick += (_, e) => { if (e.Button == MouseButtons.Right) Tree.SelectedNode = e.Node; };
        menu.Opening += (_, _) => { bool list = Tree.SelectedNode?.Tag is Node { Type: "L" }; menu.Items[0].Text = list ? "Add Node" : "Add Node After"; menu.Items[1].Text = list ? "Add List" : "Add List After"; };
        header.Paint += (_, e) =>
        {
            TextRenderer.DrawText(e.Graphics, "Format", Font, new Point(9, 4), Color.Black);
            TextRenderer.DrawText(e.Graphics, "Value", Font, new Point(ValueX + 5, 4), Color.Black);
            TextRenderer.DrawText(e.Graphics, "Description", Font, new Point(DescriptionX + 5, 4), Color.Black);
            e.Graphics.DrawLine(Pens.Silver, ValueX, 0, ValueX, 25); e.Graphics.DrawLine(Pens.Silver, DescriptionX, 0, DescriptionX, 25);
        };
        Tree.DrawNode += (_, e) =>
        {
            if (e.Node?.Tag is not Node n) { e.DrawDefault = true; return; }
            bool selected = e.Node.IsSelected;
            var background = selected ? SystemColors.Highlight : Color.White;
            var foreground = selected ? SystemColors.HighlightText : Color.FromArgb(25, 40, 60);
            using var brush = new SolidBrush(background);
            e.Graphics.FillRectangle(brush, e.Bounds.X, e.Bounds.Y, Math.Max(0, Tree.ClientSize.Width - e.Bounds.X), Tree.ItemHeight);
            var flags = TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix;
            TextRenderer.DrawText(e.Graphics, n.Name, Font, new Rectangle(e.Bounds.X, e.Bounds.Y, Math.Max(0, ValueX - 58 - e.Bounds.X), Tree.ItemHeight), foreground, flags);
            TextRenderer.DrawText(e.Graphics, n.Type == "L" ? $"L ({n.Children.Count})" : n.Type, Font, new Rectangle(ValueX - 56, e.Bounds.Y, 55, Tree.ItemHeight), foreground, flags);
            TextRenderer.DrawText(e.Graphics, n.Value, Font, new Rectangle(ValueX + 5, e.Bounds.Y, DescriptionX - ValueX - 7, Tree.ItemHeight), foreground, flags);
            TextRenderer.DrawText(e.Graphics, Description(n), Font, new Rectangle(DescriptionX + 5, e.Bounds.Y, Math.Max(0, Tree.ClientSize.Width - DescriptionX - 7), Tree.ItemHeight), foreground, flags);
            e.Graphics.DrawLine(Pens.Gainsboro, ValueX, e.Bounds.Y, ValueX, e.Bounds.Y + Tree.ItemHeight);
            e.Graphics.DrawLine(Pens.Gainsboro, DescriptionX, e.Bounds.Y, DescriptionX, e.Bounds.Y + Tree.ItemHeight);
        };
        Resize += (_, _) => { header.Invalidate(); Tree.Invalidate(); };
        InitializeCellEditing(tools);
    }
    public string Description(Node n) => n.Description.Length > 0 ? n.Description : n.Type == "L" ? "List" : n.Value == "RCMD" ? "Parameter Name" : "Value";
    public void SetRoot(Node? root) { CancelCellEdit(); Root = root; RefreshNodes(root); }
    void RefreshNodes(Node? selected = null)
    {
        CancelCellEdit();
        var expanded = new HashSet<Node>();
        void Remember(TreeNode t) { if (t.IsExpanded && t.Tag is Node n) expanded.Add(n); foreach (TreeNode child in t.Nodes) Remember(child); }
        bool newRoot = Tree.Nodes.Count == 0 || !ReferenceEquals(Tree.Nodes[0].Tag, Root);
        foreach (TreeNode t in Tree.Nodes) Remember(t);
        Tree.BeginUpdate(); Tree.Nodes.Clear();
        TreeNode Build(Node n) { var t = new TreeNode(n.Type) { Tag = n, ToolTipText = $"Name: {n.Name}\nType: {n.Type}\nValue: {n.Value}\nDescription: {n.Description}" }; foreach (var c in n.Children) t.Nodes.Add(Build(c)); return t; }
        if (Root != null) Tree.Nodes.Add(Build(Root));
        void Restore(TreeNode t) { if (newRoot || t.Tag is Node n && expanded.Contains(n)) t.Expand(); foreach (TreeNode child in t.Nodes) Restore(child); }
        foreach (TreeNode t in Tree.Nodes) Restore(t);
        void Select(TreeNode t) { if (ReferenceEquals(t.Tag, selected)) Tree.SelectedNode = t; foreach (TreeNode c in t.Nodes) Select(c); }
        foreach (TreeNode t in Tree.Nodes) Select(t);
        Tree.EndUpdate(); Tree.SelectedNode?.EnsureVisible();
        RefreshProperties();
    }
    public void AddNode(Node node)
    {
        if (!TryCommitCell()) return;
        if (Root == null) Root = node;
        else if (Tree.SelectedNode?.Tag is Node { Type: "L" } parent) parent.Children.Add(node);
        else if (Tree.SelectedNode?.Tag is Node selected && Tree.SelectedNode.Parent?.Tag is Node container) container.Children.Insert(container.Children.IndexOf(selected) + 1, node);
        else if (Root.Type == "L" && Tree.SelectedNode == null) Root.Children.Add(node);
        else Root = new() { Children = [Root, node] };
        RefreshNodes(node); Tree.SelectedNode?.Expand(); Tree.Focus(); Changed?.Invoke(); BeginCellEdit(EditField.Name);
    }
    public void AddList() => AddNode(new() { Type = "L" });
    public void DuplicateNode()
    {
        if (!TryCommitCell() || Tree.SelectedNode?.Tag is not Node n || ScopeRootLocked && ReferenceEquals(n, Root)) return; var copy = n.Clone();
        if (Tree.SelectedNode.Parent?.Tag is Node parent) parent.Children.Insert(parent.Children.IndexOf(n) + 1, copy);
        else Root = new() { Children = [n, copy] };
        RefreshNodes(copy); Tree.Focus(); Changed?.Invoke();
    }
    public void CopyNode() { if (TryCommitCell() && Tree.SelectedNode?.Tag is Node n) copiedNode = n.Clone(); }
    public void PasteNode() { if (copiedNode != null) AddNode(copiedNode.Clone()); }
    public void MoveNode(int delta)
    {
        if (!TryCommitCell() || Tree.SelectedNode?.Tag is not Node n || Tree.SelectedNode.Parent?.Tag is not Node parent) return;
        int index = parent.Children.IndexOf(n), target = index + delta; if (target < 0 || target >= parent.Children.Count) return;
        (parent.Children[index], parent.Children[target]) = (parent.Children[target], parent.Children[index]); RefreshNodes(n); Changed?.Invoke();
    }
    public void DeleteNode()
    {
        if (!TryCommitCell() || Tree.SelectedNode?.Tag is not Node n || ScopeRootLocked && ReferenceEquals(n, Root)) return; Node? next = null;
        if (Tree.SelectedNode.Parent?.Tag is Node p) { int index = p.Children.IndexOf(n); p.Children.Remove(n); next = index > 0 ? p.Children[index - 1] : p; } else Root = null;
        RefreshNodes(next); Changed?.Invoke();
    }    public void EditSelected(IWin32Window owner)
    {
        if (!TryCommitCell() || Tree.SelectedNode?.Tag is not Node n) return;
        if (n.Type == "L") { EditSubtree(owner, n); return; }
        using var f = Dialogs.Window("Edit Node · Format / Value / Description", 650, 470);
        var type = new ComboBox { Dock = DockStyle.Top, DropDownStyle = ComboBoxStyle.DropDownList }; type.Items.AddRange(Sml.Types); type.SelectedItem = n.Type;
        var value = new TextBox { Dock = DockStyle.Fill, Multiline = true, Text = n.Value, ScrollBars = ScrollBars.Both };
        var description = new TextBox { Dock = DockStyle.Bottom, Text = n.Description, PlaceholderText = "Description" };
        var nodeName = new TextBox { Dock = DockStyle.Bottom, Text = n.Name, PlaceholderText = "Node Name" };
        var bar = Dialogs.Bar(); bar.Controls.Add(Dialogs.Button("Apply", () =>
        {
            try
            {
                var candidate = n.Clone(); candidate.Type = type.Text; candidate.Value = value.Text;
                MessageLibrary.ValidateNode(candidate);
                n.Type = candidate.Type; n.Value = candidate.Value; n.Name = nodeName.Text; n.Description = description.Text;
                RefreshNodes(n); Changed?.Invoke(); f.DialogResult = DialogResult.OK;
            }
            catch (Exception e) { MessageBox.Show(f, e.Message, "Invalid SECS-II node"); }
        })); bar.Controls.Add(Dialogs.Button("Cancel", () => f.DialogResult = DialogResult.Cancel));
        f.Controls.Add(value); f.Controls.Add(type); f.Controls.Add(description); f.Controls.Add(nodeName); f.Controls.Add(bar); f.ShowDialog(owner);
    }
}


