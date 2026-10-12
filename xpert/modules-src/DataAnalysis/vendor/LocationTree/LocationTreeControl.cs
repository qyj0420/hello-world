using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using HVX.ProjectModel;

namespace LabVIEWLocationTree
{
    [ToolboxItem(true)]
    public sealed class LocationTreeControl : UserControl
    {
        private readonly TreeDataModel model = new TreeDataModel();
        public bool AnalyzeZeroVoltageData
        {
            get{return model.AnalyzeZeroVoltageData;}
            set{if(model.AnalyzeZeroVoltageData==value)return;model.AnalyzeZeroVoltageData=value;if(!value)foreach(var record in model.Records.Where(r=>r.VoltageKv==0))record.Selected=false;Rebuild(false);}
        }
        private readonly TreeView tree = new NoHorizontalTreeView();
        private readonly Label selectionDetails = new Label();
        private readonly ImageList states = new ImageList();
        private readonly List<Bitmap> stateBitmaps = new List<Bitmap>();
        private readonly ContextMenuStrip displayMenu = new ContextMenuStrip();
        private readonly ToolStripMenuItem showRecordMenu = new ToolStripMenuItem();
        private readonly ToolStripMenuItem showAllRecordsMenu = new ToolStripMenuItem();
        private readonly ToolStripMenuItem deleteDataMenu = new ToolStripMenuItem();
        private readonly HashSet<TreeNode> multiSelectedNodes = new HashSet<TreeNode>();
        private string contextDataPath = "";
        private bool english;
        private bool suppressOutputNotify;
        private bool multiSelectClick;
        internal Func<string,string,string> LabelTranslator;
        private string TextLabel(string en,string zh){return LabelTranslator==null?(english?en:zh):LabelTranslator(en,zh);}
        private bool rebuilding;
        private readonly Dictionary<int,TreeNode> mapNodesByRow=new Dictionary<int,TreeNode>();
        internal long TreeRebuildCount,StateRefreshPassCount,StateRefreshNodeCount;
        private long treeSnapshotGeneration;
        internal bool SuppressArchiveNotifications;
        private readonly int ownerThread = System.Threading.Thread.CurrentThread.ManagedThreadId;
        private sealed class NodeInfo
        {
            internal int Kind, Phase;
            internal LocationRecord Record;
            internal MapRecord Map;
        }
        // Native TreeView measures the untrimmed path/status text to create a
        // horizontal scrollbar.  Keep vertical navigation, but render a
        // single-line ellipsis within the actual sidebar width instead.
        private sealed class NoHorizontalTreeView : TreeView
        {
            protected override CreateParams CreateParams
            {
                get { var cp=base.CreateParams;cp.Style|=0x8000;return cp; } // TVS_NOHSCROLL
            }
        }

        public LocationTreeControl()
        {
            Size = new Size(220, 540);
            BackColor = HVX.PdReview.ReviewTheme.Surface;
            ForeColor = HVX.PdReview.ReviewTheme.Ink;
            var ownedFont=HVX.PdReview.ReviewTheme.RegularFont();Font=ownedFont;Disposed+=(s,e)=>ownedFont.Dispose();
            tree.Name = "LocationTree";
            tree.BackColor = HVX.PdReview.ReviewTheme.Surface;
            tree.ForeColor = HVX.PdReview.ReviewTheme.Ink;
            tree.LineColor = HVX.PdReview.ReviewTheme.Border;
            tree.Dock = DockStyle.Fill;
            tree.BorderStyle = BorderStyle.None;
            tree.CheckBoxes = false;
            tree.HideSelection = false;
            tree.DrawMode = TreeViewDrawMode.OwnerDrawText;
            tree.DrawNode += DrawTreeNode;
            tree.ShowNodeToolTips = true;
            tree.ShowLines = true;
            tree.FullRowSelect = false;
            tree.Indent = 9;
            tree.ItemHeight = 25;
            states.ColorDepth = ColorDepth.Depth32Bit;
            RefreshStateImages(20);
            tree.StateImageList = states;
            tree.NodeMouseClick += HandleNodeClick;
            tree.MouseDown += HandleTreeMouseDown;
            tree.KeyDown += HandleKey;
            showRecordMenu.Click += delegate { RequestDisplayScope(contextDataPath); };
            showAllRecordsMenu.Click += delegate { RequestDisplayScope(""); };
            deleteDataMenu.Name="LocationTreeDelete";
            deleteDataMenu.Click += delegate { RequestDeleteSelectedDataFiles(); };
            displayMenu.Items.AddRange(new ToolStripItem[]{showRecordMenu,showAllRecordsMenu,new ToolStripSeparator(),deleteDataMenu});
            UpdateContextText();
            Controls.Add(tree);
            selectionDetails.Name="LocationSelectionDetails";selectionDetails.Dock=DockStyle.Bottom;
            selectionDetails.Height=88;selectionDetails.Padding=new Padding(6);
            selectionDetails.BackColor=HVX.PdReview.ReviewTheme.SurfaceAlt;
            selectionDetails.ForeColor=HVX.PdReview.ReviewTheme.Ink;
            selectionDetails.AutoEllipsis=false;
            Controls.Add(selectionDetails);
            tree.AfterSelect+=(s,e)=>UpdateSelectionDetails();
            SizeChanged+=(s,e)=>UpdateSelectionDetails();
            LastError = "";
            ResetOutputs();
            Rebuild(false);
        }

