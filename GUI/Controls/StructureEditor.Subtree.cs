namespace FastSim;

public sealed partial class StructureEditor
{
    void EditSubtree(IWin32Window owner, Node target)
    {
        using var dialog = Dialogs.Window("LIST / Subtree Editor", 1000, 700);
        var editor = new StructureEditor { ScopeRootLocked = true };
        editor.SetRoot(target.Clone());
        var bar = Dialogs.Bar();
        bool Apply()
        {
            if (!editor.TryCommitCell()) return false;
            try
            {
                var saved = editor.Root?.Clone() ?? throw new FormatException("子树根 LIST 不能删除");
                if (saved.Type != "L") throw new FormatException("子树根必须保持 LIST");
                MessageLibrary.ValidateNode(saved);
                Assign(target, saved); target.Children = saved.Children;
                RefreshNodes(target); Changed?.Invoke(); return true;
            }
            catch (Exception ex) { MessageBox.Show(dialog, ex.Message, "Invalid subtree"); return false; }
        }
        bar.Controls.Add(Dialogs.Button("Apply", () => Apply()));
        bar.Controls.Add(Dialogs.Button("OK", () => { if (Apply()) dialog.DialogResult = DialogResult.OK; }));
        var cancel = Dialogs.Button("Cancel", () => dialog.DialogResult = DialogResult.Cancel); bar.Controls.Add(cancel); dialog.CancelButton = cancel;
        dialog.Controls.Add(editor); dialog.Controls.Add(bar);
        dialog.ShowDialog(owner);
    }
}
