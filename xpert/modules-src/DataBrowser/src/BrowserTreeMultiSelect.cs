using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using HVX.ProjectModel;

namespace HVX.Controls
{
    public sealed partial class ProjectDataBrowserControl
    {
        readonly HashSet<TreeNode> multiSelectedNodes=new HashSet<TreeNode>();
        ContextMenuStrip treeContextMenu;
        ToolStripMenuItem treeDeleteMenu;
        bool suppressTreeActivate;

        void InitializeTreeMultiSelect()
        {
            treeContextMenu=new ContextMenuStrip{Name="BrowserTreeContextMenu"};
            treeDeleteMenu=new ToolStripMenuItem{Name="BrowserTreeDelete"};
            treeDeleteMenu.Click+=(s,e)=>DeleteMultiSelectedDataFiles();
            treeContextMenu.Items.Add(treeDeleteMenu);
            // MouseDown runs before AfterSelect so Ctrl/right clicks can skip Activate.
            tree.MouseDown+=HandleTreeMultiSelectMouseDown;
            tree.NodeMouseClick+=HandleTreeMultiSelectClick;
            tree.KeyDown+=HandleTreeMultiSelectKey;
            treeContextMenu.Opening+=(s,e)=>PrepareTreeDeleteMenu();
        }
        void HandleTreeMultiSelectMouseDown(object sender,MouseEventArgs e)
        {
            suppressTreeActivate=false;
            if(editing||saving||loading)return;
            TreeNode hit=tree.HitTest(e.Location).Node;
            if(e.Button==MouseButtons.Right){suppressTreeActivate=true;return;}
            if(e.Button==MouseButtons.Left&&(ModifierKeys&Keys.Control)!=0&&IsDeletableDataNode(hit))
                suppressTreeActivate=true;
        }
        bool IsDeletableDataNode(TreeNode node)
        {
            var data=node==null?null:node.Tag as NodeData;
            if(data==null||!(data.Value is BrowserRecord))return false;
            return data.Kind=="cal"||data.Kind=="dem"||data.Kind=="loc";
        }
        bool IsTreeNodeHighlighted(TreeNode node)
        {
            return node!=null&&(node==tree.SelectedNode||multiSelectedNodes.Contains(node));
        }
        void ClearTreeMultiSelection()
        {
            if(multiSelectedNodes.Count==0)return;
            multiSelectedNodes.Clear();
            tree.Invalidate();
        }
        void HandleTreeMultiSelectClick(object sender,TreeNodeMouseClickEventArgs e)
        {
            if(e.Node==null||editing||saving||loading)return;
            if(e.Button==MouseButtons.Right)
            {
                PrepareDeleteSelectionForNode(e.Node);
                treeDeleteMenu.Text=T("删除","Delete");
                treeDeleteMenu.Enabled=CollectDeletableRecords().Length>0;
                treeContextMenu.Show(tree,e.Location);
                return;
            }
            if(e.Button!=MouseButtons.Left||e.Clicks>1)return;
            bool ctrl=(ModifierKeys&Keys.Control)!=0;
            if(ctrl)
            {
                if(!IsDeletableDataNode(e.Node))return;
                // Selection chrome only — detail/waveform stay as they are.
                if(multiSelectedNodes.Contains(e.Node))multiSelectedNodes.Remove(e.Node);
                else multiSelectedNodes.Add(e.Node);
                if(tree.SelectedNode!=e.Node){suppressTreeActivate=true;tree.SelectedNode=e.Node;}
                UpdateNavigationSelection();
                tree.Invalidate();
                return;
            }
            ClearTreeMultiSelection();
            if(IsDeletableDataNode(e.Node))multiSelectedNodes.Add(e.Node);
        }
        void HandleTreeMultiSelectKey(object sender,KeyEventArgs e)
        {
            if(e.KeyCode!=Keys.Delete||editing||saving||loading)return;
            if(CollectDeletableRecords().Length==0)return;
            e.Handled=true;e.SuppressKeyPress=true;
            DeleteMultiSelectedDataFiles();
        }
        void PrepareDeleteSelectionForNode(TreeNode node)
        {
            if(IsDeletableDataNode(node)&&!multiSelectedNodes.Contains(node))
            {
                multiSelectedNodes.Clear();
                multiSelectedNodes.Add(node);
                suppressTreeActivate=true;
                try{if(tree.SelectedNode!=node)tree.SelectedNode=node;else UpdateNavigationSelection();}
                finally{suppressTreeActivate=false;}
                tree.Invalidate();
            }
        }
        void PrepareTreeDeleteMenu()
        {
            treeDeleteMenu.Text=T("删除","Delete");
            int count=CollectDeletableRecords().Length;
            treeDeleteMenu.Enabled=count>0&&!editing&&!saving&&!loading&&project!=null;
            if(count>1)treeDeleteMenu.Text=T("删除所选 ("+count+")","Delete selected ("+count+")");
        }
        BrowserRecord[] CollectDeletableRecords()
        {
            var records=new List<BrowserRecord>();
            var seen=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach(TreeNode node in multiSelectedNodes.Concat(new[]{tree.SelectedNode}).Where(n=>n!=null))
            {
                if(!IsDeletableDataNode(node))continue;
                var record=(BrowserRecord)((NodeData)node.Tag).Value;
                if(record==null||String.IsNullOrWhiteSpace(record.Path)||!seen.Add(Path.GetFullPath(record.Path)))continue;
                records.Add(record);
            }
            return records.ToArray();
        }
        void DeleteMultiSelectedDataFiles()
        {
            if(project==null||editing||saving||loading)return;
            BrowserRecord[] records=CollectDeletableRecords();
            if(records.Length==0){SetStatus(T("请先 Ctrl 复选校准/测试/定位数据。","Ctrl-select calibration, test, or location data first."));return;}
            string message=ProjectDataFileDeletion.ConfirmMessage(records.Length,english);
            if(MessageBox.Show(FindForm(),message,ProjectDataFileDeletion.ConfirmCaption(english),
                MessageBoxButtons.OKCancel,MessageBoxIcon.Warning,MessageBoxDefaultButton.Button2)!=DialogResult.OK)return;
            string root=project.ProjectPath;
            string[] paths=records.Select(r=>r.Path).ToArray();
            try
            {
                ProjectDataFileDeletion.Delete(root,paths);
                ClearTreeMultiSelection();
                reloadRoot=root;reloadFile="";reloadKind="project";
                BeginLoadProject(root);
                SetStatus(T("已删除 ","Deleted ")+paths.Length+T(" 个数据文件（不可恢复）。"," data file(s) (irreversible)."));
            }
            catch(Exception ex)
            {
                lastError=ex.Message;
                SetStatus(T("删除失败：","Delete failed: ")+ex.Message);
                MessageBox.Show(FindForm(),ex.Message,ProjectDataFileDeletion.ConfirmCaption(english),MessageBoxButtons.OK,MessageBoxIcon.Error);
            }
        }
        string MultiSelectionSummary()
        {
            int count=multiSelectedNodes.Count(IsDeletableDataNode);
            if(count<=1)return "";
            return T("已复选 ","Multi-selected ")+count+T(" 个数据文件（校准/测试/定位）"," data files (calibration/test/location)");
        }
    }
}