        void DrawTreeNode(object sender,DrawTreeNodeEventArgs e)
        {
            var bounds=new Rectangle(e.Bounds.Left,e.Bounds.Top,Math.Max(0,tree.ClientSize.Width-e.Bounds.Left-2),e.Bounds.Height);
            if(bounds.Width==0)return;
            bool selected=e.Node==tree.SelectedNode||multiSelectedNodes.Contains(e.Node);
            Color background=selected?HVX.PdReview.ReviewTheme.Active:tree.BackColor;
            using(var brush=new SolidBrush(background))e.Graphics.FillRectangle(brush,bounds);
            TextRenderer.DrawText(e.Graphics,e.Node.Text,e.Node.NodeFont??tree.Font,bounds,
                selected?Color.White:e.Node.ForeColor.IsEmpty?tree.ForeColor:e.Node.ForeColor,
                TextFormatFlags.NoPrefix|TextFormatFlags.VerticalCenter|TextFormatFlags.EndEllipsis|TextFormatFlags.SingleLine|TextFormatFlags.PreserveGraphicsClipping);
        }
        void UpdateSelectionDetails()
        {
            int multi=multiSelectedNodes.Count(IsDeletableLocationNode);
            if(multi>1)
            {
                selectionDetails.Text=TextLabel("Multi-selected: ","已复选：")+multi.ToString(CultureInfo.InvariantCulture)+
                    TextLabel(" location data files"," 个定位数据文件")+"\n"+TextLabel("Right-click → Delete","右键 → 删除");
                selectionDetails.AccessibleDescription=selectionDetails.Text;
                int multiWidth=Math.Max(50,selectionDetails.Width-selectionDetails.Padding.Horizontal);
                selectionDetails.Height=Math.Max(Font.Height*3+16,TextRenderer.MeasureText(selectionDetails.Text,Font,new Size(multiWidth,int.MaxValue),TextFormatFlags.WordBreak).Height+16);
                return;
            }
            TreeNode n=tree.SelectedNode;
            if(n==null){selectionDetails.Text=TextLabel("Select a record or pulse","请选择记录或脉冲");return;}
            NodeInfo info=Info(n);
            string title=info.Record==null?n.Text:PhaseName(info.Phase)+" · "+info.Record.VoltageKv.ToString("0.##",CultureInfo.InvariantCulture)+" kV · #"+info.Record.Sequence;
            string state=info.Map!=null?MapReviewCaption(info.Map):
                info.Record!=null?(!info.Record.Analyzed?TextLabel("Pending analysis","待分析"):RecordReviewCaption(info.Record)):"";
            selectionDetails.Text=TextLabel("Selected: ","当前选择：")+title+"\n"+(info.Map==null?"":n.Text+" · ")+state;
            selectionDetails.AccessibleDescription=n.ToolTipText;
            int width=Math.Max(50,selectionDetails.Width-selectionDetails.Padding.Horizontal);
            selectionDetails.Height=Math.Max(Font.Height*3+16,TextRenderer.MeasureText(selectionDetails.Text,Font,new Size(width,int.MaxValue),TextFormatFlags.WordBreak).Height+16);
        }

        [Category("Appearance"), DefaultValue(false)]
        public bool EnglishVersion
        {
            get { return Read(delegate { return english; }); }
            set { Run(delegate { english = value; UpdateContextText(); Rebuild(true); }); }
        }
        public void SetEnglishDisplay(bool value) { EnglishVersion = value; }
        public string LastError { get; private set; }
        public int SelectedNodeType { get; private set; }
        public int SelectedPdMapId { get; private set; }
        public int SelectedMatrixRowIndex { get; private set; }
        public double SelectedDataId { get; private set; }
        public string SelectedDataPath { get; private set; }
        public long ClickSequence { get; private set; }
        public long MatrixChangeSequence { get; private set; }
        public long SelectionSequence { get; private set; }
        public long DataChangeSequence { get; private set; }
        public int LastChangedMapId { get; private set; }
        public int LastChangedMatrixRowIndex { get; private set; }
        public bool LastChangedAccepted { get; private set; }
        public string RequestedDeleteDataPath { get; private set; }
        public string[] RequestedDeleteDataPaths { get; private set; }
        public string RequestedDisplayDataPath { get; private set; }
        public string ProjectPath { get { return Read(delegate { return model.ProjectPath; }); } }
        public string[] Warnings { get { return Read(delegate { return model.Warnings; }); } }
        public event EventHandler SelectedOutputChanged;
        public event EventHandler MapAcceptanceChanged;
        public event EventHandler PendingSelectionChanged;
        public event EventHandler DataChanged;
        public event EventHandler AnalysisDeleteRequested;
        public event EventHandler ProjectDataDeleteRequested;
        public event EventHandler RecordDisplayFilterRequested;
        public event EventHandler TreeLeftClicked;

        public bool LoadProject(string projectPath)
        {
            return Run(delegate { model.LoadProject(projectPath); Loaded(); });
        }
        public bool LoadRecords(string[] paths, int[] phases, double[] voltages, int[] sequences, double[] ids)
        {
            return Run(delegate { model.LoadRecords(paths, phases, voltages, sequences, ids); Loaded(); });
        }
        public bool SetDataIds(string[] paths, double[] ids)
        {
            return Run(delegate { model.SetDataIds(paths, ids);treeSnapshotGeneration++; ResetOutputs(); Rebuild(true); DataChangeSequence++; Raise(DataChanged); NotifyOutput(); });
        }
        public bool SetParameterMatrix(double[,] matrix)
        {
            return Run(delegate {model.SetParameterMatrix(matrix);CompleteMatrixUpdate();});
        }
        public bool SetParameterMatrixAndReviewStates(double[,] matrix,bool[] reviewed,bool[] removed,bool[] confirmed)
        {
            return Run(delegate {model.SetParameterMatrixAndReviewStates(matrix,reviewed,removed,confirmed);CompleteMatrixUpdate();});
        }
        private void CompleteMatrixUpdate()
        {
                if(!model.LastMatrixValueChanged){RefreshStateRows(model.LastChangedStateRows);return;}
                long snapshot=++treeSnapshotGeneration;
                ResetOutputs();
                LastChangedMapId = LastChangedMatrixRowIndex = -1;
                if(model.LastMatrixStructureChanged)Rebuild(true);
                else RefreshStateRows(model.LastChangedStateRows);
                if(tree.SelectedNode!=null)UpdateSelectedOutputs(tree.SelectedNode);
                MatrixChangeSequence++;
                SelectionSequence++;
                DataChangeSequence++;
                Raise(DataChanged);
                if(snapshot!=treeSnapshotGeneration)return;
                Raise(PendingSelectionChanged);
                if(snapshot!=treeSnapshotGeneration)return;
                NotifyOutput();
        }
        public bool MarkAnalyzed(string[] paths)
        {
            return Run(delegate {
                model.MarkAnalyzed(paths); RefreshStates();
                SelectionSequence++; DataChangeSequence++;
                Raise(PendingSelectionChanged); Raise(DataChanged);
            });
        }
        public bool SetReviewedRows(bool[] values)
        {
            return Run(delegate { model.SetReviewedRows(values); RefreshStateRows(model.LastChangedStateRows); });
        }

