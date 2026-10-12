using System;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using HVX.ProjectCables;
namespace HVX.Controls
{
    public sealed partial class ProjectDataBrowserControl
    {
        string pendingSelection="",saveNotice="",saveNoticeRoot="";
        public event EventHandler CableStructureChanged;
        public bool RefreshCableStructure()
        {
            if(IsDisposed||Disposing)return false;
            if(InvokeRequired)return (bool)Invoke(new Func<bool>(RefreshCableStructure));
            if(IsBusy||editing)return false;
            if(project==null||String.IsNullOrWhiteSpace(projectPath))return true;
            try{ApplyCableJointSnapshot(CableJointStore.Read(projectPath));return true;}
            catch(Exception ex){return Error(T("接头结构刷新失败：","Cable structure refresh failed: ")+ex.Message);}
        }
        void ApplyCableJointSnapshot(CableJointSnapshot refreshed)
        {
            var phases=refreshed.Blocks.GroupBy(b=>b.Phase,StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g=>g.Key,g=>g.SelectMany(b=>b.Positions.Skip(1).Take(Math.Max(0,b.Positions.Count-2))).Distinct().OrderBy(v=>v).ToArray(),StringComparer.OrdinalIgnoreCase);
            project.JointPositionsByPhase.Clear();foreach(var phase in phases)project.JointPositionsByPhase[phase.Key]=phase.Value;
            project.JointPositionsMetres=phases.Values.SelectMany(v=>v).Distinct().OrderBy(v=>v).ToArray();
            item.SetCableStructureProjection(refreshed.Blocks.Select(b=>b.Phase).ToArray(),refreshed.Blocks.Select(b=>b.Positions.ToArray()).ToArray());
            RefreshCalibrationJointMarkers();
            if(SelectedNodeKind=="cal-comparison")RefreshComparisonOverlay();
        }
        public bool BeginCalibrationEdit()
        {
            if(InvokeRequired)return (bool)Invoke(new Func<bool>(BeginCalibrationEdit));
            if(IsBusy||SelectedNodeKind=="cal-comparison"||selectedRecord==null||selectedRecord.Kind!="cal"||selectedWave==null)return Error(T("请先选择有效校准波形。","Select a valid calibration waveform first."));
            if(IsFormalCalibration(selectedRecord.Path))return Error(T("实机校准请在校准测试页面调整并重新保存，以同步质量判据、滤波和来源记录。","Edit and resave live calibration in the calibration page to retain quality and source metadata."));
            if(!ValidCalibrationGeometry())return Error(T("校准长度或时间标记无效，不能编辑。","Invalid calibration length or time markers."));
            editing=true;calibration.AllowCursorEditing=true;Buttons(true,false,false);SetStatus(T("拖动入射和反射标记，完成后点击保存校准。","Drag the incident and reflected markers, then save calibration."));return true;
        }
        public void SetCalibrationCursorSeconds(double incidentSeconds,double reflectionSeconds)
        {
            Ui(()=>{if(!editing)throw new InvalidOperationException("BeginCalibrationEdit must be called first.");if(!BrowserPlot.Finite(incidentSeconds)||!BrowserPlot.Finite(reflectionSeconds)||incidentSeconds<0||reflectionSeconds<=incidentSeconds||reflectionSeconds>selectedWave.X.Last())throw new ArgumentException("Invalid calibration marker range.");calibration.SetCursorPositions(incidentSeconds*1e6,reflectionSeconds*1e6);});
        }
        public void CancelCalibrationEdit(){Ui(()=>{if(saving)return;editing=false;calibration.AllowCursorEditing=false;if(selectedWave!=null)RenderWave();});}
        public bool SaveCalibrationEdit()
        {
            if(InvokeRequired)return (bool)Invoke(new Func<bool>(SaveCalibrationEdit));
            if(!editing||selectedRecord==null||selectedWave==null||IsBusy)return Error(T("当前没有可保存的校准编辑。","No calibration edit is ready to save."));
            double first=calibration.IncidentCursorX/1e6,last=calibration.ReflectionCursorX/1e6,length=selectedWave.CableLengthMetres;
            if(first<0||last<=first||last>selectedWave.X.Last())return Error(T("反射标记必须晚于入射标记，并处于波形内。","Reflection must follow the incident marker within the waveform."));
            try{if(selectedCalHash!=FileHash(selectedRecord.Path))return Error(T("校准文件在加载后发生变化，请重新加载。","Calibration file changed after loading; reload it."));}catch(Exception ex){return Error(ex.Message);}string root=projectPath,cal=selectedRecord.Path;return RunSave(()=>CalibrationPersistence.SaveCalibration(root,cal,first,last,length));
        }
        public bool CorrectCableLength(double newLengthMetres,string referenceCalibrationPath)
        {
            if(InvokeRequired)return (bool)Invoke(new Func<double,string,bool>(CorrectCableLength),newLengthMetres,referenceCalibrationPath);
            if(project==null||IsBusy||editing)return Error(T("请先加载项目并完成当前编辑。","Load a project and finish the current edit first."));
            if(!BrowserPlot.Finite(newLengthMetres)||newLengthMetres<=0)return Error(T("电缆长度必须大于零。","Cable length must be positive."));
            if(IsFormalCalibration(referenceCalibrationPath))return Error(T("请在校准测试页面修正实机校准的电缆长度。","Correct live-calibration geometry on the calibration page."));
            string root=projectPath;return RunSave(()=>CalibrationPersistence.CorrectCableLength(root,newLengthMetres,referenceCalibrationPath),true);
        }
        public bool SaveJointData(double[] positions,string[] attachmentTypes,string[] bodyTypes)
        {
            if(InvokeRequired)return (bool)Invoke(new Func<double[],string[],string[],bool>(SaveJointData),positions,attachmentTypes,bodyTypes);
            if(project==null||IsBusy||editing)return Error(T("请先加载项目并完成当前编辑。","Load a project and finish the current edit first."));
            if(positions==null||attachmentTypes==null||bodyTypes==null||positions.Length!=attachmentTypes.Length||positions.Length!=bodyTypes.Length)return Error("Joint array lengths must match.");
            var joints=positions.Select((p,i)=>new CableJoint{PositionMetres=p,AttachmentType=attachmentTypes[i],BodyType=bodyTypes[i],IsEndpoint=i==0||i==positions.Length-1}).ToArray();string root=projectPath;return RunSave(()=>CalibrationPersistence.SaveJoints(root,joints),true);
        }
        bool RunSave(Func<PersistenceResult> operation,bool cableStructureChanged=false)
        {
            if(saving)return false;saving=true;lastError="";int generation=projectGeneration,selection=++selectionGeneration;tree.Enabled=false;actions.Enabled=false;SetStatus(T("正在备份并保存项目文件…","Backing up and saving project files…"));string root=projectPath,selected=SelectedFilePath;
            Task.Run(()=>{try{var result=operation();Post(()=>{if(IsDisposed||generation!=projectGeneration||selection!=selectionGeneration||!String.Equals(root,projectPath,StringComparison.OrdinalIgnoreCase))return;saving=false;editing=false;calibration.AllowCursorEditing=false;tree.Enabled=true;actions.Enabled=true;LastBackupDirectory=result.BackupDirectory;LastChangedFileCount=result.ChangedFileCount;saveNotice=T("已保存文件数：","Files saved: ")+result.ChangedFileCount+Environment.NewLine+T("备份目录：","Backup folder: ")+result.BackupDirectory+Environment.NewLine+string.Join(Environment.NewLine,result.Warnings??new string[0]);pendingSelection=selected;saveNoticeRoot=root;BeginLoadProject(root);try{ProjectSaved?.Invoke(this,EventArgs.Empty);}finally{if(cableStructureChanged)CableStructureChanged?.Invoke(this,EventArgs.Empty);}});}
                catch(Exception ex){Post(()=>{if(IsDisposed||generation!=projectGeneration||selection!=selectionGeneration||!String.Equals(root,projectPath,StringComparison.OrdinalIgnoreCase))return;saving=false;tree.Enabled=true;actions.Enabled=true;Error(ex.Message);information.Text+=Environment.NewLine+T("保存失败：","Save failed: ")+ex.Message;});}});return true;
        }
        static bool IsFormalCalibration(string path){return HVX.CalibrationStorage.LiveCalibrationRecord.IsArchiveFile(path);}
        async void ShowJoints(double proposed)
        {
            if(project==null||IsBusy)return;
            EnsureJointUiContext();
            if(SelectedNodeKind=="cal-comparison"&&!CanUseComparisonGeometry()){Error(T("仅相容的平均校准波形可按距离添加接头，请先完成平均。","Complete a compatible calibration mean before adding joints by distance."));return;}
            if(editing){SetStatus(T("请先保存或取消校准编辑，再添加接头。","Save or cancel calibration edits before editing joints."));return;}
            string root=projectPath;int generation=projectGeneration,selection=selectionGeneration;
            try
            {
                var snapshot=await PrepareJointEditorSnapshotAsync(System.Threading.CancellationToken.None);var owner=FindForm();
                using(var dialog=new CableJointDialog(snapshot,proposed,SelectedNodeKind=="cal-comparison"?comparisonGeometry.Phase:selectedRecord==null?null:selectedRecord.Phase,languageIndex,owner==null?null:owner.Icon))
                {
                    dialog.ConfigureCommitAsync((plan,operation,token)=>CommitJointDialogAsync(plan,operation,root,generation,selection,token));
                    dialog.ShowDialog(owner);
                }
            }
            catch(OperationCanceledException){ }
            catch(Exception ex){if(!IsDisposed&&generation==projectGeneration&&selection==selectionGeneration)ReportJointSaveFailure(ex.Message);}
        }
        bool SaveJointPlan(CableJointPlan plan,string expectedRoot,int generation,int selection)
        {
            if(project==null||IsBusy||editing||(SelectedNodeKind=="cal-comparison"&&!CanUseComparisonGeometry())||generation!=projectGeneration||selection!=selectionGeneration||!String.Equals(expectedRoot,projectPath,StringComparison.OrdinalIgnoreCase))
                return Error(T("项目或当前记录已改变，请重新打开添加接头窗口。","Project or selection changed; reopen the add-joint dialog."));
            if(plan==null||!String.Equals(Path.GetDirectoryName(plan.CablePath),Path.GetFullPath(projectPath).TrimEnd(Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar),StringComparison.OrdinalIgnoreCase))
                return Error(T("接头修改与当前项目不一致，未保存。","Joint edit does not belong to the current project; nothing was saved."));
            return RunJointSave(plan);
        }
        bool RunJointSave(CableJointPlan plan)
        {
            if(saving)return false;
            saving=true;lastError="";LastChangedFileCount=0;LastBackupDirectory="";
            int generation=projectGeneration,selection=++selectionGeneration;string root=projectPath;
            tree.Enabled=false;actions.Enabled=false;
            SetStatus(T("正在备份并保存接头…","Backing up and saving joint…"));
            Task.Run(()=>{
                CableJointCommitResult committed;
                try{committed=CableJointStore.CommitWithResult(plan);}
                catch(Exception ex){Post(()=>{
                    if(IsDisposed||generation!=projectGeneration||selection!=selectionGeneration||!String.Equals(root,projectPath,StringComparison.OrdinalIgnoreCase))return;
                    saving=false;tree.Enabled=true;actions.Enabled=true;ReportJointSaveFailure(ex.Message);
                });return;}
                string backup=committed.BackupPath;
                CableJointSnapshot refreshed=null;string refreshFailure="";
                try{refreshed=CableJointStore.Read(root);}catch(Exception ex){refreshFailure=ex.Message;}
                Post(()=>{
                    if(IsDisposed||generation!=projectGeneration||selection!=selectionGeneration||!String.Equals(root,projectPath,StringComparison.OrdinalIgnoreCase))return;
                    saving=false;tree.Enabled=true;actions.Enabled=true;
                    LastBackupDirectory=Path.GetDirectoryName(backup);LastChangedFileCount=1;
                    try
                    {
                        if(refreshed!=null)
                        {
                            // A joint-only edit changes CABLE metadata, not waveform data.
                            // Keep the selected record, cursors and zoom rather than scan
                            // every CAL/DEM/LOC and later overwrite the completion notice.
                            ApplyCableJointSnapshot(refreshed);
                        }
                        else calibration.SetCablePositions(new double[0]);
                    }
                    catch(Exception ex){refreshFailure=ex.Message;calibration.SetCablePositions(new double[0]);}
                    string success=JointChangeCaption(plan.OperationCode)+plan.Name+" · "+plan.PositionM.ToString("0.0",CultureInfo.InvariantCulture)+" m · "+String.Join(", ",plan.Phases);
                    string notice=success+Environment.NewLine+T("电缆结构备份：","Cable structure backup: ")+backup;
                    if(!String.IsNullOrEmpty(refreshFailure))
                    {
                        lastError=T("文件已保存，但接头标记刷新失败，请重新加载项目：","The file was saved, but joint markers could not be refreshed. Reload the project: ")+refreshFailure;
                        notice+=Environment.NewLine+lastError;SetStatus(lastError);
                    }
                    else SetStatus(success+T("；当前波形接头标记已更新。","; current waveform joint markers updated."));
                    if(!String.IsNullOrEmpty(committed.Warning))
                    {lastError=committed.Warning;notice+=Environment.NewLine+committed.Warning;SetStatus(committed.Warning);}
                    information.Text=notice+Environment.NewLine+Environment.NewLine+information.Text;AdjustInformationHeight();
                    try{try{ProjectSaved?.Invoke(this,EventArgs.Empty);}finally{CableStructureChanged?.Invoke(this,EventArgs.Empty);}}
                    catch(Exception ex){lastError=T("接头已保存，但宿主通知失败：","Joint saved, but host notification failed: ")+ex.Message;information.Text+=Environment.NewLine+lastError;SetStatus(lastError);}
                });
            });return true;
        }
        string JointChangeCaption(string operation)
        {return operation=="RemoveJoint"?T("接头已删除：","Joint removed: "):operation=="EditJoint"?T("接头已保存（修改）：","Joint saved (updated): "):T("接头已保存（添加）：","Joint saved (added): ");}
        // Compatibility seam for older callers/tests. Interactive callers await
        // CommitJointDialogAsync, so completion means the transaction completed,
        // not merely that a background write has been queued.
        void CommitJointDialog(CableJointPlan plan,string operation,string root,int generation,int selection)
        {
            if(project==null||IsBusy||editing||generation!=projectGeneration||selection!=selectionGeneration||!String.Equals(root,projectPath,StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(T("项目或记录已改变，请重新打开接头编辑。","Project or selection changed; reopen the joint editor."));
            if(plan==null||!String.Equals(Path.GetDirectoryName(plan.CablePath),Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar),StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Joint plan does not belong to the current project.");
            if(SelectedNodeKind=="cal-comparison")RequireComparisonProjectGeometry(CableJointStore.Read(root));
            CableJointCommitResult committed;
            LastChangedFileCount=0;LastBackupDirectory="";saving=true;tree.Enabled=actions.Enabled=false;
            try{committed=CableJointStore.CommitWithResult(plan);}
            finally{saving=false;tree.Enabled=actions.Enabled=true;}
            FinishJointDialogCommit(plan,operation,root,committed,null,"");
        }
        void FinishJointDialogCommit(CableJointPlan plan,string operation,string root,CableJointCommitResult committed,CableJointSnapshot refreshed,string refreshFailure)
        {
            // From here the data is committed. Refresh/observer failures are
            // warnings, never a false 'not saved' error followed by a retry.
            LastBackupDirectory=Path.GetDirectoryName(committed.BackupPath);LastChangedFileCount=1;lastError="";
            string notice=JointChangeCaption(operation)+plan.Name+" · "+plan.PositionM.ToString("0.0",CultureInfo.InvariantCulture)+" m"+Environment.NewLine+T("电缆结构备份：","Cable structure backup: ")+committed.BackupPath;
            try{if(!String.IsNullOrEmpty(refreshFailure))throw new IOException(refreshFailure);ApplyCableJointSnapshot(refreshed??CableJointStore.Read(root));}
            catch(Exception ex){lastError=T("已保存，但标记刷新失败，请重新加载：","Saved, but marker refresh failed; reload: ")+ex.Message;}
            if(!String.IsNullOrEmpty(committed.Warning))lastError+=(lastError.Length>0?Environment.NewLine:"")+committed.Warning;
            try{try{ProjectSaved?.Invoke(this,EventArgs.Empty);}finally{CableStructureChanged?.Invoke(this,EventArgs.Empty);}}
            catch(Exception ex){lastError+=(lastError.Length>0?Environment.NewLine:"")+T("已保存，但宿主通知失败：","Saved, but host notification failed: ")+ex.Message;}
            try {
                if(!IsDisposed&&!Disposing){information.Text=notice+(lastError.Length>0?Environment.NewLine+lastError:"")+Environment.NewLine+information.Text;AdjustInformationHeight();SetStatus(lastError.Length>0?lastError:notice);}
            } catch(Exception ex) {
                // The transaction has already committed. Do not let a disposed
                // child control or layout callback turn it into a retryable save failure.
                lastError+=(lastError.Length>0?Environment.NewLine:"")+T("已保存，但状态显示失败，请重新加载：","Saved, but status display failed; reload: ")+ex.Message;
            }
        }
        void ReportJointSaveFailure(string message)
        {
            string notice=T("接头未保存：","Joint was not saved: ")+message;
            Error(notice);information.Text=notice+Environment.NewLine+Environment.NewLine+information.Text;AdjustInformationHeight();
        }
        void ShowLengthDialog()
        {
            if(project==null||IsBusy||editing)return;
            using(var dialog=CreateLengthDialog(project.CableLengthMetres,project.Calibrations.Select(c=>c.Name).ToArray(),Screen.FromControl(this).WorkingArea))
            {
                var length=(NumericUpDown)dialog.Controls.Find("CorrectCableLengthValue",true).Single();
                var reference=(ComboBox)dialog.Controls.Find("CorrectCableLengthReference",true).Single();
                if(dialog.ShowDialog(FindForm())==DialogResult.OK)CorrectCableLength((double)length.Value,reference.SelectedIndex==0?null:project.Calibrations[reference.SelectedIndex-1].Path);
            }
        }

        // Pure dialog construction is also used by offline layout checks; it never
        // edits project data. Size every row from the actual font before showing.
        Form CreateLengthDialog(double currentLength,string[] calibrationNames,Rectangle workingArea)
        {
            var owner=FindForm();
            var dialog=new Form{Name="CorrectCableLengthDialog",Text=T("电缆长度修正","Correct cable length"),Font=Font,
                AutoScaleMode=AutoScaleMode.None,FormBorderStyle=FormBorderStyle.FixedDialog,
                StartPosition=owner==null?FormStartPosition.CenterScreen:FormStartPosition.CenterParent,
                MaximizeBox=false,MinimizeBox=false,ShowInTaskbar=false,BackColor=Color.White};
            HVX.Presentation.OemUiBranding.Apply(dialog);
            var note=new Label{Name="CorrectCableLengthNote",Text=T("保持原校准时间差不变，重新计算波速并同步保存测试、定位文件。","Keep calibration delays unchanged, recalculate velocity, and update test/location files."),UseCompatibleTextRendering=false};
            var label=new Label{Name="CorrectCableLengthLabel",Text=T("新电缆长度 / m","New cable length / m"),UseCompatibleTextRendering=false};
            var refLabel=new Label{Name="CorrectCableLengthReferenceLabel",Text=T("校准匹配","Calibration matching"),UseCompatibleTextRendering=false};
            var hint=new Label{Name="CorrectCableLengthHint",Text=T("内部接头位置保持不变。保存前自动创建完整备份。","Internal joint distances stay unchanged. Files are backed up before saving."),UseCompatibleTextRendering=false};
            var length=new NumericUpDown{Name="CorrectCableLengthValue",Font=Font,DecimalPlaces=1,Increment=.1m,Minimum=.1m,Maximum=1000000,
                Value=(decimal)Math.Max(.1,Math.Min(1000000,BrowserPlot.Finite(currentLength)?currentLength:1))};
            var reference=new ComboBox{Name="CorrectCableLengthReference",Font=Font,DropDownStyle=ComboBoxStyle.DropDownList};
            reference.Items.Add(T("按引用和相别自动匹配","Match references and phases"));reference.Items.AddRange(calibrationNames??new string[0]);reference.SelectedIndex=0;
            var ok=new BrowserButton{Name="CorrectCableLengthSave",Text=T("保存","Save"),Font=Font,DialogResult=DialogResult.OK};
            var cancel=new BrowserButton{Name="CorrectCableLengthCancel",Text=T("取消","Cancel"),Font=Font,DialogResult=DialogResult.Cancel};
            int labelWidth=Math.Max(TextRenderer.MeasureText(label.Text,Font).Width,TextRenderer.MeasureText(refLabel.Text,Font).Width)+8;
            int inputWidth=Math.Max(190,TextRenderer.MeasureText(reference.Items[0].ToString(),Font).Width+36);
            int width=Math.Min(Math.Max(530,labelWidth+inputWidth+54+SystemInformation.VerticalScrollBarWidth),Math.Max(240,workingArea.Width-48));
            int contentWidth=width-SystemInformation.VerticalScrollBarWidth-2;
            int innerWidth=contentWidth-40;
            labelWidth=Math.Min(labelWidth,Math.Max(90,innerWidth/2));
            int fieldWidth=innerWidth-labelWidth-12;
            Func<Label,int,int> measure=(item,w)=>TextRenderer.MeasureText(item.Text,Font,new Size(w,int.MaxValue),TextFormatFlags.WordBreak|TextFormatFlags.TextBoxControl).Height+6;
            int y=16;
            note.SetBounds(20,y,innerWidth,measure(note,innerWidth));y=note.Bottom+14;
            int rowHeight=Math.Max(length.PreferredSize.Height,measure(label,labelWidth));
            label.SetBounds(20,y,labelWidth,rowHeight);length.SetBounds(32+labelWidth,y,fieldWidth,length.PreferredSize.Height);y+=rowHeight+12;
            rowHeight=Math.Max(reference.PreferredSize.Height,measure(refLabel,labelWidth));
            refLabel.SetBounds(20,y,labelWidth,rowHeight);reference.SetBounds(32+labelWidth,y,fieldWidth,reference.PreferredSize.Height);y+=rowHeight+14;
            hint.SetBounds(20,y,innerWidth,measure(hint,innerWidth));y=hint.Bottom+16;
            reference.DropDownWidth=Math.Min(Math.Max(fieldWidth,inputWidth),Math.Max(200,workingArea.Width-48));
            var content=new Panel{Name="CorrectCableLengthContent",Size=new Size(contentWidth,y),BackColor=Color.White};
            content.Controls.AddRange(new Control[]{note,label,length,refLabel,reference,hint});
            var scroll=new Panel{Name="CorrectCableLengthScroll",Dock=DockStyle.Fill,AutoScroll=true,BackColor=Color.White,AutoScrollMinSize=new Size(0,y)};scroll.Controls.Add(content);
            int buttonHeight=Math.Max(34,TextRenderer.MeasureText(ok.Text,Font).Height+14);
            int buttonWidth=Math.Max(110,Math.Max(TextRenderer.MeasureText(ok.Text,Font).Width,TextRenderer.MeasureText(cancel.Text,Font).Width)+32);
            var footer=new Panel{Name="CorrectCableLengthFooter",Dock=DockStyle.Bottom,Height=buttonHeight+24,BackColor=Color.White};
            ok.SetBounds(width-32-2*buttonWidth,10,buttonWidth,buttonHeight);cancel.SetBounds(width-20-buttonWidth,10,buttonWidth,buttonHeight);
            footer.Controls.AddRange(new Control[]{ok,cancel});dialog.Controls.Add(scroll);dialog.Controls.Add(footer);
            dialog.AcceptButton=ok;dialog.CancelButton=cancel;
            dialog.ClientSize=new Size(width,Math.Min(y+footer.Height,Math.Max(180,workingArea.Height-80)));
            return dialog;
        }
    }
}
