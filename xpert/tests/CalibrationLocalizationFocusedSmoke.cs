using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using HVX.Controls;
using Xiongxin.Calibration;
using Xiongxin.OWTS.Localization;

// Render only managed controls and synthetic state. No product Main, I/O owner or SDK.
internal static class CalibrationLocalizationFocusedSmoke
{
    const BindingFlags B=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static;
    static int checks;
    static readonly Regex Han=new Regex("[\\u4e00-\\u9fff]");
    static void Check(bool pass,string why){checks++;if(!pass)throw new Exception(why);}
    static object Call(object o,string n,params object[] a){return o.GetType().GetMethods(B).Single(m=>m.Name==n&&m.GetParameters().Length==a.Length).Invoke(o,a);}
    static void Set(object o,string n,object v){o.GetType().GetField(n,B).SetValue(o,v);}
    static string T(int i,string cn,string en){return UiLanguage.Text(i,cn,en);}
    static bool Fits(Graphics g,string text,Font font,RectangleF rect)
    {
        using(var format=(StringFormat)typeof(CalibrationProgramControl).GetMethod("ThemeCaptionFormat",B).Invoke(null,new object[]{g,text,font,rect,false}))
        {
            int fitted,lines;g.MeasureString(text,font,rect.Size,format,out fitted,out lines);return fitted==text.Length;
        }
    }
    [STAThread]static int Main(string[] args)
    {
        try
        {
            string qa=args[0];Directory.CreateDirectory(qa);
            string[] expected={"zh-CN","en","bg","hr","cs","fr","de","hu","it","pl","ro","ru","sr","sk","sl","es","tr","vi","zh-TW","pt-BR","ja","sv","nl","es-419","en-GB","de-CH","fr-CH","it-CH","rm-CH","be","kk","uz","az","hy","ky","tg","tk","uk","ka","pt-PT"};
            Check(UiLanguage.Codes.SequenceEqual(expected),"all 40 existing language codes and legacy indexes preserved");
            string[] keys=((string[])typeof(InternationalText).GetField("CalibrationRows",B).GetValue(null))[0].Split('|').Skip(1).ToArray();
            string[] safety=((string[])typeof(InternationalText).GetField("CalibrationSafetyRows",B).GetValue(null)).Skip(1).Select(r=>r.Split('|')[0]).ToArray();
            string[] flow=((string[])typeof(InternationalText).GetField("CalibrationFlowRows",B).GetValue(null)).Skip(1).Select(r=>r.Split('|')[0]).ToArray();
            var audit=new List<object>();
            foreach(string code in UiLanguage.Codes)
            {
                int index=UiLanguage.Index(code),found=0,safetyFound=0,flowFound=0;string translated;
                foreach(string key in keys)if(UiLanguage.TryTranslate(index,key,out translated)){found++;Check(!String.IsNullOrWhiteSpace(translated),"resource nonempty: "+code+" / "+key);}
                foreach(string key in safety)if(UiLanguage.TryTranslate(index,key,out translated))safetyFound++;
                foreach(string key in flow)if(UiLanguage.TryTranslate(index,key,out translated))flowFound++;
                audit.Add(new{Code=code,FocusedKeyCount=keys.Length,FocusedCatalogPresent=found,FocusedEnglishFallback=code=="zh-TW"?0:keys.Length-found,
                    SafetyKeyCount=safety.Length,SafetyCatalogPresent=safetyFound,SafetyEnglishFallback=code=="zh-TW"?0:safety.Length-safetyFound,
                    FlowKeyCount=flow.Length,FlowCatalogPresent=flowFound,FlowEnglishFallback=code=="zh-TW"?0:flow.Length-flowFound,
                    TraditionalWindowsConversion=code=="zh-TW",ProfessionalReviewCompleted=false});
                if(new[]{"en","bg","hr","cs","fr","de","hu","it","pl","ro","ru","sr","sk","sl","es","tr","pt-BR","ja","sv","nl","es-419","en-GB","de-CH","fr-CH","it-CH","pt-PT"}.Contains(code))
                    Check(found==keys.Length,"focused resources cover preserved target language: "+code);
            }
            File.WriteAllText(Path.Combine(qa,"language-focused-audit.json"),new JavaScriptSerializer().Serialize(new{Schema="HVX.Calibration.LocalizationFocused/1",Scope=keys.Length+" focused captions, "+safety.Length+" feedback messages, "+flow.Length+" flow captions only; not all pages. zh-TW uses Chinese-script conversion, not an independent pack.",Languages=audit}));
            foreach(string code in new[]{"en","zh-TW","de","fr","es","it","pt-BR","sv","nl"})
            using(var page=new CalibrationProgramControl())
            using(var bitmap=new Bitmap(1280,720))
            using(var graphics=Graphics.FromImage(bitmap))
            {
                page.EmbeddedMode=true;page.LanguageIndex=UiLanguage.Index(code);page.Size=bitmap.Size;page.SelectedLevelCode="5nC";
                var captions=new List<string>();CalibrationProgramControl.CaptionTranslationForTests=captions.Add;
                try
                {
                    foreach(bool quality in new[]{false,true})
                    {
                        Set(page,"compactQualityTab",quality);captions.Clear();Call(page,"RenderFrame",graphics,new Rectangle(Point.Empty,bitmap.Size));
                        if(code!="zh-TW")Check(!captions.Any(s=>Han.IsMatch(s)),"actual compact "+(quality?"noise":"waveform")+" frame contains no Chinese labels in "+code+": "+String.Join(";",captions.Where(s=>Han.IsMatch(s))));
                        else Check(captions.Contains("校準參數")&&captions.Contains("校準檔位"),"traditional frame uses translated calibration labels");
                        bitmap.Save(Path.Combine(qa,"calibration-"+code+"-"+(quality?"noise":"waveform")+".png"));
                    }
                }finally{CalibrationProgramControl.CaptionTranslationForTests=null;}
                // Exercise the separate large-layout phase footer; the host uses
                // the compact layout, so the compact-frame check cannot cover it.
                var drawingLanguage=typeof(CalibrationProgramControl).GetField("drawingLanguage",B);
                int previousLanguage=(int)drawingLanguage.GetValue(null);
                try
                {
                    drawingLanguage.SetValue(null,page.LanguageIndex);captions.Clear();
                    CalibrationProgramControl.CaptionTranslationForTests=captions.Add;
                    typeof(CalibrationProgramControl).GetMethod("DrawThemePhaseSelection",B).Invoke(null,new object[]{graphics,-1});
                    string footer=UiLanguage.BuiltIn(page.LanguageIndex,"参数按相别独立保存");
                    Check(captions.Contains(footer),"large-layout phase footer uses the same translated key: "+code);
                    if(code!="zh-TW")Check(!captions.Any(s=>Han.IsMatch(s)),"large-layout phase selector has no Chinese captions in "+code);
                    else Check(footer=="參數按相別獨立保存","large-layout traditional footer is converted");
                }
                finally
                {
                    drawingLanguage.SetValue(null,previousLanguage);
                    CalibrationProgramControl.CaptionTranslationForTests=null;
                }
                var notices=new List<string>();page.ShowCalibrationEntryNoticeForTests=notices.Add;
                page.ReadCalibrationEntryEmergencyForTests=()=>Task.FromResult<bool?>(true);page.NotifyCalibrationPageEntered();
                page.ReadCalibrationEntryEmergencyForTests=()=>Task.FromResult<bool?>(null);page.NotifyCalibrationPageEntered();
                Check(notices.Count==2&&page.CalibrationEntryEmergencySignalHigh==null,"localized state feedback preserves actual HIGH then unknown semantics: "+code);
                if(code!="zh-TW")Check(!notices.Any(s=>Han.IsMatch(s)),"localized reminders do not leak Chinese: "+code);
                else Check(notices[0].Contains("解除")&&notices[1].Contains("未確認"),"traditional reminder retains observed/unknown distinction");
                using(var font=new Font("Microsoft YaHei UI",10))
                    foreach(string notice in notices)Check(Fits(graphics,notice,font,new RectangleF(0,0,444,88)),"full safety reminder fits body without losing final instruction: "+code);
                int cancellations=0;
                using(var self=new CalibrationSelfCheckWindow(null,()=>cancellations++,text=>UiLanguage.BuiltIn(page.LanguageIndex,text)))
                {
                    self.UpdateStep("内部参数适配中…");var body=self.Controls.OfType<Label>().Single();var cancel=self.Controls.OfType<Button>().Single();
                    Check(self.Text==T(page.LanguageIndex,"采集自校验","Acquisition self-check")&&body.Text==T(page.LanguageIndex,"内部参数适配中…","Checking input settings…"),"self-check title and current progress use host language: "+code);
                    Check(cancel.Text==T(page.LanguageIndex,"取消采集","Cancel acquisition"),"self-check cancel button uses host language: "+code);
                    Call(cancel,"OnClick",EventArgs.Empty);Check(cancellations==1&&!cancel.Enabled&&body.Text==T(page.LanguageIndex,"正在停止自校验…","Stopping self-check…"),"translated cancel retains one real cancellation callback and stopping feedback: "+code);
                    self.Complete();
                }
                using(var font=new Font("Microsoft YaHei UI",8.5f))
                {
                    Check(Fits(graphics,UiLanguage.Translate(page.LanguageIndex,"Select noise"),font,new RectangleF(0,0,84,34)),"noise selection caption fully fits its two-line button: "+code);
                    Check(Fits(graphics,UiLanguage.Translate(page.LanguageIndex,"Auto threshold"),font,new RectangleF(0,0,84,34)),"automatic threshold caption fully fits its two-line button: "+code);
                    Check(Fits(graphics,UiLanguage.Translate(page.LanguageIndex,"Manual controls"),font,new RectangleF(0,0,140,30)),"manual control caption fully fits beside toggle: "+code);
                    Check(Fits(graphics,UiLanguage.Translate(page.LanguageIndex,"Parameters saved per phase"),font,new RectangleF(0,0,194,40)),"phase persistence footer fits: "+code);
                }
            }
            Check(UiLanguage.Text(1,"未施加低通","Low-pass: off")=="Low-pass: off","off filter status is not rephrased as an applied cutoff");
            Check(UiLanguage.BuiltIn(1,"实际截止 {0} MHz").Contains("{0}")&&String.Format(UiLanguage.BuiltIn(1,"实际截止 {0} MHz"),"7.0")=="Cutoff: 7.0 MHz","cutoff value and MHz unit preserved");
            Check(UiLanguage.BuiltIn(1,"C:\\用户项目\\原始LOC.loc")=="C:\\用户项目\\原始LOC.loc","project paths remain outside substring translation");
            string formSource=File.ReadAllText(Path.Combine(Environment.CurrentDirectory,"modules-src/CalibrationStandalone/CalibrationForm.cs"));
            const string unknown="急停状态未确认，请检查设备连接后重试。当前未取得有效急停状态。";
            Check(Regex.Matches(formSource,Regex.Escape("Func<string,string> translate=HostTranslateUi??(text=>text);")).Count==2,"both failure notices use the host translator with a standalone fallback");
            Check(Regex.Matches(formSource,Regex.Escape("translate(\""+unknown+"\")")).Count==2,"both failure bodies reuse the existing translated unknown-state notice");
            Check(Regex.Matches(formSource,Regex.Escape("translate(\"急停状态提醒\")")).Count==2,"both failure titles use the host translator");
            Check(Regex.IsMatch(formSource,@"if\s*\(!box\.EmergencySignalHigh\.HasValue\)\s*\{[^}]+MessageBoxIcon\.Warning\);\s*return;",RegexOptions.Singleline),"unknown emergency input still returns before acquisition");
            Check(formSource.Contains("if (capturing) HostCancel();")&&formSource.Contains("readFailureNotified = true;")&&formSource.Contains("System.Diagnostics.Debug.WriteLine(\"Emergency status read: \" + ex);"),"read failure retains capture cancellation, notification latch and full diagnostic");
            string englishUnknown=UiLanguage.BuiltIn(1,unknown);
            Check(!Han.IsMatch(englishUnknown)&&englishUnknown.Contains("not confirmed")&&englishUnknown.Contains("retry"),"unknown failure has an English next action, not a fabricated emergency state");
            Console.WriteLine("CALIBRATION_LOCALIZATION_FOCUSED_PASS="+checks+"; LANGUAGES_PRESERVED=40; SDK_CALLS=0; PRODUCT_STARTED=0; SPECIALIST_REVIEW=NOT_PERFORMED");return 0;
        }
        catch(Exception error){Console.Error.WriteLine(error);return 1;}
        finally{CalibrationProgramControl.CaptionTranslationForTests=null;}
    }
}