        public bool SetRemovedRows(bool[] values)
        {
            return Run(delegate { model.SetRemovedRows(values); RefreshStateRows(model.LastChangedStateRows); });
        }

        public bool SetAcceptanceConfirmedRows(bool[] values)
        {
            return Run(delegate { model.SetAcceptanceConfirmedRows(values);RefreshStateRows(model.LastChangedStateRows); });
        }
        public bool SetReviewStates(bool[] reviewed,bool[] removed,bool[] confirmed)
        {
            return Run(delegate {model.SetReviewStates(reviewed,removed,confirmed);RefreshStateRows(model.LastChangedStateRows);});
        }

        string MapReviewCaption(MapRecord map)
        {
            return map.IncludedInResults?(map.ManuallyAccepted?TextLabel("Accepted","已接受"):TextLabel("Accepted / automatic analysis","已接受 / 自动分析")):map.Removed?TextLabel("Removed / source waveform retained","已删除 / 保留原始波形"):
                map.Reviewed?TextLabel("Uncertain / source waveform retained","不确定 / 保留原始波形"):TextLabel("Pending review","待复核");
        }
        string RecordReviewCaption(LocationRecord record)
        {
            if(record.Maps.Count==0)return TextLabel("Analyzed","已分析");
            if(record.Maps.All(map=>map.IncludedInResults))return TextLabel("Accepted","已接受");
            if(record.Maps.All(map=>map.Removed))return TextLabel("Removed","已删除");
            if(record.Maps.Any(map=>!map.IncludedInResults&&!map.Removed&&!map.Reviewed))return TextLabel("Pending review","待复核");
            if(record.Maps.Any(map=>!map.IncludedInResults&&!map.Removed))return TextLabel("Uncertain","不确定");
            return TextLabel("Processed","已处理");
        }
        public bool[] GetReviewedRows() { return Read(delegate { return model.GetReviewedRows(); }); }
        public bool ResetAnalysisForPaths(string[] paths)
        {
            return Run(delegate {
                model.ResetAnalysisForPaths(paths);
                ResetOutputs(); LastChangedMapId = LastChangedMatrixRowIndex = -1;
                Rebuild(true); MatrixChangeSequence++; SelectionSequence++; DataChangeSequence++;
                Raise(DataChanged); Raise(PendingSelectionChanged); NotifyOutput();
            });
        }
        public bool SetDataSelected(string path, bool value)
        {
            return Run(delegate { if(model.FindRecord(path)==null) throw new ArgumentException("Data path was not found."); if (model.SetRecordSelected(path, value)) PendingChanged(); });
        }
        public bool SetPhaseSelected(int phase, bool value)
        {
            return Run(delegate { if (model.SetPhaseSelected(phase, value)) PendingChanged(); });
        }
        public int GetPhaseCheckState(int phase) { return Read(delegate { return model.GetPhaseCheckState(phase); }); }
        public bool SetMapAccepted(int mapId, bool value)
        {
            return Run(delegate {
                MapRecord m = model.FindMap(mapId);
                if(m==null) throw new ArgumentException("Map ID is not present in the current matrix.");
                ChangeAcceptance(m, value);
            });
        }
        public bool SetMapAcceptedBySourceRow(int sourceRow, bool value)
        {
            return Run(delegate {
                MapRecord m = model.FindMapBySourceRow(sourceRow);
                if(m==null) throw new ArgumentException("Source row is not present in the current matrix.");
                ChangeAcceptance(m, value);
            });
        }
        private void ChangeAcceptance(MapRecord map, bool value)
        {
            bool changed=model.SetMapAcceptedBySourceRow(map.RowIndex,value);
            // Repeating the current state must not invent manual provenance for
            // an already accepted automatic result.
            bool decisionChanged=value?!map.IncludedInResults:!map.Removed;
            if(!changed&&!decisionChanged)return;
            map.Reviewed=true;map.Removed=!value;if(value)map.AcceptanceConfirmed=true;
            LastChangedMapId = map.Id; LastChangedMatrixRowIndex = map.RowIndex; LastChangedAccepted = value;
            MatrixChangeSequence++; RefreshStateRows(new[]{map.RowIndex}); Raise(MapAcceptanceChanged);
        }
        public double[,] GetParameterMatrix() { return Read(delegate { return model.GetParameterMatrix(); }); }
        public string[] GetSelectedDataPaths() { return Read(delegate { return model.GetSelectedDataPaths(); }); }
        public string[] GetDataPaths() { return Read(delegate { return model.Records.Select(r => r.DataPath).ToArray(); }); }
        public double[] GetDataIds() { return Read(delegate { return model.Records.Select(r => r.DataId).ToArray(); }); }
        public double[,] GetDataCatalog()
        {
            return Read(delegate {
                var a = new double[model.Records.Count, 7];
                for (int i=0;i<model.Records.Count;i++) {
                    LocationRecord r=model.Records[i];
                    a[i,0]=r.Phase; a[i,1]=r.DataId; a[i,2]=r.VoltageKv; a[i,3]=r.Sequence;
                    a[i,4]=r.Analyzed?1:0; a[i,5]=r.Selected?1:0; a[i,6]=r.Maps.Count;
                }
                return a;
            });
        }
        public string[] GetAnalyzedDataPaths()
        { return Read(delegate { return model.Records.Where(r => r.Analyzed).Select(r => r.DataPath).ToArray(); }); }
        public bool SelectMap(int mapId)
        {
            return Run(delegate {
                MapRecord map = model.FindMap(mapId);
                if(map==null) throw new ArgumentException("Map ID is not present in the current matrix.");
                SelectRow(map.RowIndex);
            });
        }
        public bool SelectSourceRow(int sourceRow)
        {
            return Run(delegate { SelectRow(sourceRow); });
        }
        private void SelectRow(int sourceRow)
        {
                TreeNode node;
                if(!mapNodesByRow.TryGetValue(sourceRow,out node)||!Object.ReferenceEquals(Info(node).Map,model.FindMapBySourceRow(sourceRow)))
                    throw new ArgumentException("Source row is not present in the current matrix snapshot.");
                Activate(node);
        }
        public bool SelectData(string path)
        {
            return Run(delegate {
                LocationRecord r=model.FindRecord(path);
                if(r==null) throw new ArgumentException("Data path was not found.");
                TreeNode node=AllNodes().First(n=>Info(n).Kind==3 && object.ReferenceEquals(Info(n).Record,r));
                tree.SelectedNode=node; node.EnsureVisible(); Activate(node);
            });
        }
        public void ExpandAll() { Run(delegate { tree.ExpandAll(); }); }
        public void CollapseAll() { Run(delegate { tree.CollapseAll(); if(tree.Nodes.Count>0) { tree.Nodes[0].Expand(); tree.TopNode=tree.Nodes[0]; } }); }

