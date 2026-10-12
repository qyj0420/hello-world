using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Drawing;
using LabVIEWItemInformation;

namespace HVX.Controls
{
    public sealed partial class ProjectDataBrowserControl
    {
        readonly Panel projectLoadingView=new Panel{Dock=DockStyle.Fill,BackColor=BrowserTheme.Surface};
        readonly Label projectLoadingText=new Label{Dock=DockStyle.Fill,TextAlign=ContentAlignment.MiddleCenter,ForeColor=BrowserTheme.Ink};
        readonly Button projectLoadRetry=new HVX.Presentation.R2RoundedButton{Dock=DockStyle.Bottom,Height=44,Visible=false};
        void InitializeProjectLoadingView()
        {
            projectLoadingView.Name="ProjectLoadingView";projectLoadingText.Name="ProjectLoadingText";
            projectLoadRetry.Name="ProjectLoadRetry";projectLoadRetry.Click+=(s,e)=>{using(var chooser=new FolderBrowserDialog()){if(chooser.ShowDialog(FindForm())==DialogResult.OK)BeginLoadProject(chooser.SelectedPath);}};
            projectLoadingView.Controls.Add(projectLoadingText);projectLoadingView.Controls.Add(projectLoadRetry);Controls.Add(projectLoadingView);projectLoadingView.Visible=false;
        }
        void ShowProjectLoadingView(string text)
        {
            projectLoadRetry.Visible=false;projectLoadingText.Text=text;projectLoadingView.Visible=true;projectLoadingView.BringToFront();shellLayout.Visible=false;
        }
        void RevealProjectContent()
        {
            bool wasCovered=projectLoadingView.Visible||!shellLayout.Visible;
            shellLayout.Visible=true;
            if(wasCovered)shellLayout.PerformLayout();
            if(SelectedNodeKind=="project")ArrangeProjectInformation();
            projectLoadingView.Visible=false;
        }
        void FailProjectLoad(Exception ex)
        {
            loading=false;loadingWave=false;project=null;projectPath="";tree.Enabled=true;tree.Nodes.Clear();ClearDetail();item.ApplyPreparedItem(null,languageIndex,ex.Message);UpdateProjectHeader();lastError=ex.Message;
            string message=T("无法读取项目。请确认项目文件可访问后重新打开。","Unable to load project. Check that the project files are accessible, then reopen the project.");
            SetStatus(message);ShowProjectLoadingView(message);projectLoadRetry.Text=T("重新打开项目","Reopen project");projectLoadRetry.Visible=true;
        }
        sealed class PreparedProjectLoad
        {
            internal BrowserProject Project;
            internal ItemDocument Item;
            internal string ItemError="";
        }
        static PreparedProjectLoad PrepareProjectLoad(string path,double switchTime,CancellationToken token)
        {
            var prepared=new PreparedProjectLoad{Project=BrowserReader.LoadProject(path,switchTime,token)};
            PrepareProjectItem(prepared,token);
            return prepared;
        }
        static void PrepareProjectItem(PreparedProjectLoad prepared,CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            try{prepared.Item=ItemDocument.Load(prepared.Project.ProjectPath);}
            catch(OperationCanceledException){throw;}
            catch(Exception ex){prepared.ItemError=ex.Message;}
            token.ThrowIfCancellationRequested();
        }
        void RefreshProjectInformationAsync()
        {
            // Selecting the project node deliberately refreshes disk metadata.
            // Language/layout changes reuse the last applied snapshot instead.
            int selection=selectionGeneration,generation=projectGeneration;
            var prepared=new PreparedProjectLoad{Project=project};
            waveCancellation.Dispose();waveCancellation=new CancellationTokenSource();var token=waveCancellation.Token;
            loadingWave=true;heading.Text=T("项目信息","Project information");
            ShowProjectLoadingView(T("正在读取项目…","Loading project…"));
            SetStatus(T("正在读取项目…","Loading project…"));
            Task.Run(()=>{
                try{
                    PrepareProjectItem(prepared,token);
                    Post(()=>{
                        if(generation!=projectGeneration||selection!=selectionGeneration||token.IsCancellationRequested)return;
                        try{
                            item.ApplyPreparedItem(prepared.Item,languageIndex,prepared.ItemError);ShowProjectInfo();
                            SetStatus(prepared.Item==null?ItemLoadMessage(prepared.ItemError):T("项目信息已更新。","Project information updated."));
                        }catch(Exception ex){
                            item.ApplyPreparedItem(null,languageIndex,ex.Message);
                            ShowControl(new Label{Text=T("无法显示项目信息。请重新打开项目。","Unable to display project information. Reopen the project."),TextAlign=ContentAlignment.MiddleCenter});Error(ex.Message);
                        }finally{
                            if(generation==projectGeneration&&selection==selectionGeneration){
                                try{RevealProjectContent();}
                                catch(Exception ex){
                                    if(generation==projectGeneration&&selection==selectionGeneration){
                                        lastError=ex.Message;
                                        string message=T("无法显示项目信息。请重新打开项目。","Unable to display project information. Reopen the project.");
                                        SetStatus(message);ShowProjectLoadingView(message);
                                        projectLoadRetry.Text=T("重新打开项目","Reopen project");projectLoadRetry.Visible=true;
                                    }
                                }finally{if(generation==projectGeneration&&selection==selectionGeneration)loadingWave=false;}
                            }
                        }
                    });
                }catch(OperationCanceledException){}
            });
        }
    }
}
