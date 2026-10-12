using System;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using HVX.Controls;

internal static class ProjectInvariantTimeSmoke
{
    static int checks;
    static void Check(bool value,string message){checks++;if(!value)throw new Exception(message);}
    static readonly BindingFlags Private=BindingFlags.Instance|BindingFlags.NonPublic;
    static readonly DateTime Stamp=new DateTime(2026,9,29,16,31,47,DateTimeKind.Unspecified);
    static object Field(object o,string n){return o.GetType().GetField(n,Private).GetValue(o);}
    [STAThread] static int Main(string[] args)
    {
        var culture=Thread.CurrentThread.CurrentCulture;var ui=Thread.CurrentThread.CurrentUICulture;
        try {
            Application.EnableVisualStyles();
            string root=Path.Combine(Path.GetFullPath(args[0]),"project-invariant-time-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
            foreach(string code in new[]{"zh-CN","en-US","fr-FR","tr-TR","ar-SA","fa-IR","th-TH"}){
                Thread.CurrentThread.CurrentCulture=CultureInfo.GetCultureInfo(code);Thread.CurrentThread.CurrentUICulture=CultureInfo.GetCultureInfo(code);
                var draft=new ProjectDraft{StartName="人工始端名称",EndName="终端",CableMode="单相",LengthA=400,LengthB=400,LengthC=400,TestDateTime=Stamp,LayingDate=new DateTime(2024,2,29),
                    WeatherUpdatedAt=new DateTime(2026,9,29,8,31,47,DateTimeKind.Utc),HardwareSampleTime=Stamp,EnvironmentSampleTime=new DateTime(2026,9,29,8,31,47,DateTimeKind.Utc),GnssSampleTime=new DateTime(2026,9,29,8,30,47,DateTimeKind.Utc)};
                string path=ProjectCreationStorage.SaveNew(draft,Path.Combine(root,code));
                Check(Path.GetFileName(path)=="2026-09-29_16-31-47","new numeric directory, Gregorian invariant "+code);
                string text=File.ReadAllText(Path.Combine(path,"ITEM.in"),Encoding.GetEncoding(936));
                Check(text.Contains("日期: = \"2026-09-29\"")&&text.Contains("时间: = \"16:31:47\"")&&text.Contains("铺设时间: = \"2024-02-29\""),"invariant ITEM fields "+code);
                Check(text.Contains("2026-09-29T08:31:47.0000000Z"),"UTC sample preserves zone "+code);
                var loaded=ProjectCreationStorage.Load(path);
                Check(loaded.TestDateTime==Stamp&&loaded.LayingDate==draft.LayingDate,"new date roundtrip "+code);
                Check(loaded.WeatherUpdatedAt==draft.WeatherUpdatedAt&&loaded.WeatherUpdatedAt.Kind==DateTimeKind.Utc&&loaded.HardwareSampleTime.Kind==DateTimeKind.Unspecified,"sampling kind preserved "+code);
                var record=ProjectScanner.ReadProject(path);
                Check(record.TestTime==Stamp&&record.DisplayTestTime=="2026-09-29 16:31:47","scanner invariant display "+code);
                string second=ProjectCreationStorage.SaveNew(draft,Path.Combine(root,code));
                Check(Path.GetFileName(second)=="2026-09-29_16-31-47-01","collision suffix "+code);
                File.WriteAllText(Path.Combine(second,"ITEM.in"),"[ITEM]\r\n电缆芯数: = \"单相\"\r\n",Encoding.GetEncoding(936));
                Check(ProjectScanner.ReadProject(second).TestTime==Stamp,"new directory fallback retains seconds despite suffix "+code);
                string old=Path.Combine(root,code,"旧项目人工名称");Directory.CreateDirectory(old);
                File.Copy(Path.Combine(path,"CABLE.in"),Path.Combine(old,"CABLE.in"));
                string oldText=text.Replace("日期: = \"2026-09-29\"","日期: = \"2026年09月29日\"").Replace("时间: = \"16:31:47\"","时间: = \"16时31分\"")
                    .Replace("铺设时间: = \"2024-02-29\"","铺设时间: = \"2024年02月29日\"").Replace("2026-09-29T08:31:47.0000000Z","2026-09-29 08:31:47");
                File.WriteAllText(Path.Combine(old,"ITEM.in"),oldText,Encoding.GetEncoding(936));
                var oldDraft=ProjectCreationStorage.Load(old);
                Check(oldDraft.TestDateTime==new DateTime(2026,9,29,16,31,0)&&oldDraft.LayingDate==draft.LayingDate,"old Chinese date exact minute read "+code);
                using(var page=new ProjectCreationControl{ShowDialogMessages=false,SettingsPath=Path.Combine(root,code,"test-only-settings.ini")}){
                    Check(page.LoadProject(old)&&page.TestDateTime==oldDraft.TestDateTime,"real UI load does not truncate 16:31 to 16:00 "+code);
                    page.LanguageIndex=1;page.LanguageIndex=0;
                    Check(page.TestDateTime==oldDraft.TestDateTime,"language change does not mutate date "+code);
                    page.TestDateTime=Stamp;
                    Check(page.TestDateTime==Stamp&&((DateTimePicker)Field(page,"testDatePicker")).CustomFormat=="yyyy-MM-dd HH:mm:ss","real picker preserves seconds "+code);
                }
                Check(ProjectCreationStorage.UpdateExisting(oldDraft,old)==old&&Directory.Exists(old),"save does not rename old/manual directory "+code);
                Check(ProjectCreationStorage.Load(old).TestDateTime==oldDraft.TestDateTime,"old-to-new numeric save keeps instant "+code);
                DateTime parsed;
                Check(ProjectScanner.TryParseTime("2026年09月29日16时31分-02",out parsed)&&parsed==oldDraft.TestDateTime,"legacy collision compatible "+code);
                Check(ProjectScanner.TryParseTime("2026年09月29日16时31分47秒",out parsed)&&parsed==Stamp,"legacy seconds retained "+code);
                Check(ProjectScanner.TryParseTime("2026-09-29-16'31'47.12-02",out parsed)&&parsed==Stamp.AddMilliseconds(120),"complete legacy apostrophe timestamp and suffix "+code);
                Check(ProjectScanner.TryParseTime("2026-05-05T08:31:17Z",out parsed)&&parsed==new DateTime(2026,5,5,8,31,17,DateTimeKind.Utc)&&parsed.Kind==DateTimeKind.Utc,"whole-second ISO UTC preserves second and kind "+code);
                Check(ProjectScanner.TryParseTime("2026-05-05T16:31:17+08:00",out parsed)&&parsed.ToUniversalTime()==new DateTime(2026,5,5,8,31,17,DateTimeKind.Utc),"whole-second ISO offset preserves instant "+code);
                Check(ProjectScanner.TryParseTime("2026-05-05T08:31:17.1234567Z",out parsed)&&parsed.Ticks==new DateTime(2026,5,5,8,31,17,DateTimeKind.Utc).AddTicks(1234567).Ticks&&parsed.Kind==DateTimeKind.Utc,"fractional ISO retains all seven digits "+code);
                Check(ProjectScanner.TryParseTime("2026-05-05T08:31:17.12Z",out parsed)&&parsed.ToUniversalTime()==new DateTime(2026,5,5,8,31,17,DateTimeKind.Utc).AddMilliseconds(120),"short ISO fraction supported "+code);
                foreach(string manual in new[]{"人工名称2026年09月29日16时31分","2026-09-29_16-31-47复测","2026-05-05T08:31:17Z extra","2026-02-30_16-31-47","2026-05-05T08:31:17+99:00"})
                    Check(!ProjectScanner.TryParseTime(manual,out parsed),"no partial/manual/invalid date guessing "+code+" "+manual);
                string manualPath=Path.Combine(root,code,"人工名称2026年09月29日16时31分");Directory.CreateDirectory(manualPath);
                File.Copy(Path.Combine(second,"ITEM.in"),Path.Combine(manualPath,"ITEM.in"));File.Copy(Path.Combine(second,"CABLE.in"),Path.Combine(manualPath,"CABLE.in"));
                Check(!ProjectScanner.ReadProject(manualPath).HasTestTime,"real scanner directory fallback does not invent manual-name time "+code);
            }
            var fixture=new ProjectRecord{HasTestTime=true,TestTime=Stamp,DisplayTestTime="2026-09-29 16:31:47",ProjectName="手工名称 2026年09月29日"};
            using(var page=new ProjectManagementControl())for(int lang=0;lang<19;lang++){
                page.LanguageIndex=lang;
                Check((string)page.GetType().GetMethod("LocalizedTestTime",Private).Invoke(page,new object[]{fixture})=="2026-09-29 16:31:47","management language-neutral timestamp "+lang);
                using(var dialog=new MergeReportDialog(new[]{fixture},lang))
                    Check((string)dialog.GetType().GetMethod("LocalizedTestTime",Private).Invoke(dialog,new object[]{fixture})=="2026-09-29 16:31:47","merge dialog language-neutral timestamp "+lang);
                Check(fixture.ProjectName=="手工名称 2026年09月29日","manual name unchanged "+lang);
            }
            using(var page=new ProjectManagementControl{Size=new Size(720,520)})foreach(float points in new[]{9.5f,15f,20f}){
                var grid=(DataGridView)Field(page,"grid");grid.Size=new Size(680,320);
                using(var font=new Font("Microsoft YaHei UI",points)){
                    grid.Font=font;grid.DefaultCellStyle.Font=font;
                    page.GetType().GetMethod("FitGridColumns",Private).Invoke(page,null);
                    int required=TextRenderer.MeasureText("2026-09-29 23:59:59",font,Size.Empty,TextFormatFlags.NoPadding|TextFormatFlags.SingleLine).Width+24;
                    Check(grid.Columns["TestTime"].Width>=required&&grid.Columns["TestTime"].DefaultCellStyle.WrapMode==DataGridViewTriState.False,"single-line complete timestamp at "+points+" pt");
                    grid.DefaultCellStyle.Font=null;grid.Font=page.Font;
                }
            }
            Console.WriteLine("PROJECT_INVARIANT_TIME_PASS="+checks+";hardware=false;QA="+root);return 0;
        }catch(Exception error){Console.Error.WriteLine(error);return 1;}
        finally{Thread.CurrentThread.CurrentCulture=culture;Thread.CurrentThread.CurrentUICulture=ui;}
    }
}