        // Language- and directory-independent visual keys for named analysis archives.
        private string ArchiveNodeKey(TreeNode node)
        {
            NodeInfo info=Info(node);
            if(info.Kind<3)return info.Kind+":"+info.Phase;
            string key=info.Kind+":"+info.Phase+":"+info.Record.DataId.ToString("R",CultureInfo.InvariantCulture);
            return info.Kind==4?key+":"+info.Map.Id.ToString(CultureInfo.InvariantCulture):key;
        }
        public string[] GetExpandedNodeKeys() { return Read(()=>AllNodes().Where(n=>n.IsExpanded).Select(ArchiveNodeKey).ToArray()); }
        public string GetSelectedNodeKey() { return Read(()=>tree.SelectedNode==null?"":ArchiveNodeKey(tree.SelectedNode)); }
        public string GetTopNodeKey() { return Read(()=>tree.TopNode==null?"":ArchiveNodeKey(tree.TopNode)); }
        public bool RestoreNodeView(string[] expandedKeys,string selectedKey,string topKey)
        {
            return Run(delegate{
                var expanded=new HashSet<string>(expandedKeys??new string[0],StringComparer.Ordinal);
                rebuilding=true;tree.BeginUpdate();
                try{
                    tree.CollapseAll();TreeNode selected=null,top=null;
                    foreach(TreeNode node in AllNodes()){
                        string key=ArchiveNodeKey(node);if(expanded.Contains(key))node.Expand();
                        if(key==selectedKey)selected=node;if(key==topKey)top=node;
                    }
                    // Restore presentation only; the review control restores its waveform separately.
                    tree.SelectedNode=selected;if(top!=null)tree.TopNode=top;
                    ResetOutputs();
                    if(selected!=null){NodeInfo info=Info(selected);SelectedNodeType=info.Kind;
                        if(info.Record!=null){SelectedDataPath=info.Record.DataPath;SelectedDataId=info.Record.DataId;}
                        if(info.Map!=null){SelectedPdMapId=info.Map.Id;SelectedMatrixRowIndex=info.Map.RowIndex;}}
                }finally{tree.EndUpdate();rebuilding=false;}
            });
        }

        private void FocusMapNode(TreeNode node)
        {
            // Keep the selected MAP's file as the context header. An explicit
            // MAP action, not initial loading, opens this one branch.
            bool scopeChanged=tree.SelectedNode==null||tree.SelectedNode.Parent!=node.Parent;
            if(scopeChanged)tree.BeginUpdate();
            try {
                if(scopeChanged){
                    TreeNode file=node.Parent,phase=file==null?null:file.Parent;
                    foreach(TreeNode root in tree.Nodes)foreach(TreeNode branch in root.Nodes){
                        if(branch!=phase){if(branch.IsExpanded)branch.Collapse();continue;}
                        foreach(TreeNode sibling in branch.Nodes)
                            if(sibling!=file&&sibling.IsExpanded)sibling.Collapse();
                    }
                }
                for(TreeNode parent=node.Parent;parent!=null;parent=parent.Parent) parent.Expand();
                if(tree.SelectedNode!=node)tree.SelectedNode=node;
            } finally { if(scopeChanged)tree.EndUpdate(); }
            if(scopeChanged&&node.Parent!=null)tree.TopNode=node.Parent;
            // A late MAP in a long file cannot fit alongside its parent. In that
            // case keep the exact MAP visible instead of hiding its highlight.
            Rectangle bounds=node.Bounds;
            if(bounds.Top<0 || bounds.Bottom>tree.ClientSize.Height) node.EnsureVisible();
        }
        private void InvalidateSelectedNode()
        {
            if(tree.SelectedNode!=null)tree.Invalidate(tree.SelectedNode.Bounds);
        }

