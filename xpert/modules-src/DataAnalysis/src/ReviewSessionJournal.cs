using System;
using System.IO;
using System.Text;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace HVX.PdReview
{
    // One append-only file per control session. No keystrokes or text-box
    // contents are collected. Painting/hover/drag-preview events are excluded.
    internal sealed class ReviewSessionJournal : IDisposable
    {
        internal static Func<string> DirectoryProvider=()=>Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"HVX","PdReview","OperationLogs");
        readonly object gate=new object();
        StreamWriter writer;
        bool closed;
        internal string FilePath {get;private set;}="";
        internal string Error {get;private set;}="";
        internal int Count {get;private set;}
        internal void Append(string action,string detail)
        {
            lock(gate)
            {
                if(closed)return;
                try
                {
                    if(writer==null)
                    {
                        string root=DirectoryProvider();Directory.CreateDirectory(root);
                        FilePath=Path.Combine(root,DateTime.Now.ToString("yyyyMMdd-HHmmss-fff",CultureInfo.InvariantCulture)+"-"+Guid.NewGuid().ToString("N")+".jsonl");
                        writer=new StreamWriter(new FileStream(FilePath,FileMode.CreateNew,FileAccess.Write,FileShare.Read),new UTF8Encoding(false));
                    }
                    // Explicit keys survive release obfuscation. Anonymous
                    // property names are implementation details and get renamed.
                    writer.WriteLine(new JavaScriptSerializer().Serialize(new Dictionary<string,object>{{"Sequence",Count+1},{"Time",DateTimeOffset.Now.ToString("o",CultureInfo.InvariantCulture)},{"Action",action},{"Detail",detail??""}}));
                    Count++;
                }
                catch(Exception ex){if(ex is OutOfMemoryException||ex is StackOverflowException)throw;Error=ex.GetType().Name+": "+ex.Message;}
            }
        }
        internal void Flush(){lock(gate){try{if(writer!=null)writer.Flush();}catch(IOException ex){Error=ex.Message;}catch(ObjectDisposedException){}}}
        internal string ReadPage(int page,int size)
        {
            Flush();lock(gate)
            {
                if(FilePath.Length==0)return "";
                using(var reader=new StreamReader(new FileStream(FilePath,FileMode.Open,FileAccess.Read,FileShare.ReadWrite)))
                {
                    int skip=Math.Max(0,page)*size;while(skip-->0&&reader.ReadLine()!=null){}
                    var lines=new List<string>();string line;while(lines.Count<size&&(line=reader.ReadLine())!=null)
                    {
                        var record=new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(line);
                        lines.Add(record["Sequence"]+"  "+record["Time"]+"  "+record["Action"]+Environment.NewLine+"    "+record["Detail"]);
                    }
                    return String.Join(Environment.NewLine,lines);
                }
            }
        }
        public void Dispose(){lock(gate){if(closed)return;Append("SessionClosed","");Flush();try{if(writer!=null)writer.Dispose();}catch(IOException ex){Error=ex.Message;}closed=true;}}
    }
    public sealed partial class PdReviewControl
    {
        readonly ReviewSessionJournal operationJournal=new ReviewSessionJournal();
        readonly HashSet<Control> journalControls=new HashSet<Control>();
        readonly HashSet<ToolStripItem> journalMenuItems=new HashSet<ToolStripItem>();
        Timer journalFlushTimer;
        void InitializeOperationJournal()
        {
            RecordOperation("SessionOpened","Control="+ControlBuildVersion);
            HookOperationControls(this);
            journalFlushTimer=new Timer{Interval=1000};journalFlushTimer.Tick+=(s,e)=>operationJournal.Flush();journalFlushTimer.Start();
            Disposed+=(s,e)=>{journalFlushTimer.Stop();journalFlushTimer.Dispose();operationJournal.Dispose();};
        }
        void RecordOperation(string action,string detail)
        {operationJournal.Append(action,detail);}
        string OperationContext()
        {return "Project="+projectPath+"; TreeFile="+analysisTree.SelectedDataPath+"; row="+selected+"; MAP="+pdmap.CurrentPdMapId+"; filter="+WaveformFilterMode;}
        void HookOperationControls(Control control)
        {
            if(!journalControls.Add(control))return;
            string name=String.IsNullOrEmpty(control.Name)?control.GetType().Name:control.Name;
            var button=control as ButtonBase;
            if(button!=null)button.Click+=(s,e)=>{if(!uiSync)RecordOperation("Command",name+"; "+button.Text+(button is CheckBox?"; checked="+((CheckBox)button).Checked:"")+"; "+OperationContext());};
            var combo=control as ComboBox;
            if(combo!=null)combo.SelectionChangeCommitted+=(s,e)=>RecordOperation("Selection",name+"; index="+combo.SelectedIndex+"; "+combo.Text);
            var number=control as NumericUpDown;
            if(number!=null)number.ValueChanged+=(s,e)=>{if(number.Focused&&!uiSync)RecordOperation("Value",name+"="+number.Value.ToString(CultureInfo.InvariantCulture));};
            var track=control as TrackBar;
            if(track!=null)track.MouseUp+=(s,e)=>RecordOperation("Slider",name+"="+track.Value);
            var tree=control as TreeView;
            if(tree!=null){tree.AfterSelect+=(s,e)=>RecordOperation("TreeSelect",e.Node.FullPath);tree.AfterCheck+=(s,e)=>{if(e.Action!=TreeViewAction.Unknown)RecordOperation("TreeCheck",e.Node.FullPath+"="+e.Node.Checked);};}
            if(control==pdmap||control==spectrum||control==locationOverview)
            {
                control.MouseUp+=(s,e)=>RecordOperation("PlotRelease",name+"; button="+e.Button+"; x="+e.X+"; y="+e.Y+"; "+OperationContext());
                control.MouseWheel+=(s,e)=>RecordOperation("PlotWheel",name+"; delta="+e.Delta+"; modifiers="+Control.ModifierKeys);
                control.MouseDoubleClick+=(s,e)=>RecordOperation("PlotDoubleClick",name+"; x="+e.X+"; y="+e.Y+"; "+OperationContext());
            }
            if(control.ContextMenuStrip!=null)HookJournalMenu(control.ContextMenuStrip.Items);
            control.ContextMenuStripChanged+=(s,e)=>{if(control.ContextMenuStrip!=null)HookJournalMenu(control.ContextMenuStrip.Items);};
            control.ControlAdded+=(s,e)=>HookOperationControls(e.Control);
            control.Disposed+=(s,e)=>journalControls.Remove(control);
            foreach(Control child in control.Controls)HookOperationControls(child);
        }
        void HookJournalMenu(ToolStripItemCollection items)
        {
            foreach(ToolStripItem item in items)
            {
                if(!journalMenuItems.Add(item))continue;
                item.Click+=(s,e)=>RecordOperation("Menu",item.Text+"; "+OperationContext());
                item.Disposed+=(s,e)=>journalMenuItems.Remove(item);
                var parent=item as ToolStripDropDownItem;if(parent!=null)HookJournalMenu(parent.DropDownItems);
            }
        }
    }
}
