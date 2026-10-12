using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Windows.Forms;

// Synthetic, isolated UI fixtures. Does not authorize diagnostics, save project
// data, train a model, discover hardware or invoke any acquisition operation.
static class OwnedModuleDialogLayoutSmoke
{
    const BindingFlags All=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance;
    static int checks; static string output;
    static void Check(bool condition,string text){checks++;if(!condition)throw new Exception(text);}
    static object Make(Assembly assembly,string type,params object[] values){return Activator.CreateInstance(assembly.GetType(type,true),All,null,values,null);}
    static object Call(object value,string method,params object[] args){return value.GetType().GetMethod(method,All).Invoke(value,args);}
    static object Field(object value,string name){return value.GetType().GetField(name,All).GetValue(value);}
    static Control[] Children(Control root){return root.Controls.Cast<Control>().SelectMany(c=>new[]{c}.Concat(Children(c))).ToArray();}
    static void Fonts(Control root,Font font){root.Font=font;foreach(Control child in root.Controls)Fonts(child,font);}
    static void Pump(Form form){for(int i=0;i<4;i++){form.PerformLayout();Application.DoEvents();}}
    static void Show(Form form){form.ShowInTaskbar=false;form.Opacity=0;form.Show();Pump(form);}
    static void Buttons(Form form,string scenario)
    {
        foreach(var button in Children(form).OfType<Button>().Where(b=>b.Visible)){
            var size=TextRenderer.MeasureText(button.Text,button.Font,Size.Empty,TextFormatFlags.NoPadding|TextFormatFlags.SingleLine);
            Check(button.Width>=size.Width+12,scenario+" complete button caption: "+button.Text+" width="+button.Width+" needs="+size.Width);
            Check(button.Height>=size.Height+4,scenario+" complete button height: "+button.Text);
            Check(button.Parent.ClientRectangle.Contains(button.Bounds),scenario+" button in footer: "+button.Text+" "+button.Bounds+" / "+button.Parent.ClientRectangle);
            Check(form.ClientRectangle.Contains(form.RectangleToClient(button.RectangleToScreen(button.ClientRectangle))),scenario+" button inside window: "+button.Text);
        }
    }
    static string Digest(Icon icon){using(var data=new MemoryStream()){icon.Save(data);using(var hash=SHA256.Create())return Convert.ToBase64String(hash.ComputeHash(data.ToArray()));}}
    static void Brand(Form form,Assembly assembly)
    {
        var branding=assembly.GetType("HVX.Presentation.OemUiBranding",true);
        using(var expected=(Icon)branding.GetMethod("LoadIcon",BindingFlags.Static|BindingFlags.NonPublic).Invoke(null,null))
            Check(Digest(form.Icon)==Digest(expected),form.Text+" uses current neutral/OEM icon");
    }
    static void Shot(Form form,string name){using(var bitmap=new Bitmap(form.Width,form.Height)){form.DrawToBitmap(bitmap,new Rectangle(Point.Empty,form.Size));bitmap.Save(Path.Combine(output,name+".png"));}}
    static void Reach(Control control,Panel viewport,Form form,string label)
    {
        control.Select();viewport.ScrollControlIntoView(control);Pump(form);
        Rectangle area=viewport.RectangleToClient(control.RectangleToScreen(control.ClientRectangle));
        if(area.Top>=viewport.ClientSize.Height||area.Bottom<=0){Console.WriteLine(label+" area="+area+" viewport="+viewport.ClientSize+" scroll="+viewport.AutoScrollPosition+" maximum="+viewport.VerticalScroll.Maximum+" content="+control.Parent.Bounds);Shot(form,"unreachable-control");}
        Check(control.Height>=Math.Min(control.Font.Height*3+6,viewport.ClientSize.Height),label+" retains three readable text lines; height="+control.Height);
        Check(area.Top<viewport.ClientSize.Height&&area.Bottom>0,label+" can scroll into view");
    }
    static void ProbeRemaining(Assembly analysis,Assembly management)
    {
        using(var font=new Font("Microsoft YaHei UI",24f))using(var owner=(Control)Make(analysis,"HVX.PdReview.PdReviewControl")){
            owner.GetType().GetProperty("LanguageIndex").SetValue(owner,1,null);owner.Font=font;
            using(var leave=(Form)Call(owner,"CreateAnalysisLeaveDialog",false)){
                Show(leave);Fonts(leave,font);leave.Size=leave.MinimumSize;Pump(leave);
                var warning=Children(leave).OfType<Label>().Single();int need=TextRenderer.MeasureText(warning.Text,warning.Font,new Size(Math.Max(1,warning.Width),int.MaxValue),TextFormatFlags.WordBreak).Height;
                Console.WriteLine("REMAINING_LEAVE_WARNING:height="+warning.Height+";required="+need+";clipped="+(warning.Height<need));Shot(leave,"remaining-leave-warning-24pt");
            }
            var records=Array.CreateInstance(management.GetType("HVX.Controls.ProjectRecord",true),0);
            using(var merge=(Form)Make(management,"HVX.Controls.MergeReportDialog",records,1)){
                Show(merge);Fonts(merge,font);merge.Size=merge.MinimumSize;var grid=Children(merge).OfType<DataGridView>().Single();grid.Rows.Add(1,"2026-10-02 12:30:00","Synthetic project","Synthetic remark");Pump(merge);
                Console.WriteLine("REMAINING_MERGE_ROW:height="+grid.Rows[0].Height+";required="+grid.Rows[0].Cells["ProjectName"].PreferredSize.Height+";clipped="+(grid.Rows[0].Height<grid.Rows[0].Cells["ProjectName"].PreferredSize.Height));Shot(merge,"remaining-merge-row-24pt");
            }
        }
    }
    [STAThread]static int Main(string[] args)
    {
        try {
            Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
            string modules=Path.GetFullPath(args[0]);output=Path.GetFullPath(args[1]);Directory.CreateDirectory(output);
            bool analysisOnly=args.Length>2&&args[2]=="--analysis-only";
            var analysis=Assembly.LoadFrom(Path.Combine(modules,"DataAnalysis/HVX.PdReview.V1.0.dll"));
            var report=Assembly.LoadFrom(Path.Combine(modules,"ReportGenerator/Xiongxin.OWTS.ReportGenerator.dll"));
            var management=Assembly.LoadFrom(Path.Combine(modules,"ProjectManagement/Xiongxin.OWTS.ProjectManagement.dll"));
            if(args.Length>2&&args[2]=="--remaining-probe"){ProbeRemaining(analysis,management);return 0;}
            foreach(int lang in new[]{0,1,18})foreach(float points in new[]{9.5f,14.25f,18f,24f})using(var font=new Font("Microsoft YaHei UI",points))
            using(var owner=(Control)Make(analysis,"HVX.PdReview.PdReviewControl")) {
                owner.GetType().GetProperty("LanguageIndex").SetValue(owner,lang,null);owner.Font=font;
                string scenario="lang-"+lang+"-font-"+points;
                foreach(bool overwrite in new[]{false,true})using(var leave=(Form)Call(owner,"CreateAnalysisLeaveDialog",overwrite)){
                    Show(leave);Fonts(leave,font);leave.Size=leave.MinimumSize;Pump(leave);Brand(leave,analysis);
                    var viewport=(Panel)leave.Controls.Find("AnalysisLeaveViewport",true)[0];var warning=leave.Controls.Find("AnalysisLeaveWarning",true)[0];
                    int need=TextRenderer.MeasureText(warning.Text,warning.Font,new Size(Math.Max(1,warning.Width),int.MaxValue),TextFormatFlags.WordBreak).Height;
                    Check(warning.Height>=need,scenario+" unsaved-analysis explanation is complete");Check(viewport.Height>=160,scenario+" unsaved-analysis explanation has a usable scroll viewport");
                    foreach(var button in Children(leave).OfType<Button>()){
                        int textHeight=TextRenderer.MeasureText(button.Text,button.Font,new Size(Math.Max(1,button.Width-20),int.MaxValue),TextFormatFlags.WordBreak).Height;
                        Check(button.Height>=textHeight+8,scenario+" wrapped decision caption is complete: "+button.Text);
                        Check(leave.ClientRectangle.Contains(leave.RectangleToClient(button.RectangleToScreen(button.ClientRectangle))),scenario+" decision remains outside scrolling body");
                    }
                    Check(leave.Controls.Find("OverwriteCurrentAnalysis",true)[0].Enabled==overwrite,"overwrite availability remains unchanged");
                    Check(((Button)leave.CancelButton).DialogResult==DialogResult.Cancel,"leave cancellation semantics retained");
                    Check(leave.Size.Width<=Screen.FromControl(leave).WorkingArea.Width&&leave.Size.Height<=Screen.FromControl(leave).WorkingArea.Height,"leave window fits work area");
                    if(lang==1&&points==24&&overwrite)Shot(leave,"fixed-leave-minimum-24pt");
                }
                var mergeRecords=Array.CreateInstance(management.GetType("HVX.Controls.ProjectRecord",true),0);
                using(var merge=(Form)Make(management,"HVX.Controls.MergeReportDialog",mergeRecords,lang)){
                    Show(merge);Fonts(merge,font);merge.Size=merge.MinimumSize;
                    var grid=Children(merge).OfType<DataGridView>().Single();grid.Rows.Add(1,"2026-10-02 12:30:00","Synthetic project with a long title","Synthetic editable remark");Pump(merge);Brand(merge,management);Buttons(merge,scenario+" merge");
                    var viewport=(Panel)merge.Controls.Find("MergeReportViewport",true)[0];Check(viewport.Height>=160,scenario+" merge body viewport remains usable");Reach(grid,viewport,merge,scenario+" merge project table");
                    int requiredRow=grid.Rows[0].GetPreferredHeight(0,DataGridViewAutoSizeRowMode.AllCells,true);
                    Check(grid.Rows[0].Height>=requiredRow,scenario+" merge row fits actual column widths; actual="+grid.Rows[0].Height+" required="+requiredRow);
                    Check(grid.Rows[0].Height>=font.Height,scenario+" merge row fits a complete glyph");
                    int dateWidth=TextRenderer.MeasureText("2026-10-02 12:30:00",grid.Font,Size.Empty,TextFormatFlags.SingleLine|TextFormatFlags.NoPadding).Width+12;
                    Check(grid.Columns["TestTime"].Width>=dateWidth,scenario+" merge date remains complete after font changes");
                    foreach(var label in Children(merge).OfType<Label>()){
                        int required=TextRenderer.MeasureText(label.Text,label.Font,new Size(Math.Max(1,label.Width-label.Padding.Horizontal),int.MaxValue),TextFormatFlags.WordBreak).Height+label.Padding.Vertical;
                        Check(label.Height>=required,scenario+" merge label fits: "+label.Text);
                    }
                    var introduction=(TextBox)Field(merge,"introduction");Reach(introduction,viewport,merge,scenario+" merge introduction");Check(!introduction.ReadOnly,"merge introduction remains editable");Check(!grid.Columns["Remark"].ReadOnly,"merge remarks remain editable");
                    Check(merge.GetType().GetProperty("Items").GetValue(merge,null)==null,"layout inspection does not capture or merge projects");
                    Check(merge.Size.Width<=Screen.FromControl(merge).WorkingArea.Width&&merge.Size.Height<=Screen.FromControl(merge).WorkingArea.Height,"merge window fits work area");
                    if(lang==1&&points==24)Shot(merge,"fixed-merge-minimum-24pt");
                }
                using(var feedback=(Form)Call(owner,"CreateConclusionFeedbackDialog","Synthetic conclusion; no engineering result.")){
                    Show(feedback);Fonts(feedback,font);feedback.Size=feedback.MinimumSize;Pump(feedback);Brand(feedback,analysis);Buttons(feedback,scenario+" feedback");
                    var viewport=(Panel)feedback.Controls.Find("ReviewFeedbackViewport",true)[0];
                    foreach(string name in new[]{"ReviewAlgorithmConclusion","ReviewCorrectedConclusion","ReviewConclusionFeedbackNotes"})Reach(feedback.Controls.Find(name,true)[0],viewport,feedback,scenario+" "+name);
                    var corrected=(TextBox)feedback.Controls.Find("ReviewCorrectedConclusion",true)[0];corrected.Text="Edited synthetic conclusion";Pump(feedback);Check(corrected.Text=="Edited synthetic conclusion"&&!corrected.ReadOnly,"feedback remains editable");
                    if(lang==1&&points==18)Shot(feedback,"analysis-feedback-minimum-18pt");
                }
                var factory=owner.GetType().GetMethod("CreateSavedAnalysisDialog",All);var savedType=factory.GetParameters()[0].ParameterType.GetElementType();var saved=Array.CreateInstance(savedType,1);
                var entryInfo=Activator.CreateInstance(savedType);savedType.GetProperty("Name").SetValue(entryInfo,"Synthetic saved analysis with a deliberately long name; no file is read or saved",null);savedType.GetProperty("CreatedLocal").SetValue(entryInfo,new DateTime(2026,10,2,12,30,0),null);saved.SetValue(entryInfo,0);
                using(var picker=(Form)factory.Invoke(owner,new object[]{saved})){
                    Show(picker);Fonts(picker,font);picker.Size=new Size(580,390);Pump(picker);Brand(picker,analysis);Buttons(picker,scenario+" picker");
                    var list=(ListView)picker.Controls.Find("SavedAnalysisList",true)[0];var viewport=(Panel)picker.Controls.Find("SavedAnalysisViewport",true)[0];
                    Check(viewport.Height>=160,scenario+" saved analysis body retains usable viewport");Check(list.Height>=font.Height*4,scenario+" saved analysis list retains four rows");
                    Reach(list,viewport,picker,scenario+" analysis list");list.Items[0].Selected=true;Pump(picker);Check(((Button)picker.AcceptButton).Enabled,"selecting a synthetic row enables load without reading a project");
                    var detail=(TextBox)picker.Controls.Find("SelectedAnalysisName",true)[0];Reach(detail,viewport,picker,scenario+" selected analysis detail");Check(detail.Text.Contains("Synthetic saved analysis"),"selected full name remains accessible");
                    if(lang==1&&points==18)Shot(picker,"analysis-picker-580-18pt");
                }
                // The password-free validation edition has no diagnostic password
                // entry; exercise the dialog only in editions that still supply it.
                var passwordFactory=owner.GetType().GetMethod("CreateDiagnosticPasswordDialog",All);
                if(passwordFactory!=null)using(var password=(Form)passwordFactory.Invoke(owner,null)){
                    Show(password);Fonts(password,font);password.Size=password.MinimumSize;Pump(password);Brand(password,analysis);Buttons(password,scenario+" password");
                    var hint=password.Controls.Find("DiagnosticPasswordHint",true)[0];var entry=(TextBox)password.Controls.Find("DiagnosticPassword",true)[0];
                    Check(hint.Bottom<=entry.Top,"diagnostic hint does not overlap password");Check(entry.UseSystemPasswordChar,"diagnostic input remains protected");
                    if(lang==1&&points==18)Shot(password,"diagnostic-password-18pt");
                }
                using(var attachments=(Form)Call(owner,"CreateReportAttachmentDialog")){
                    Show(attachments);Fonts(attachments,font);attachments.Size=attachments.MinimumSize;Pump(attachments);Brand(attachments,analysis);Buttons(attachments,scenario+" attachments");
                    var viewport=(Panel)attachments.Controls.Find("ReviewAttachmentViewport",true)[0];var list=attachments.Controls.Find("ReportAttachmentSelection",true)[0];
                    Check(viewport.Height>=160,scenario+" attachment body retains usable viewport");Reach(list,viewport,attachments,scenario+" attachment list");
                    if(lang==1&&points==18)Shot(attachments,"analysis-attachments-minimum-18pt");
                }
                if(!analysisOnly){
                using(var range=(Form)Make(report,"HVX.ReportGenerator.VoltageRangeDialog",lang,null)){
                    Show(range);Fonts(range,font);range.Size=range.MinimumSize;Pump(range);Brand(range,report);Buttons(range,scenario+" voltage");
                    foreach(var label in Children(range).OfType<Label>().Where(l=>l.Name.StartsWith("VoltageRangeLabel"))){
                        int required=TextRenderer.MeasureText(label.Text,label.Font,new Size(label.Width,int.MaxValue),TextFormatFlags.WordBreak).Height;
                        Check(label.Height>=required,scenario+" entire voltage label including unit: "+label.Text+" height="+label.Height+" needs="+required);
                    }
                    var low=(NumericUpDown)Field(range,"minimum");var high=(NumericUpDown)Field(range,"maximum");low.Value=1;high.Value=1;((Button)range.AcceptButton).PerformClick();Pump(range);
                    Check(range.DialogResult!=DialogResult.OK&&((Label)Field(range,"message")).Text.Length>0,"equal voltage limits rejected with message");
                    high.Value=1.5m;((Button)range.AcceptButton).PerformClick();Check(range.DialogResult==DialogResult.OK,"valid voltage limits accepted");
                }
                var option=Make(report,"HVX.ReportGenerator.StatisticsRangeOption");var options=Array.CreateInstance(option.GetType(),1);options.SetValue(option,0);
                option.GetType().GetField("Interval",All).SetValue(option,Make(report,"HVX.ReportGenerator.VoltageInterval","synthetic","0.5U0–1.5U0",0.5,1.5));
                using(var stats=(Form)Make(report,"HVX.ReportGenerator.ReportSettingsDialog",lang,options)){
                    Show(stats);Fonts(stats,font);stats.Size=stats.MinimumSize;Pump(stats);Brand(stats,report);Buttons(stats,scenario+" report settings");
                    var grid=Children(stats).OfType<DataGridView>().Single();Check(grid.ColumnHeadersHeightSizeMode==DataGridViewColumnHeadersHeightSizeMode.AutoSize,"existing automatic report header measurement retained");
                    var viewport=(Panel)stats.Controls.Find("ReportStatisticsViewport",true)[0];Check(viewport.Height>=160,scenario+" report statistics body retains usable viewport");Reach(grid,viewport,stats,scenario+" report grid");
                    Check(grid.Rows[0].Height>=grid.Rows[0].GetPreferredHeight(0,DataGridViewAutoSizeRowMode.AllCells,true),scenario+" range caption row must fit its actual font and column width");
                    Check(grid.Rows[0].Height>=font.Height,scenario+" range caption row fits a complete glyph");
                    if(lang==1&&points==18)Shot(stats,"report-statistics-minimum-18pt");
                }
                }
            }
            for(int repeat=0;repeat<4;repeat++)foreach(int lang in new[]{0,1,18}){
                using(var import=(Form)Make(management,"HVX.Controls.ImportModeDialog",lang)){Brand(import,management);Show(import);}
                var records=Array.CreateInstance(management.GetType("HVX.Controls.ProjectRecord",true),0);
                using(var merge=(Form)Make(management,"HVX.Controls.MergeReportDialog",records,lang)){Brand(merge,management);Show(merge);}
                using(var after=new Form()){Show(after);Check(after.Icon!=null,"closing owned dialogs leaves default icon usable");}
            }
            Console.WriteLine("PASS OwnedModuleDialogLayoutSmoke checks="+checks+";reportDialogs="+(!analysisOnly)+";synthetic=true;hardware=false;persistence=false;physicalDpi=false;QA="+output);return 0;
        }catch(Exception error){Console.Error.WriteLine(error);return 1;}
    }
}