        private bool Run(Action action)
        {
            if(IsDisposed) return false;
            if(System.Threading.Thread.CurrentThread.ManagedThreadId!=ownerThread) {
                if(!IsHandleCreated) { LastError="Create the control handle on its owning UI thread before calling from another thread."; return false; }
                return (bool)Invoke(new Func<Action,bool>(Run),action);
            }
            try { action(); LastError=""; return true; }
            catch(Exception ex) { LastError=ex.Message; return false; }
        }
        private T Read<T>(Func<T> read)
        {
            if(System.Threading.Thread.CurrentThread.ManagedThreadId!=ownerThread) {
                if(!IsHandleCreated) throw new InvalidOperationException("The control handle has not been created on its owning UI thread.");
                return (T)Invoke(read);
            }
            return read();
        }
        private void Loaded()
        {
            treeSnapshotGeneration++;
            ResetOutputs(); LastChangedMapId=LastChangedMatrixRowIndex=-1; LastChangedAccepted=false;
            Rebuild(false); MatrixChangeSequence++; SelectionSequence++; DataChangeSequence++;
            Raise(DataChanged); Raise(PendingSelectionChanged); NotifyOutput();
        }
        private void ResetOutputs()
        {
            SelectedNodeType=0; SelectedPdMapId=-1; SelectedMatrixRowIndex=-1;
            SelectedDataId=double.NaN; SelectedDataPath="";
            RequestedDeleteDataPath="";
            RequestedDisplayDataPath="";
        }
        private void NotifyOutput() { ClickSequence++; Raise(SelectedOutputChanged); }
        private void Raise(EventHandler handler) { if(handler!=null&&!SuppressArchiveNotifications) handler(this,EventArgs.Empty); }
        private void PendingChanged() { RefreshStates(); SelectionSequence++; Raise(PendingSelectionChanged); }
        private static NodeInfo Info(TreeNode node) { return (NodeInfo)node.Tag; }
        private IEnumerable<TreeNode> AllNodes()
        {
            foreach(TreeNode root in tree.Nodes) {
                yield return root;
                foreach(TreeNode phase in root.Nodes) {
                    yield return phase;
                    foreach(TreeNode data in phase.Nodes) {
                        yield return data;
                        foreach(TreeNode map in data.Nodes) yield return map;
                    }
                }
            }
        }
        private string PhaseName(int p) { return p==0?"ALL":english?"L"+p:new[]{"","A","B","C"}[p]; }
        private string Key(TreeNode n)
        {
            NodeInfo i=Info(n);
            if(i.Kind==3) return "D:"+i.Record.DataPath;
            if(i.Kind==4) return "M:"+i.Record.DataPath+":"+i.Map.Id.ToString(CultureInfo.InvariantCulture);
            return i.Kind+":"+i.Phase;
        }
        private void Rebuild(bool preserve)
        {
            TreeRebuildCount++;
            var expanded=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string selected=null,top=null;
            if(preserve) {
                foreach(TreeNode n in AllNodes()) if(n.IsExpanded) expanded.Add(Key(n));
                if(tree.SelectedNode!=null) selected=Key(tree.SelectedNode);
                if(tree.TopNode!=null) top=Key(tree.TopNode);
            }
            rebuilding=true; tree.BeginUpdate();
            try {
                multiSelectedNodes.Clear();
                tree.Nodes.Clear();mapNodesByRow.Clear();
                var root=new TreeNode(TextLabel("Location data","定位数据")); root.Tag=new NodeInfo{Kind=1}; tree.Nodes.Add(root);
                foreach(int p in new[]{1,2,3,0}) {
                    var list=model.Records.Where(r=>r.Phase==p).ToList();
                    if(list.Count==0) continue;
                    var phase=new TreeNode(PhaseName(p)+(english||p==0?"":"相")+" ("+list.Count.ToString(CultureInfo.InvariantCulture)+")");
                    phase.Tag=new NodeInfo{Kind=2,Phase=p}; root.Nodes.Add(phase);
                    var duplicates=list.GroupBy(r=>r.VoltageKv.ToString("0.00",CultureInfo.InvariantCulture)+"_"+r.Sequence).Where(g=>g.Count()>1).Select(g=>g.Key).ToArray();
                    foreach(LocationRecord r in list) {
                        string label="#"+r.Sequence.ToString(CultureInfo.InvariantCulture)+" "+r.VoltageKv.ToString("0.##",CultureInfo.InvariantCulture)+"kV";
                        if(duplicates.Contains(r.VoltageKv.ToString("0.00",CultureInfo.InvariantCulture)+"_"+r.Sequence)) label+=" · ID "+r.DataId.ToString("0",CultureInfo.InvariantCulture);
                        var data=new TreeNode(label); data.Tag=new NodeInfo{Kind=3,Phase=p,Record=r}; phase.Nodes.Add(data);
                        int local=0;
                        foreach(MapRecord m in r.Maps) {
                            var map=new TreeNode(TextLabel("Pulse ","脉冲 ")+(++local).ToString("00",CultureInfo.InvariantCulture)); map.Tag=new NodeInfo{Kind=4,Phase=p,Record=r,Map=m}; data.Nodes.Add(map);mapNodesByRow.Add(m.RowIndex,map);
                        }
                    }
                }
                RefreshStates();
                foreach(TreeNode n in AllNodes()) {
                    if(preserve?expanded.Contains(Key(n)):Info(n).Kind==1) n.Expand();
                    if(preserve && selected!=null && Key(n)==selected) tree.SelectedNode=n;
                }
                TreeNode topNode=AllNodes().FirstOrDefault(n=>Key(n)==top);
                if(topNode!=null) tree.TopNode=topNode;
            } finally { tree.EndUpdate(); rebuilding=false;UpdateSelectionDetails(); }
        }
        public void SetZeroVoltageReferenceStatus(string sourcePath,string state)
        {
            Run(delegate{
                foreach(var record in model.Records.Where(r=>r.VoltageKv<=0&&String.Equals(r.DataPath,sourcePath,StringComparison.OrdinalIgnoreCase)))
                    record.NoiseReferenceState=state=="Verified"||state=="Simulation"||state=="Rejected"?state:"Pending";
                RefreshStates();UpdateSelectionDetails();
            });
        }
        private string NoiseReferenceCaption(LocationRecord record)
        {
            if(record.NoiseReferenceState=="Verified")return TextLabel("Noise reference","参考噪声");
            if(record.NoiseReferenceState=="Simulation")return TextLabel("SIM noise reference","仿真参考噪声");
            if(record.NoiseReferenceState=="Rejected")return TextLabel("Reference not applicable","参考不适用");
            return TextLabel("Reference unverified","参考待核验");
        }
        private void RefreshStates()
        {
            StateRefreshPassCount++;
            foreach(TreeNode n in AllNodes())RefreshStateNode(n);
        }
        private void RefreshStateRows(int[] rows)
        {
            if(rows.Length==0)return;
            var nodes=new HashSet<TreeNode>();
            foreach(int row in rows){
                TreeNode node;
                if(!mapNodesByRow.TryGetValue(row,out node)||!Object.ReferenceEquals(Info(node).Map,model.FindMapBySourceRow(row)))
                    throw new InvalidOperationException("Tree row cache does not match the current matrix snapshot.");
                for(TreeNode current=node;current!=null;current=current.Parent)nodes.Add(current);
            }
            StateRefreshPassCount++;
            tree.BeginUpdate();
            try{foreach(TreeNode node in nodes)RefreshStateNode(node);}
            finally{tree.EndUpdate();}
            if(tree.SelectedNode!=null&&nodes.Contains(tree.SelectedNode))UpdateSelectionDetails();
        }
        private void RefreshStateNode(TreeNode n)
        {
                StateRefreshNodeCount++;
                NodeInfo i=Info(n);
                if(i.Kind==1) { n.StateImageIndex=6; n.ToolTipText=TextLabel("Location data","定位数据"); }
                else if(i.Kind==2) {
                    int state=model.GetPhaseCheckState(i.Phase); n.StateImageIndex=state<0?5:state;
                    n.ToolTipText=state<0?TextLabel("No eligible pending data","该相无可分析的待处理数据"):TextLabel("Select eligible pending data in this phase","勾选该相允许分析的待处理数据");
                } else if(i.Kind==3) {
                    LocationRecord r=i.Record;
                    bool noise=!model.CanAnalyze(r);
                    n.StateImageIndex=noise?5:r.Analyzed?(r.Maps.Count==0?4:3):(r.Selected?1:0);
                    string status=r.Analyzed?(r.Maps.Count==0?TextLabel("Analyzed / no MAP extracted","已分析 / 未提取到 MAP"):TextLabel("Analyzed / MAPs present","已分析 / 有 MAP")):TextLabel("Pending analysis","待分析");
                    if(noise)status=NoiseReferenceCaption(r)+TextLabel("; read-only comparison, excluded from discharge statistics and diagnosis","；只读对照，不计入放电统计和诊断结论");
                    else if(r.VoltageKv==0)status+=TextLabel("; zero-voltage analysis selected; voltage phase unknown","；已选择分析零电压记录；电压相位未知");
                    string shortStatus=noise?NoiseReferenceCaption(r):!r.Analyzed?TextLabel("Pending","未分析"):RecordReviewCaption(r);
                    string caption="#"+r.Sequence.ToString("00",CultureInfo.InvariantCulture)+" · "+r.VoltageKv.ToString("0.##",CultureInfo.InvariantCulture)+" kV · "+shortStatus;
                    bool duplicate=model.Records.Count(other=>other.Phase==r.Phase&&other.Sequence==r.Sequence&&other.VoltageKv==r.VoltageKv)>1;
                    if(duplicate)caption+=" · ID "+r.DataId.ToString("0",CultureInfo.InvariantCulture);
                    if(n.Text!=caption)n.Text=caption;
                    n.ForeColor=noise?HVX.PdReview.ReviewTheme.SecondaryInk:tree.ForeColor;
                    string reviewStatus=r.Maps.Count==0?"":TextLabel("\nReviewed: ","\n已复核：")+r.Maps.Count(map=>map.Reviewed)+" / "+r.Maps.Count+
                        TextLabel("; accepted: ","；已接受：")+r.Maps.Count(map=>map.IncludedInResults)+
                        TextLabel("; pending review: ","；待复核：")+r.Maps.Count(map=>!map.IncludedInResults&&!map.Removed&&!map.Reviewed)+
                        TextLabel("; uncertain: ","；不确定：")+r.Maps.Count(map=>!map.IncludedInResults&&!map.Removed&&map.Reviewed)+
                        TextLabel("; removed: ","；已删除：")+r.Maps.Count(map=>map.Removed)+TextLabel(". Viewing alone does not enter statistics.","。仅查看不计入统计。");
                    if(r.Maps.Any(map=>map.IncludedInResults&&!map.ManuallyAccepted))reviewStatus+=TextLabel("\nIncludes automatic analysis results. You can review the waveforms and adjust acceptance or removal.","\n包含自动分析结果，可查看波形并调整接受或删除。");
                    n.ToolTipText=caption+"\n"+status+reviewStatus+(r.Analyzed?TextLabel("\nClick status icon to clear these analysis results.","\n点击状态图标可清除该数据的分析结果。") : "")+"\nID: "+r.DataId.ToString("0",CultureInfo.InvariantCulture)+"\n"+r.DataPath;
                } else {
                    n.StateImageIndex=i.Map.IncludedInResults?8:i.Map.Removed?10:i.Map.Reviewed?9:7;
                    n.ToolTipText=n.Text+"\n"+MapReviewCaption(i.Map);
                }
        }
        private bool IsDeletableLocationNode(TreeNode node)
        {
            NodeInfo info=node==null?null:Info(node);
            return info!=null&&info.Kind==3&&info.Record!=null&&!String.IsNullOrWhiteSpace(info.Record.DataPath);
        }
        private void HandleNodeClick(object sender,TreeNodeMouseClickEventArgs e)
        {
            if(rebuilding) return;
            if(e.Button==MouseButtons.Right) {
                PrepareDeleteSelectionForNode(e.Node);
                PrepareDisplayMenu(e.Node); displayMenu.Show(tree,e.Location); return;
            }
            if(e.Button!=MouseButtons.Left) return;
            if(e.Clicks>1) return;
            TreeViewHitTestLocations hit=tree.HitTest(e.Location).Location;
            if((hit&TreeViewHitTestLocations.StateImage)!=0) { Toggle(e.Node); return; }
            bool ctrl=(ModifierKeys&Keys.Control)!=0;
            if(ctrl&&IsDeletableLocationNode(e.Node))
            {
                // Ctrl multi-select: update tree chrome only — no overview/waveform reload.
                multiSelectClick=true;
                try
                {
                    if(multiSelectedNodes.Contains(e.Node))multiSelectedNodes.Remove(e.Node);
                    else multiSelectedNodes.Add(e.Node);
                    if(tree.SelectedNode!=e.Node)tree.SelectedNode=e.Node;
                    UpdateSelectionDetails();
                    tree.Invalidate();
                }
                finally{multiSelectClick=false;}
                return;
            }
            multiSelectedNodes.Clear();
            if(IsDeletableLocationNode(e.Node))multiSelectedNodes.Add(e.Node);
            tree.Invalidate();
            if((hit&TreeViewHitTestLocations.Label)!=0||(hit&TreeViewHitTestLocations.Image)!=0||(hit&TreeViewHitTestLocations.RightOfLabel)!=0) Activate(e.Node);
        }
        private void HandleTreeMouseDown(object sender,MouseEventArgs e)
        {
            // Ctrl multi-select must not clear spectrum scope or other result views.
            if(rebuilding||e.Button!=MouseButtons.Left)return;
            TreeNode hit=tree.HitTest(e.Location).Node;
            if((ModifierKeys&Keys.Control)!=0&&IsDeletableLocationNode(hit))return;
            Raise(TreeLeftClicked);
        }
        private void UpdateContextText()
        {
            showRecordMenu.Text=TextLabel("Show this location group","显示该组定位数据");
            showAllRecordsMenu.Text=TextLabel("Exit group view","退出该组谱图");
            int count=CollectDeletableDataPaths().Length;
            deleteDataMenu.Text=count>1?TextLabel("Delete selected ("+count+")","删除所选 ("+count+")"):TextLabel("Delete","删除");
        }
        private void PrepareDisplayMenu(TreeNode node)
        {
            NodeInfo info=node==null?null:Info(node);
            contextDataPath=info!=null && info.Record!=null?info.Record.DataPath:"";
            showRecordMenu.Enabled=!String.IsNullOrEmpty(contextDataPath);
            UpdateContextText();
            deleteDataMenu.Enabled=CollectDeletableDataPaths().Length>0;
        }
        private void PrepareDeleteSelectionForNode(TreeNode node)
        {
            if(IsDeletableLocationNode(node)&&!multiSelectedNodes.Contains(node))
            {
                multiSelectedNodes.Clear();
                multiSelectedNodes.Add(node);
                suppressOutputNotify=true;
                try{if(tree.SelectedNode!=node)tree.SelectedNode=node;}
                finally{suppressOutputNotify=false;}
                UpdateSelectionDetails();
                tree.Invalidate();
            }
        }
        private string[] CollectDeletableDataPaths()
        {
            var paths=new List<string>();
            var seen=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach(TreeNode node in multiSelectedNodes.Concat(new[]{tree.SelectedNode}).Where(n=>n!=null))
            {
                if(!IsDeletableLocationNode(node))continue;
                string path=Info(node).Record.DataPath;
                try{path=Path.GetFullPath(path);}catch{continue;}
                if(seen.Add(path))paths.Add(path);
            }
            return paths.ToArray();
        }
        private void RequestDeleteSelectedDataFiles()
        {
            string[] paths=CollectDeletableDataPaths();
            if(paths.Length==0){LastError=TextLabel("Ctrl-select location data first.","请先 Ctrl 复选定位数据。");return;}
            bool englishUi=english;
            if(MessageBox.Show(FindForm(),ProjectDataFileDeletion.ConfirmMessage(paths.Length,englishUi),
                ProjectDataFileDeletion.ConfirmCaption(englishUi),MessageBoxButtons.OKCancel,MessageBoxIcon.Warning,MessageBoxDefaultButton.Button2)!=DialogResult.OK)
                return;
            RequestedDeleteDataPaths=paths;
            Raise(ProjectDataDeleteRequested);
        }
        private void RequestDisplayScope(string path)
        {
            RequestedDisplayDataPath=path??"";
            Raise(RecordDisplayFilterRequested);
        }
        private void HandleKey(object sender,KeyEventArgs e)
        {
            if(e.KeyCode==Keys.Delete)
            {
                if(CollectDeletableDataPaths().Length==0)return;
                e.Handled=true;e.SuppressKeyPress=true;RequestDeleteSelectedDataFiles();return;
            }
            if(tree.SelectedNode==null) return;
            if(e.KeyCode==Keys.Space) { Toggle(tree.SelectedNode); e.Handled=true; e.SuppressKeyPress=true; }
            else if(e.KeyCode==Keys.Enter) { Activate(tree.SelectedNode); e.Handled=true; e.SuppressKeyPress=true; }
        }
        private void Toggle(TreeNode n)
        {
            NodeInfo i=Info(n);
            if(i.Kind==2 && model.GetPhaseCheckState(i.Phase)>=0) SetPhaseSelected(i.Phase,model.GetPhaseCheckState(i.Phase)!=1);
            else if(i.Kind==3) {
                if (!i.Record.Analyzed) SetDataSelected(i.Record.DataPath,!i.Record.Selected);
                else { RequestedDeleteDataPath=i.Record.DataPath; Raise(AnalysisDeleteRequested); }
            }
            else if(i.Kind==4) SetMapAcceptedBySourceRow(i.Map.RowIndex,!i.Map.IncludedInResults);
        }
        private void Activate(TreeNode n)
        {
            if(suppressOutputNotify||multiSelectClick){UpdateSelectionDetails();return;}
            NodeInfo i=Info(n);
            if(i.Kind==4) FocusMapNode(n);
            UpdateSelectedOutputs(n);
            NotifyOutput();
        }
        private void UpdateSelectedOutputs(TreeNode n)
        {
            NodeInfo i=Info(n);
            ResetOutputs(); SelectedNodeType=i.Kind;
            if(i.Record!=null) { SelectedDataPath=i.Record.DataPath; SelectedDataId=i.Record.DataId; }
            if(i.Map!=null) { SelectedPdMapId=i.Map.Id; SelectedMatrixRowIndex=i.Map.RowIndex; }
        }
        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            UpdateStateImageScale();
        }
        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);
            // Support both top-level and child per-monitor DPI notifications on
            // older Framework hosts, without requiring the newer DeviceDpi API.
            if(m.Msg==0x02E0 || m.Msg==0x02E3) UpdateStateImageScale();
        }
        private void UpdateStateImageScale()
        {
            if(!IsHandleCreated || IsDisposed) return;
            float scale;
            using(Graphics g=CreateGraphics()) scale=Math.Max(1F,g.DpiX/96F);
            int pixels=Math.Max(20,Math.Min(256,(int)Math.Round(20F*scale)));
            if(states.ImageSize.Width!=pixels) RefreshStateImages(pixels);
            tree.Indent=(int)Math.Round(9F*scale);
            tree.ItemHeight=Math.Max((int)Math.Round(25F*scale),tree.Font.Height+(int)Math.Ceiling(4F*scale));
        }
        private void RefreshStateImages(int pixels)
        {
            states.Images.Clear();
            foreach(Bitmap bitmap in stateBitmaps) bitmap.Dispose();
            stateBitmaps.Clear();
            states.ImageSize=new Size(pixels,pixels);
            for(int i=0;i<11;i++) {
                Bitmap bitmap=DrawState(i,pixels);
                stateBitmaps.Add(bitmap);
                states.Images.Add(bitmap);
            }
        }
        private static GraphicsPath StateTilePath()
        {
            var path=new GraphicsPath();
            const float x=2F,y=2F,size=16F,diameter=5F;
            path.AddArc(x,y,diameter,diameter,180,90);
            path.AddArc(x+size-diameter,y,diameter,diameter,270,90);
            path.AddArc(x+size-diameter,y+size-diameter,diameter,diameter,0,90);
            path.AddArc(x,y+size-diameter,diameter,diameter,90,90);
            path.CloseFigure();
            return path;
        }
        private static Pen StatePen(Color color,float width)
        {
            return new Pen(color,width){StartCap=LineCap.Round,EndCap=LineCap.Round,LineJoin=LineJoin.Round};
        }
        private static Bitmap DrawState(int state,int pixels)
        {
            // Render at 4x the actual monitor resolution. The transparent margin,
            // rounded joins and downsampling keep the mark smooth at 100-200% DPI.
            var bitmap=new Bitmap(pixels,pixels,System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
            using(var large=new Bitmap(pixels*4,pixels*4,System.Drawing.Imaging.PixelFormat.Format32bppPArgb)) {
                using(Graphics g=Graphics.FromImage(large)) {
                    g.Clear(Color.Transparent);
                    g.SmoothingMode=SmoothingMode.AntiAlias;
                    g.PixelOffsetMode=PixelOffsetMode.HighQuality;
                    g.ScaleTransform(large.Width/20F,large.Height/20F);
                    Color accent=HVX.PdReview.ReviewTheme.Active;
                    Color border=HVX.PdReview.ReviewTheme.Border;
                    bool reviewState=state>=7;
                    Color reviewColor=state==7?Color.FromArgb(185,130,39):state==8?Color.FromArgb(37,128,100):state==9?Color.FromArgb(123,139,157):Color.FromArgb(182,77,88);
                    bool active=state==1 || state==2;
                    Color fill=active?accent:state==5?HVX.PdReview.ReviewTheme.SurfaceAlt:state==3?HVX.PdReview.ReviewTheme.ActiveSoft:HVX.PdReview.ReviewTheme.Surface;
                    if(reviewState)fill=reviewColor;
                    using(GraphicsPath tile=StateTilePath()) {
                        using(var brush=new SolidBrush(fill)) g.FillPath(brush,tile);
                        using(var pen=StatePen(reviewState?reviewColor:active?accent:border,1.15F)) g.DrawPath(pen,tile);
                    }
                    if(state==1 || state==8) {
                        using(var pen=StatePen(Color.White,2.15F))
                            g.DrawLines(pen,new[]{new PointF(5.2F,10F),new PointF(8.4F,13.1F),new PointF(14.7F,6.8F)});
                    } else if(state==9) {
                        using(var pen=StatePen(Color.White,1.9F))
                            g.DrawPolygon(pen,new[]{new PointF(10F,5.5F),new PointF(15F,14F),new PointF(5F,14F)});
                    } else if(state==2 || state==4) {
                        using(var pen=StatePen(state==2?Color.White:HVX.PdReview.ReviewTheme.SecondaryInk,2.15F))
                            g.DrawLine(pen,6F,10F,14F,10F);
                    } else if(state==7) {
                        using(var pen=StatePen(Color.White,1.9F)) {
                            g.DrawArc(pen,6.9F,5F,6.2F,5.7F,185,265);
                            g.DrawLine(pen,10F,10.8F,10F,11.7F);
                        }
                        using(var brush=new SolidBrush(Color.White))g.FillEllipse(brush,9F,13.5F,2F,2F);
                    } else if(state==10) {
                        using(var pen=StatePen(Color.White,2.1F)) {
                            g.DrawLine(pen,6.5F,6.5F,13.5F,13.5F);g.DrawLine(pen,13.5F,6.5F,6.5F,13.5F);
                        }
                    } else if(state==3 || state==6) {
                        Color stroke=state==3?HVX.PdReview.ReviewTheme.SecondaryInk:accent;
                        using(var pen=StatePen(stroke,1.55F))
                            g.DrawLines(pen,new[]{new PointF(4.5F,10.2F),new PointF(6.4F,10.2F),new PointF(8F,5.6F),new PointF(10.4F,14.2F),new PointF(12.5F,8.1F),new PointF(13.7F,10.2F),new PointF(15.5F,10.2F)});
                    }
                }
                using(Graphics g=Graphics.FromImage(bitmap)) {
                    g.CompositingMode=CompositingMode.SourceCopy;
                    g.InterpolationMode=InterpolationMode.HighQualityBicubic;
                    g.PixelOffsetMode=PixelOffsetMode.HighQuality;
                    g.DrawImage(large,new Rectangle(0,0,pixels,pixels),0,0,large.Width,large.Height,GraphicsUnit.Pixel);
                }
            }
            return bitmap;
        }
        protected override void Dispose(bool disposing)
        {
            if(disposing) { tree.NodeMouseClick-=HandleNodeClick; tree.MouseDown-=HandleTreeMouseDown; tree.KeyDown-=HandleKey; displayMenu.Dispose(); }
            base.Dispose(disposing);
            if(disposing) { states.Dispose(); foreach(Bitmap bitmap in stateBitmaps) bitmap.Dispose(); stateBitmaps.Clear(); }
        }
    }
}
