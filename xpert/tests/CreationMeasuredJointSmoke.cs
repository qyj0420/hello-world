using System;
using System.Collections;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Windows.Forms;

internal static class CreationMeasuredJointSmoke
{
    const BindingFlags All=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Static|BindingFlags.Instance;
    static Control module;static Type type;static int checks;
    static object Field(string name){return type.GetField(name,All).GetValue(module);}
    static object Get(string name){return type.GetProperty(name,All).GetValue(module,null);}
    static void Set(string name,object value){type.GetProperty(name,All).SetValue(module,value,null);}
    static object Call(string name,params object[] args){return type.GetMethods(All).Single(m=>m.Name==name&&m.GetParameters().Length==args.Length).Invoke(module,args);}
    static void Check(bool value,string name){if(!value)throw new InvalidOperationException(name);checks++;}
    static void Pump(){for(int i=0;i<5;i++)Application.DoEvents();}
    static void CheckJointGridReachable(string scenario)
    {
        var grid=(DataGridView)Field("cableGrid");
        var viewport=(ScrollableControl)grid.Parent.Parent;
        var rows=grid.Rows.Cast<DataGridViewRow>().Where(row=>row.Visible).Take(2).ToArray();
        Check(rows.Length==2,"layout fixture includes at least two grid rows: "+scenario);
        grid.FirstDisplayedScrollingRowIndex=rows[0].Index;
        viewport.ScrollControlIntoView(grid);Pump();
        int minimum=grid.ColumnHeadersHeight+rows.Sum(row=>row.Height);
        Check(grid.ClientSize.Height>=minimum&&grid.DisplayedRowCount(false)>=2,"grid contains header and two complete rows: "+scenario);
        Rectangle visible=viewport.RectangleToScreen(viewport.ClientRectangle);
        Check(visible.Contains(grid.RectangleToScreen(new Rectangle(0,0,grid.ClientSize.Width,grid.ColumnHeadersHeight))),"grid header reachable by outer vertical scroll: "+scenario);
        foreach(var row in rows)
        {
            Rectangle bounds=grid.GetRowDisplayRectangle(row.Index,false);
            Check(bounds.Height==row.Height&&visible.Contains(grid.RectangleToScreen(bounds)),"complete grid row reachable by outer vertical scroll: "+scenario+" row="+row.Index);
        }
        Check(!viewport.HorizontalScroll.Visible,"grid view needs no outer horizontal scroll: "+scenario);
    }
    static string Block(string phase,string material){return "0\t"+phase+"\t\t\r\n1\t始端\t户内冷缩\t0m\r\n2\t本体\t"+material+"\t\r\n1\t终端\t户内冷缩\t100m\r\n";}
    static string Fixture(string root,string name,string mode,string cable,bool legacy=true)
    {
        string path=Path.Combine(root,name);Directory.CreateDirectory(path);
        string item="[ITEM]\r\n始端地点: = \"QA start\"\r\n终端地点: = \"QA end\"\r\n相电压: = \"8.7kV\"\r\n电缆芯数: = \""+mode+"\"\r\nA相电缆长度: = \"100\"\r\nB相电缆长度: = \"100\"\r\nC相电缆长度: = \"100\"\r\n";
        File.WriteAllText(Path.Combine(path,"ITEM.in"),item,new UTF8Encoding(false));File.WriteAllText(Path.Combine(path,"CABLE.in"),cable,new UTF8Encoding(false));
        if(legacy){File.WriteAllBytes(Path.Combine(path,"original.cal"),new byte[]{1,2,3,4,5});File.WriteAllBytes(Path.Combine(path,"original.dem"),new byte[]{8,7,6});}return path;
    }
    sealed class QaForm:Form{protected override bool ShowWithoutActivation{get{return true;}}}
    [STAThread] static int Main(string[] args)
    {
        try
        {
            Application.EnableVisualStyles();
            string root=Path.Combine(Path.GetFullPath(args[1]),"creation-measured-joint-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
            var assembly=Assembly.LoadFrom(Path.Combine(Path.GetFullPath(args[0]),"modules","ProjectCreation","Xiongxin.OWTS.ProjectCreation.dll"));
            type=assembly.GetType("HVX.Controls.ProjectCreationControl",true);
            using(module=(Control)Activator.CreateInstance(type))using(var host=new QaForm {AutoScaleMode=AutoScaleMode.None,ClientSize=new Size(1256,620),ShowInTaskbar=false})
            {
                Set("EmbeddedMode",true);Set("ShowDialogMessages",false);Set("OnlineWeatherEnabled",false);Set("AutoRefreshWeatherOnHardwareSample",false);
                Set("SettingsPath",Path.Combine(root,"qa-settings.ini"));Set("DataRootDirectory",root);Set("LanguageIndex",0);
                module.Dock=DockStyle.Fill;host.Controls.Add(module);host.Show();Pump();
                string path=Fixture(root,"abc","三相单芯",Block("A相","XLPE-A")+Block("B相","油纸-B")+Block("C相","XLPE-C"));
                byte[] original=File.ReadAllBytes(Path.Combine(path,"CABLE.in")),item=File.ReadAllBytes(Path.Combine(path,"ITEM.in"));
                Check((bool)Call("LoadProject",path),"load acquired project");
                Check(!(bool)Get("CanEditCableGeometry")&&!((Control)Field("lengthANumber")).Enabled&&!((Control)Field("cableModeCombo")).Enabled,"length and core remain locked");
                Check(((Control)Field("addJointButton")).Enabled,"append joint button enabled");
                Check(((Control)Field("editJointButton")).Enabled&&((Control)Field("deleteJointButton")).Enabled,"explicit interior-joint edit/delete enabled while full geometry remains locked");
                Set("Notes","unsaved field survives additive joint");
                Check((bool)Call("AddJoint","A相","预制接头","",40d),"safe append API succeeds: "+Get("LastError"));
                string now=File.ReadAllText(Path.Combine(path,"CABLE.in"));
                Check(now.Contains("接头 1")&&now.Contains("1\t终端\t户内冷缩\t100m"),"new ordinal joint and original length retained");
                Check(now.EndsWith(Block("B相","油纸-B")+Block("C相","XLPE-C")),"unselected phases unchanged byte-for-byte");
                Check(now.Split(new[]{"XLPE-A"},StringSplitOptions.None).Length==3,"blank after-material preserves original split segment");
                Check(File.ReadAllBytes(Path.Combine(path,"ITEM.in")).SequenceEqual(item),"append never writes ITEM");
                Check(Directory.GetFiles(path,"*before-joint*").Any(file=>File.ReadAllBytes(file).SequenceEqual(original)),"exact pre-append backup exists");
                Check(File.ReadAllBytes(Path.Combine(path,"original.cal")).SequenceEqual(new byte[]{1,2,3,4,5})&&File.ReadAllBytes(Path.Combine(path,"original.dem")).SequenceEqual(new byte[]{8,7,6}),"raw files unchanged");
                Check((string)Get("Notes")=="unsaved field survives additive joint"&&(bool)Get("IsDirty"),"unrelated unsaved fields retained");
                Check(((double[])Call("GetPhaseJointPositions",0)).Contains(40d),"preview refreshed after append");
                byte[] post=File.ReadAllBytes(Path.Combine(path,"CABLE.in"));
                Check(!(bool)Call("AddJoint","A相","预制接头","",40d),"duplicate append rejected");
                Check(!(bool)Call("AddJoint","A相","预制接头","",110d),"out-of-range append rejected");
                Check(!(bool)Call("RemoveJoint","A相",-1),"invalid joint removal rejected");
                bool blocked=false;try{Call("SetPhaseCableLengths",90d,100d,100d);}catch(TargetInvocationException ex){blocked=ex.InnerException is InvalidOperationException;}
                Check(blocked,"length API still protected");Check(File.ReadAllBytes(Path.Combine(path,"CABLE.in")).SequenceEqual(post),"failed operations do not modify cable");
                Check((bool)Call("SaveProject"),"non-geometry metadata save succeeds: "+Get("LastError"));
                Check(File.ReadAllBytes(Path.Combine(path,"CABLE.in")).SequenceEqual(post),"metadata save does not reserialize cable");
                // The shared dialog's prepared plan may carry a user name without
                // the Chinese word for joint; the project preview must retain it.
                var store=assembly.GetType("HVX.ProjectCables.CableJointStore",true);object snapshot=store.GetMethod("Read",All).Invoke(null,new object[]{path});
                object plan=snapshot.GetType().GetMethod("Prepare",All).Invoke(snapshot,new object[]{new[]{"B","C"},60d,"Junction J2","冷缩接头","",""});
                Call("CommitMeasuredJoint",plan);
                Check(((double[])Call("GetPhaseJointPositions",1)).Contains(60d)&&((double[])Call("GetPhaseJointPositions",2)).Contains(60d),"custom-named joint visible for selected phases");
                byte[] itemBeforeConflict=File.ReadAllBytes(Path.Combine(path,"ITEM.in"));
                File.AppendAllText(Path.Combine(path,"CABLE.in"),"\r\n",new UTF8Encoding(false));
                byte[] externallyChangedCable=File.ReadAllBytes(Path.Combine(path,"CABLE.in"));
                Set("Notes","must not overwrite concurrently edited metadata");
                Check(!(bool)Call("SaveProject"),"metadata save detects concurrent cable change");
                Check(File.ReadAllBytes(Path.Combine(path,"ITEM.in")).SequenceEqual(itemBeforeConflict)&&File.ReadAllBytes(Path.Combine(path,"CABLE.in")).SequenceEqual(externallyChangedCable),"concurrent change preserved and ITEM untouched");
                Check((bool)Call("LoadProject",path),"reload after concurrent edit");
                Set("Notes","locked writer test");
                using(var held=new FileStream(Path.Combine(path,".DataBrowser.write.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None))
                    Check(!(bool)Call("SaveProject"),"metadata save shares exclusive editor lock");
                Check(File.ReadAllBytes(Path.Combine(path,"ITEM.in")).SequenceEqual(itemBeforeConflict),"locked writer never updates ITEM");
                Check((bool)Call("SaveProject"),"metadata save succeeds after lock releases");
                foreach(string changed in new[]{"ITEM.in","CABLE.in"})
                {
                    string conflict=Fixture(root,"append-conflict-"+changed,"三相单芯",Block("A相","XLPE-A")+Block("B相","XLPE-B")+Block("C相","XLPE-C"));
                    Check((bool)Call("LoadProject",conflict),"load additive concurrency fixture "+changed);
                    Set("Notes","unsaved local notes must remain");
                    if(changed=="ITEM.in")File.AppendAllText(Path.Combine(conflict,changed),"备注: = \"external metadata edit\"\r\n",new UTF8Encoding(false));
                    else File.WriteAllText(Path.Combine(conflict,changed),Block("A相","external-material")+Block("B相","XLPE-B")+Block("C相","XLPE-C"),new UTF8Encoding(false));
                    byte[] conflictItem=File.ReadAllBytes(Path.Combine(conflict,"ITEM.in")),conflictCable=File.ReadAllBytes(Path.Combine(conflict,"CABLE.in"));
                    Check(!(bool)Call("AddJoint","A相","预制接头","",30d),"append rejects metadata changed since load: "+changed);
                    Check(((string)Get("LastError")).Contains("重新加载"),"append conflict explains required reload");
                    Check((string)Get("Notes")=="unsaved local notes must remain"&&(bool)Get("IsDirty"),"append conflict keeps unsaved draft");
                    Check(File.ReadAllBytes(Path.Combine(conflict,"ITEM.in")).SequenceEqual(conflictItem)&&File.ReadAllBytes(Path.Combine(conflict,"CABLE.in")).SequenceEqual(conflictCable),"append conflict preserves external edits byte-for-byte");
                    object fresh=store.GetMethod("Read",All).Invoke(null,new object[]{conflict});
                    object freshPlan=fresh.GetType().GetMethod("Prepare",All).Invoke(fresh,new object[]{new[]{"A"},30d,"must-not-append","冷缩接头","",""});
                    bool directBlocked=false;try{Call("CommitMeasuredJoint",freshPlan);}catch(TargetInvocationException ex){directBlocked=ex.InnerException is InvalidOperationException;}
                    Check(directBlocked,"fresh externally based plan cannot bypass loaded baseline");
                    Check(!(bool)Call("SaveProject"),"metadata save after rejected append remains blocked: "+changed);
                    Check(File.ReadAllBytes(Path.Combine(conflict,"ITEM.in")).SequenceEqual(conflictItem)&&File.ReadAllBytes(Path.Combine(conflict,"CABLE.in")).SequenceEqual(conflictCable),"later save does not overwrite external metadata or cable");
                    Check(!Directory.GetFiles(conflict,"*before-joint*").Any(),"conflicted append creates no commit backup");
                }
                foreach(string changed in new[]{"ITEM.in","CABLE.in"})
                {
                    string conflict=Fixture(root,"refresh-conflict-"+changed,"三相单芯",Block("A相","XLPE-A")+Block("B相","XLPE-B")+Block("C相","XLPE-C"));
                    Check((bool)Call("LoadProject",conflict),"load post-commit concurrency fixture "+changed);
                    Set("Notes","unsaved notes before post-commit refresh");
                    object originalCableHash=Field("loadedJointCableHash"),originalItemHash=Field("loadedJointItemHash");
                    object fresh=store.GetMethod("Read",All).Invoke(null,new object[]{conflict});
                    object committedPlan=fresh.GetType().GetMethod("Prepare",All).Invoke(fresh,new object[]{new[]{"A"},30d,"committed-before-refresh","冷缩接头","",""});
                    store.GetMethod("Commit",All).Invoke(null,new[]{committedPlan});
                    // Deterministically simulate an external writer in the gap
                    // between the atomic append and the UI's metadata refresh.
                    if(changed=="ITEM.in")File.AppendAllText(Path.Combine(conflict,changed),"备注: = \"external after commit\"\r\n",new UTF8Encoding(false));
                    else File.AppendAllText(Path.Combine(conflict,changed),"\r\n",new UTF8Encoding(false));
                    byte[] conflictItem=File.ReadAllBytes(Path.Combine(conflict,"ITEM.in")),conflictCable=File.ReadAllBytes(Path.Combine(conflict,"CABLE.in"));
                    bool refreshBlocked=false;try{Call("RefreshAfterMeasuredJoint",true,committedPlan);}catch(TargetInvocationException ex){refreshBlocked=ex.InnerException is InvalidOperationException;}
                    Check(refreshBlocked,"post-commit refresh refuses external edit: "+changed);
                    Check(Equals(Field("loadedJointCableHash"),originalCableHash)&&Equals(Field("loadedJointItemHash"),originalItemHash),"failed refresh does not authorize external baseline");
                    Check((string)Get("Notes")=="unsaved notes before post-commit refresh"&&(bool)Get("IsDirty"),"failed refresh retains unsaved draft");
                    Check(!(bool)Call("SaveProject"),"save after conflicted refresh remains blocked: "+changed);
                    Check(File.ReadAllBytes(Path.Combine(conflict,"ITEM.in")).SequenceEqual(conflictItem)&&File.ReadAllBytes(Path.Combine(conflict,"CABLE.in")).SequenceEqual(conflictCable),"post-commit external edits survive later save attempt");
                }
                Check((bool)Call("LoadProject",path),"restore ordinary project for layout checks");
                Check((bool)Call("AddJoint","A相","预制接头","",60d),"add second QA joint for complete-row layout checks");
                ((ComboBox)Field("selectedPhaseCombo")).SelectedIndex=0;Pump();
                foreach(Size size in new[]{new Size(1065,500),new Size(1256,620),new Size(1850,990)})
                {
                    host.ClientSize=size;Call("SetOptionalSectionExpanded","JointsAndBody",true);Pump();
                    var editor=module.Controls.Find("JointEditorLayout",true).OfType<TableLayoutPanel>().Single();
                    var fields=(TableLayoutPanel)editor.GetControlFromPosition(0,1);var actions=(Control)editor.GetControlFromPosition(0,2);
                    foreach(TableLayoutPanel field in fields.Controls.OfType<TableLayoutPanel>())
                    {
                        Control frame=field.Controls.Cast<Control>().First(c=>c.GetType().Name=="ProjectCreationInputFrame");
                        Check(frame.Bottom<=field.Height+1,"joint input row fully contains frame: "+size);
                        Control input=frame.Controls[0];Check(input.Height>=input.Font.Height,"input glyph height fits: "+size);
                    }
                    Check(fields.Bottom<=actions.Top,"joint fields never overlap Add button: "+size);
                    Check(!editor.HorizontalScroll.Visible,"no horizontal joint editor scroll: "+size);
                    using(var image=new Bitmap(module.Width,module.Height)){module.DrawToBitmap(image,module.ClientRectangle);image.Save(Path.Combine(root,"joint-editor-"+size.Width+"x"+size.Height+".png"));}
                    CheckJointGridReachable(size.ToString());
                    using(var image=new Bitmap(module.Width,module.Height)){module.DrawToBitmap(image,module.ClientRectangle);image.Save(Path.Combine(root,"joint-grid-"+size.Width+"x"+size.Height+".png"));}
                }
                host.ClientSize=new Size(1065,500);Pump();
                foreach(float points in new[]{12f,15f,20f})
                {
                    var editor=module.Controls.Find("JointEditorLayout",true).OfType<TableLayoutPanel>().Single();
                    var fields=(TableLayoutPanel)editor.GetControlFromPosition(0,1);
                    foreach(TableLayoutPanel field in fields.Controls.OfType<TableLayoutPanel>())
                    {
                        foreach(FlowLayoutPanel caption in field.Controls.OfType<FlowLayoutPanel>())foreach(Control label in caption.Controls)label.Font=new Font("Microsoft YaHei UI",points);
                        Control frame=field.Controls.Cast<Control>().First(c=>c.GetType().Name=="ProjectCreationInputFrame");frame.Controls[0].Font=new Font("Microsoft YaHei UI",points);
                    }
                    Pump();
                    foreach(TableLayoutPanel field in fields.Controls.OfType<TableLayoutPanel>())
                    {
                        Control frame=field.Controls.Cast<Control>().First(c=>c.GetType().Name=="ProjectCreationInputFrame");
                        Check(frame.Bottom<=field.Height+1&&frame.Controls[0].Height>=frame.Controls[0].Font.Height,"scaled editor font fits row: "+points);
                    }
                    Check(fields.Bottom<=editor.GetControlFromPosition(0,2).Top,"scaled fields do not overlap button: "+points);
                    var viewport=(ScrollableControl)editor.Parent;viewport.ScrollControlIntoView((Control)Field("addJointButton"));Pump();
                    Control add=(Control)Field("addJointButton");
                    Check(viewport.RectangleToScreen(viewport.ClientRectangle).Contains(add.RectangleToScreen(add.ClientRectangle)),"scaled Add button fully reachable by vertical scroll: "+points);
                    using(var image=new Bitmap(module.Width,module.Height)){module.DrawToBitmap(image,module.ClientRectangle);image.Save(Path.Combine(root,"joint-editor-font"+points+".png"));}
                    Check(!viewport.HorizontalScroll.Visible,"scaled editor has no horizontal scrollbar: "+points);
                    var grid=(DataGridView)Field("cableGrid");
                    grid.Font=new Font("Microsoft YaHei UI",points);
                    grid.ColumnHeadersDefaultCellStyle.Font=grid.Font;
                    grid.ColumnHeadersHeight=Math.Max(28,grid.Font.Height+8);
                    foreach(DataGridViewRow row in grid.Rows)row.Height=Math.Max(28,grid.Font.Height+8);
                    Pump();CheckJointGridReachable("font "+points);
                    using(var image=new Bitmap(module.Width,module.Height)){module.DrawToBitmap(image,module.ClientRectangle);image.Save(Path.Combine(root,"joint-grid-font"+points+".png"));}
                    grid.FirstDisplayedScrollingColumnIndex=grid.Columns["MaterialBefore"].Index;Pump();
                    foreach(string columnName in new[]{"MaterialBefore","Material"})
                    {
                        var column=grid.Columns[columnName];var bounds=grid.GetCellDisplayRectangle(column.Index,-1,false);
                        Check(bounds.Width==column.Width&&bounds.Left>=0&&bounds.Right<=grid.ClientSize.Width,"full insulation header reachable at font "+points+" / "+columnName);
                    }
                    using(var image=new Bitmap(module.Width,module.Height)){module.DrawToBitmap(image,module.ClientRectangle);image.Save(Path.Combine(root,"joint-insulation-font"+points+".png"));}
                    grid.FirstDisplayedScrollingColumnIndex=0;
                }
                string repeated=Fixture(root,"duplicate-a","三芯",Block("A相","材料一")+Block("A相","材料二")+Block("A相","材料三"));
                Check((bool)Call("LoadProject",repeated),"load duplicate-A measured project");
                Check((bool)Call("AddJoint","A相","预制接头","",35d),"append repeated-A blocks without renaming: "+Get("LastError"));
                string duplicate=File.ReadAllText(Path.Combine(repeated,"CABLE.in"));
                Check(duplicate.Split(new[]{"0\tA相"},StringSplitOptions.None).Length==4&&!duplicate.Contains("0\tB相")&&!duplicate.Contains("0\tC相"),"duplicate A never renamed or collapsed");
                Check(duplicate.Split(new[]{"接头 1"},StringSplitOptions.None).Length==4,"three-core ordinal joint written to all actual blocks");
                Check(duplicate.Contains("材料一")&&duplicate.Contains("材料二")&&duplicate.Contains("材料三"),"per-block materials retained");
                // Inject a view refresh failure only, after preparing a valid write.
                // The committed file must be reported saved, with reload guidance.
                object duplicateSnapshot=store.GetMethod("Read",All).Invoke(null,new object[]{repeated});
                object duplicatePlan=duplicateSnapshot.GetType().GetMethod("Prepare",All).Invoke(duplicateSnapshot,new object[]{new[]{"ALL"},50d,"接头_50","冷缩接头","",""});
                object oldGrid=Field("cableGrid");type.GetField("cableGrid",All).SetValue(module,null);
                try{Call("CommitMeasuredJoint",duplicatePlan);}finally{type.GetField("cableGrid",All).SetValue(module,oldGrid);}
                Check(File.ReadAllText(Path.Combine(repeated,"CABLE.in")).Contains("接头 2"),"atomic append survives injected view refresh failure");
                Check((bool)Get("LastOperationSucceeded")&&(string)Get("LastOperation")=="AddJoint","refresh failure does not report committed addition as failed");
                Check(((Label)Field("statusLabel")).Text.Contains("接头已保存")&&((Label)Field("statusLabel")).Text.Contains("重新加载"),"refresh failure explicitly asks reload instead of repeat append");
                Check(!(bool)Call("AddJoint","A相","预制接头","",65d),"post-refresh-failure writes blocked until reload");
                foreach(string modern in new[]{"Live","Acquisition"})
                {
                    string modernPath=Fixture(root,"modern-"+modern,"三相单芯",Block("A相","XLPE-A")+Block("B相","XLPE-B")+Block("C相","XLPE-C"),false);
                    string sampleFolder=modern=="Live"?Path.Combine(modernPath,"Calibration","Live"):Path.Combine(modernPath,"Calibration","Acquisition","session-qa");
                    Directory.CreateDirectory(sampleFolder);string sample=Path.Combine(sampleFolder,modern=="Live"?"A_5nC.hvcal":"capture_01.xxs");
                    byte[] sampleBytes={9,8,7,6};File.WriteAllBytes(sample,sampleBytes);
                    Check((bool)Call("LoadProject",modernPath),"load modern "+modern+"-only project");
                    Check((bool)Get("HasExistingTestData")&&!(bool)Get("CanEditCableGeometry")&&!((Control)Field("lengthANumber")).Enabled,"modern "+modern+" data locks length");
                    string state="; LastError="+Get("LastError")+"; IsBusy="+Get("IsBusy")+"; CanEditCableGeometry="+Get("CanEditCableGeometry");
                    Check(((Control)Field("addJointButton")).Enabled,"modern "+modern+"-only project append button enabled"+state);
                    bool added=(bool)Call("AddJoint","B相","预制接头","",45d);
                    Check(added,"modern "+modern+"-only project append succeeds; LastError="+Get("LastError")+"; IsBusy="+Get("IsBusy")+"; CanEditCableGeometry="+Get("CanEditCableGeometry"));
                    Check(File.ReadAllBytes(sample).SequenceEqual(sampleBytes),"modern "+modern+" original untouched");
                }
                string notSample=Fixture(root,"unrelated","三相单芯",Block("A相","XLPE-A")+Block("B相","XLPE-B")+Block("C相","XLPE-C"),false);
                string deep=Path.Combine(notSample,"Calibration","Acquisition","session","nested");Directory.CreateDirectory(deep);File.WriteAllBytes(Path.Combine(deep,"capture.xxs"),new byte[]{1});
                string other=Path.Combine(notSample,"Other");Directory.CreateDirectory(other);File.WriteAllBytes(Path.Combine(other,"foreign.hvcal"),new byte[]{2});
                Check((bool)Call("LoadProject",notSample),"load project with unrelated files");
                Check(!(bool)Get("HasExistingTestData")&&(bool)Get("CanEditCableGeometry"),"detection does not recurse into deeper or unrelated folders");
                // Deliberately exercise deep project paths, not a shorter QA
                // root that would hide MAX_PATH regressions in helper names.
                Check(root.Length<199,"QA root leaves space for an explicit 200-character project directory");
                string deepSafe=Fixture(root,new string('d',200-root.Length-1),"三相单芯",Block("A相","XLPE-A")+Block("B相","XLPE-B")+Block("C相","XLPE-C"));
                string oldToken="20260929T120000000-"+new string('a',32);
                Check(Path.Combine(deepSafe,"CABLE.before-joint-"+oldToken+".bak").Length>=260&&Path.Combine(deepSafe,"ITEM.before-metadata-"+oldToken+".bak").Length>=260,"legacy helper naming genuinely exceeds MAX_PATH in this fixture");
                byte[] deepCable=File.ReadAllBytes(Path.Combine(deepSafe,"CABLE.in")),deepItem=File.ReadAllBytes(Path.Combine(deepSafe,"ITEM.in"));
                Check((bool)Call("LoadProject",deepSafe),"load deep but supported project");
                Check((bool)Call("AddJoint","A相","预制接头","",35d),"deep append with compact GUID succeeds: "+Get("LastError"));
                string deepBackup=Directory.GetFiles(deepSafe,"CABLE.before-joint-*.bak").Single();Guid backupId;
                string jointId=Path.GetFileNameWithoutExtension(deepBackup).Substring("CABLE.before-joint-".Length);
                Check(deepBackup.Length<260&&jointId.Length==32&&Guid.TryParseExact(jointId,"N",out backupId),"joint backup retains full 128-bit GUID within MAX_PATH");
                Check(File.ReadAllBytes(deepBackup).SequenceEqual(deepCable),"deep joint backup preserves exact source bytes");
                Set("Notes","deep metadata update");
                Check((bool)Call("SaveProject"),"deep metadata update with compact GUID succeeds: "+Get("LastError"));
                string metadataBackup=Directory.GetFiles(deepSafe,"ITEM.before-metadata-*.bak").Single();
                string metadataId=Path.GetFileNameWithoutExtension(metadataBackup).Substring("ITEM.before-metadata-".Length);
                Check(metadataBackup.Length<260&&metadataId.Length==32&&Guid.TryParseExact(metadataId,"N",out backupId),"metadata backup retains full 128-bit GUID within MAX_PATH");
                Check(File.ReadAllBytes(metadataBackup).SequenceEqual(deepItem),"deep metadata backup preserves exact source bytes");
                Check(!Directory.GetFiles(deepSafe,"*.tmp").Any(),"deep successful saves leave no stage file");
                foreach(int directoryLength in new[]{230,239})
                {
                    string tooDeep=Fixture(root,new string('x',directoryLength-root.Length-1),"三相单芯",Block("A相","XLPE-A")+Block("B相","XLPE-B")+Block("C相","XLPE-C"));
                    byte[] sourceCable=File.ReadAllBytes(Path.Combine(tooDeep,"CABLE.in")),sourceItem=File.ReadAllBytes(Path.Combine(tooDeep,"ITEM.in"));
                    string[] sourceNames=Directory.GetFiles(tooDeep).OrderBy(p=>p).ToArray();
                    Check((bool)Call("LoadProject",tooDeep),"source metadata remains readable at directory length "+directoryLength);
                    Check(!(bool)Call("AddJoint","A相","预制接头","",35d)&&((string)Get("LastError")).Contains("路径过长"),"too-long joint helpers fail with explicit path guidance");
                    Set("Notes","must remain unsaved at overlong path");
                    Check(!(bool)Call("SaveProject")&&((string)Get("LastError")).Contains("路径过长"),"too-long metadata helpers fail with explicit path guidance");
                    Check(File.ReadAllBytes(Path.Combine(tooDeep,"CABLE.in")).SequenceEqual(sourceCable)&&File.ReadAllBytes(Path.Combine(tooDeep,"ITEM.in")).SequenceEqual(sourceItem),"path rejection preserves both source files byte-for-byte");
                    Check(Directory.GetFiles(tooDeep).OrderBy(p=>p).SequenceEqual(sourceNames),"path preflight creates no lock, stage or backup");
                    Check((bool)Get("IsDirty")&&(string)Get("Notes")=="must remain unsaved at overlong path","path rejection preserves the editable draft");
                }
            }
            Console.WriteLine("PASS "+checks+" assertions; "+root);return 0;
        }
        catch(Exception error){Console.Error.WriteLine(error);return 1;}
    }
}
