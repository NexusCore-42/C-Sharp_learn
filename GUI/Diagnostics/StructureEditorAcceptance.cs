using System.Text.Json;
using System.ComponentModel;

namespace FastSim;

public sealed partial class SimulatorForm
{
    async Task<object> AuditStructureEditing()
    {
        if (flowRunner!.Running) throw new InvalidOperationException("Stop Flow before the editor audit");
        var checks = new List<string>();
        void Check(bool ok, string label) { if (!ok) throw new InvalidOperationException(label); checks.Add(label); }
        IEnumerable<Control> Desc(Control c) { foreach (Control child in c.Controls) { yield return child; foreach (var d in Desc(child)) yield return d; } }
        void Select(StructureEditor editor, Node n)
        {
            TreeNode? Find(TreeNode t) => ReferenceEquals(t.Tag,n) ? t : t.Nodes.Cast<TreeNode>().Select(Find).FirstOrDefault(x=>x!=null);
            editor.Tree.SelectedNode=editor.Tree.Nodes.Cast<TreeNode>().Select(Find).First(x=>x!=null); editor.Tree.SelectedNode!.EnsureVisible(); editor.Tree.Focus();
        }
        Control Input(StructureEditor editor) => Desc(editor).Single(c=>c.Name=="StructureCellEditor");
        void Key(Control c, Keys key) { var handle=c.Handle; SendMessage(handle,0x100,(IntPtr)(int)key,IntPtr.Zero); SendMessage(handle,0x101,(IntPtr)(int)key,IntPtr.Zero); }
        void Fill(StructureEditor editor, StructureEditor.EditField field, string value)
        {
            editor.BeginCellEdit(field); Input(editor).Text=value; Check(editor.TryCommitCell(),"Commit "+field+"="+value);
        }
        var model = new Node { Name="Root", Children=[
            new(){Name="DATAID",Type="U4",Value="1"}, new(){Name="CARRIERACTION",Type="A",Value="1"},
            new(){Name="CARRIERID",Type="A",Value="1"},new(){Name="PTN",Type="U1",Value="1"},
            new(){Name="Entries",Children=[new(){Name="Nested",Children=[new(){Name="Flag",Type="Boolean",Value="false"},new(){Name="Bytes",Type="B",Value="0"}]}]},
            new(){Name="TAIL",Type="A",Value="outside"} ] };
        CommitLibrary(p=>MessageLibrary.Add(p,new(){Stream=3,Function=17,TemplateName="EditorAcceptance",Root=model}),p=>p.Library.Last().Id);
        var edited=current!; var root=edited.Root!;
        ((TabControl)flowPage!.Parent!).SelectedIndex=0;
        Select(structure,root.Children[0]);
        int x=Math.Max(145,structure.Tree.ClientSize.Width*36/100)+20;
        int y=structure.Tree.SelectedNode!.Bounds.Y+10; IntPtr point=(IntPtr)((y<<16)|x);
        SendMessage(structure.Tree.Handle,0x201,(IntPtr)1,point);SendMessage(structure.Tree.Handle,0x202,IntPtr.Zero,point);
        Check(!structure.IsCellEditing,"Single click only selects a cell");
        SendMessage(structure.Tree.Handle,0x203,(IntPtr)1,point);
        Check(structure.IsCellEditing && structure.CurrentField==StructureEditor.EditField.Value,"Double click starts Value cell editor");
        Input(structure).Text="101"; Key(Input(structure),Keys.Enter);
        Check(root.Children[0].Value=="101" && ReferenceEquals(structure.Tree.SelectedNode?.Tag,root.Children[1]) && structure.IsCellEditing,"Enter commits DATAID and opens next Value");
        Input(structure).Text="Proceed";Key(Input(structure),Keys.Enter);
        Input(structure).Text="Carrier1";Key(Input(structure),Keys.Enter);
        Check(root.Children[1].Value=="Proceed" && root.Children[2].Value=="Carrier1" && ReferenceEquals(structure.Tree.SelectedNode?.Tag,root.Children[3]),"Continuous Enter edits three consecutive values");
        Input(structure).Text="300";Key(Input(structure),Keys.Enter);
        Check(root.Children[3].Value=="1" && structure.EditError.Length>0 && structure.IsCellEditing,"U1 overflow rejected without model mutation");
        Key(Input(structure),Keys.Escape);
        Check(!structure.IsCellEditing && root.Children[3].Value=="1","Esc cancels invalid input");
        Key(structure.Tree,Keys.F2); Check(structure.IsCellEditing,"F2 begins selected cell");
        Input(structure).Text="2";Key(Input(structure),Keys.Enter);
        Check(structure.Tree.SelectedNode?.Tag is Node{Name:"Flag"} && Input(structure) is ComboBox,"Enter skips LISTs and opens Boolean ComboBox");
        Input(structure).Text="true";Check(structure.TryCommitCell(),"Boolean edit commits");
        Select(structure,root.Children[0]);Fill(structure,StructureEditor.EditField.Name,"DATAID2");
        Fill(structure,StructureEditor.EditField.Type,"U1");
        structure.BeginCellEdit(StructureEditor.EditField.Type);((ComboBox)Input(structure)).SelectedItem="Boolean";
        Check(!structure.TryCommitCell() && root.Children[0].Type=="U1","Invalid type conversion keeps original type/value");structure.CancelCellEdit();
        Fill(structure,StructureEditor.EditField.Description,"长描述第一行\r\nsecond line 保留完整文本");
        Check(structure.Tree.SelectedNode!.ToolTipText.Contains("second line"),"Description tooltip preserves multiline text");
        Select(structure,root.Children[2]); structure.BeginCellEdit(StructureEditor.EditField.Value);structure.CancelCellEdit();
        SendMessage(structure.Tree.Handle,0x102,(IntPtr)'7',IntPtr.Zero);
        Check(structure.IsCellEditing && Input(structure).Text=="7","Typing starts selected Value editor");structure.CancelCellEdit();
        structure.BeginCellEdit(StructureEditor.EditField.Name);
        var tab=Message.Create(Input(structure).Handle,0x100,(IntPtr)(int)Keys.Tab,IntPtr.Zero);
        Input(structure).PreProcessMessage(ref tab);
        Check(structure.CurrentField==StructureEditor.EditField.Type,"Tab advances Name to Type");
        typeof(StructureEditor).GetMethod("ProcessCmdKey",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.Invoke(structure,new object[]{tab,Keys.Shift|Keys.Tab});
        Check(structure.CurrentField==StructureEditor.EditField.Name,"Shift+Tab moves Type back to Name");structure.CancelCellEdit();
        Select(structure,root);structure.BeginCellEdit(StructureEditor.EditField.Value);Check(!structure.IsCellEditing,"LIST Value remains read-only");
        var pg=Desc(structure).OfType<PropertyGrid>().Single();var props=TypeDescriptor.GetProperties(pg.SelectedObject!);
        Check(props["Value"]!.IsReadOnly && props["Child Count"]!.IsReadOnly && (int)props["Child Count"]!.GetValue(pg.SelectedObject)! == 6,"LIST Properties expose read-only count and Value");
        Select(structure,root.Children[0]);props=TypeDescriptor.GetProperties(pg.SelectedObject!);
        props["Value"]!.SetValue(pg.SelectedObject,"102");Check(root.Children[0].Value=="102","Properties edit writes model");
        try{props["Value"]!.SetValue(pg.SelectedObject,"300");throw new InvalidOperationException("Invalid property accepted");}catch(OverflowException){}
        Check(root.Children[0].Value=="102","Properties range validation rejects overflow");
        Select(structure,root.Children[4].Children[0].Children[1]);Fill(structure,StructureEditor.EditField.Value,"0xFF 0x00");
        Check(((Node)structure.Tree.SelectedNode!.Tag!).Value=="0xFF 0x00","Binary hex values use existing SECS parser");
        Select(structure,root.Children[3]);structure.AddNode(new(){Type="A",Value="new"});var added=(Node)structure.Tree.SelectedNode!.Tag!;
        Check(ReferenceEquals(root.Children[4],added) && structure.IsCellEditing,"Child Add Node inserts immediately after and starts editing");structure.CancelCellEdit();
        structure.AddList();var list=(Node)structure.Tree.SelectedNode!.Tag!;
        Check(ReferenceEquals(root.Children[5],list),"Child Add List inserts next sibling");structure.CancelCellEdit();
        structure.AddNode(new(){Type="U4",Value="9"});structure.CancelCellEdit();Check(list.Children.Count==1,"Selected LIST Add Node inserts child");
        Select(structure,list);structure.CopyNode();structure.PasteNode();structure.CancelCellEdit();
        Check(list.Children.Count==2 && list.Children[1].Children.Count==1,"LIST paste preserves nested copy");
        structure.DuplicateNode();var duplicate=(Node)structure.Tree.SelectedNode!.Tag!;duplicate.Children[0].Value="8";
        Check(list.Children[1].Children[0].Value=="9","Recursive duplicate is a deep copy");
        structure.MoveNode(-1);Check(ReferenceEquals(list.Children[1],duplicate),"Move Up stays in parent");
        structure.MoveNode(1);structure.DeleteNode();Check(list.Children.Count==2,"Move Down and Delete update model");

        // Real modal Subtree Editor. Timer interacts only with the newly opened dialog.
        async Task Subtree(Action<Form,StructureEditor> action)
        {
            Exception? failure=null;using var timer=new System.Windows.Forms.Timer {Interval=100};
            timer.Tick+=(_,_)=>
            {
                var dialog=Application.OpenForms.Cast<Form>().FirstOrDefault(f=>f.Text=="LIST / Subtree Editor");if(dialog==null)return;timer.Stop();
                try{action(dialog,Desc(dialog).OfType<StructureEditor>().Single());}catch(Exception e){failure=e;dialog.DialogResult=DialogResult.Cancel;}
            };
            Select(structure,list);timer.Start(); structure.EditSelected(this);if(failure!=null)throw failure;await Task.Delay(20);
        }
        string before=JsonSerializer.Serialize(list);
        await Subtree((dialog,sub)=>
        {
            Check(sub.Root!=list && sub.Root!.Children.Count==2 && sub.Root.Children[1].Children.Count==1,"Subtree root isolates full recursive descendants");
            Select(sub,sub.Root.Children[0]);Fill(sub,StructureEditor.EditField.Value,"44");dialog.DialogResult=DialogResult.Cancel;
        });Check(JsonSerializer.Serialize(list)==before,"Cancel discards un-applied subtree edits");
        await Subtree((dialog,sub)=>
        {
            var local=sub.Root!; Select(sub,local.Children[0]);Fill(sub,StructureEditor.EditField.Value,"55");
            Desc(dialog).OfType<Button>().Single(b=>b.Text=="Apply").PerformClick();
            Check(list.Children[0].Value=="55" && dialog.Visible,"Apply updates real subtree without closing");
            Fill(sub,StructureEditor.EditField.Value,"66"); Check(list.Children[0].Value=="55","After Apply draft remains isolated");
            dialog.DialogResult=DialogResult.Cancel;
        });Check(list.Children[0].Value=="55" && root.Children.Last().Value=="outside","Cancel keeps applied changes and leaves outer sibling untouched");
        await Subtree((dialog,sub)=>
        {
            Select(sub,sub.Root!.Children[1].Children[0]);Fill(sub,StructureEditor.EditField.Description,"nested saved");
            sub.AddList();sub.CancelCellEdit();sub.AddNode(new(){Name="Inside",Type="I2",Value="-1"});sub.CancelCellEdit();
            using(var image=new Bitmap(dialog.Width,dialog.Height)){dialog.DrawToBitmap(image,new Rectangle(Point.Empty,image.Size));image.Save(Path.Combine(Path.GetDirectoryName(flowProjectPath)!,"subtree.png"));}
            Desc(dialog).OfType<Button>().Single(b=>b.Text=="OK").PerformClick();
        });Check(list.Children[1].Children.Count==2 && list.Children[1].Children[1].Children[0].Name=="Inside","OK commits nested editing and structural additions");
        Select(structure,root.Children[3]);structure.BeginCellEdit(StructureEditor.EditField.Value);Input(structure).Text="3";
        SaveFlowProject();Check(root.Children[3].Value=="3" && !structure.IsCellEditing,"Save Project commits the still-active cell before serialization");
        var xmlText=LibraryCodec.WriteXml([edited]);var smlText=LibraryCodec.WriteSml([edited]);
        Check(LibraryCodec.WriteXml(LibraryCodec.ReadXml(xmlText))==xmlText && LibraryCodec.WriteXml(LibraryCodec.ReadSml(smlText))==xmlText,"XML and SML exports contain all committed edits");
        SaveFlowProject(); string id=edited.Id;LoadFlowProject(flowProjectPath);
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(flowProjectPath)!,"before-reload.xml"),xmlText);
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(flowProjectPath)!,"after-reload.xml"),LibraryCodec.WriteXml([templates.Single(t=>t.Id==id)]));
        Check(LibraryCodec.WriteXml([templates.Single(t=>t.Id==id)])==xmlText,"Project reload preserves every property, order and nested structure");
        return new {Ok=true,Checks=checks,TemplateId=id,Xml=xmlText,Path=flowProjectPath};
    }
}

