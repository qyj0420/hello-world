using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Xiongxin.OWTS.Localization;

// Exact program-owned captions only. No controls, product entry point or device owner.
internal static class CalibrationDisplayFeedbackLanguagesSmoke
{
    static int checks;
    static void Check(bool value,string reason){checks++;if(!value)throw new Exception(reason);}
    static int Main(string[] args)
    {
        try {
            string[] codes={"zh-CN","en","bg","hr","cs","fr","de","hu","it","pl","ro","ru","sr","sk","sl","es","tr","vi","zh-TW","pt-BR","ja","sv","nl","es-419","en-GB","de-CH","fr-CH","it-CH","rm-CH","be","kk","uz","az","hy","ky","tg","tk","uk","ka","pt-PT"};
            Check(UiLanguage.Codes.SequenceEqual(codes),"all 40 language codes and their persisted indexes retained");
            string[] english={"Display adjusted automatically. Please review the waveform.","Please review the waveform.","Please review the waveform and adjust the display filter manually if needed."};
            string[] chinese={"已自动调整显示，请核对波形。","请核对波形。","请核对波形，必要时手动调整显示滤波。"};
            string[] added={"rm-CH","vi","ja","kk","uz","az","hy","ky","tg","tk","ka"};
            var rows=(string[])typeof(InternationalText).GetField("CalibrationShortCableRows",BindingFlags.NonPublic|BindingFlags.Static).GetValue(null);
            string[] columns=rows[0].Split('|');
            Check(columns.Distinct(StringComparer.Ordinal).Count()==columns.Length,"resource language columns are unique");
            foreach(string row in rows)Check(row.Split('|').Length==columns.Length,"every resource row has the same number of columns");
            foreach(string code in codes)
            for(int k=0;k<english.Length;k++) {
                string translated;int language=UiLanguage.Index(code);
                Check(UiLanguage.TryTranslate(language,english[k],out translated),"caption is present rather than a missing-catalog fallback: "+code);
                Check(!String.IsNullOrWhiteSpace(translated),"caption is nonempty: "+code);
                Check(UiLanguage.Text(language,chinese[k],english[k])==translated,"actual caption selection uses the same catalog: "+code);
                if(code!="en"&&code!="en-GB")Check(translated!=english[k],"caption has actual local text: "+code);
                Check(translated.IndexOf("{",StringComparison.Ordinal)<0&&!translated.Contains("Exception")&&!translated.Contains("PICO")&&!translated.Contains("AO/DO"),"caption is short customer feedback, not internal details: "+code);
            }
            foreach(string code in added) {
                int at=Array.IndexOf(columns,code);Check(at>0,"new language is explicitly represented in source resource: "+code);
                for(int k=0;k<english.Length;k++) {
                    string text=rows.Single(r=>r.StartsWith(english[k]+"|",StringComparison.Ordinal)).Split('|')[at];
                    Check(text!=english[k]&&!String.IsNullOrWhiteSpace(text),"new translation is not copied English: "+code);
                    Check(UiLanguage.Translate(UiLanguage.Index(code),english[k])==text,"new translation reaches runtime unchanged: "+code);
                }
                // Only the three new display feedback captions are in this task.
                // Preserve and declare the other four messages' prior English fallback.
                foreach(string row in rows.Skip(4))Check(row.Split('|')[at]==row.Split('|')[1],"other short-cable captions retain their prior English fallback: "+code);
            }
            string[] ordinary={"Calibration acquisition","Calibration quality warning","The device connection is not ready. Return to the home page, check the connection and retry. Current data is retained.","Load a valid project first.","Select a valid calibration level.","The calibration quality needs review and the data may be unreliable. Save anyway?","This joint is saved. Delete it on the project editing page."};
            foreach(string code in new[]{"en","zh-CN","de","fr","es","it","pt-BR","sv","nl"})
            foreach(string key in ordinary) {
                string text;Check(UiLanguage.TryTranslate(UiLanguage.Index(code),key,out text)&&!String.IsNullOrWhiteSpace(text),"short customer guard has a catalog translation: "+code);
                if(code!="en")Check(text!=key,"short customer guard uses actual local text: "+code);
            }
            string archive=File.ReadAllText(Path.Combine(Environment.CurrentDirectory,"modules-src/CalibrationTest/src/CalibrationProgramControl.LiveArchive.cs"));
            Check(archive.Contains("MessageBox.Show(FindForm(),CalibrationQualitySaveCustomerCaption"),"customer quality modal uses short risk caption");
            Check(archive.Contains("ConfirmQualityWarningSaveForTests(warning)")&&archive.Contains("AuditQualityOperation(phaseIndex,level,\"CalibrationQualityWarning\",accepted?\"Accepted\":\"Cancelled\",warning)"),"full warning evidence and explicit yes/no decision retained");
            Check(archive.Contains("MessageBoxDefaultButton.Button2")&&archive.Contains("if(!accepted)return false;"),"quality confirmation defaults to no and cancellation prevents saving");
            Check(archive.Contains("lastCalibrationError=count>0||failures.Count==0?String.Empty"),"an applicable new calibration suppresses unrelated old-file rejection feedback");
            Check(archive.Contains("WriteCalibrationDiagnostic(\"Calibration records not loaded"),"old-file rejection details remain diagnostic evidence");
            Console.WriteLine("CALIBRATION_DISPLAY_FEEDBACK_LANGUAGES_PASS="+checks+"; CAPTIONS=3; LANGUAGES_PRESERVED=40; NEW_TRANSLATIONS=33; OTHER_CAPTIONS=NOT_FULLY_TRANSLATED; SDK_CALLS=0; PRODUCT_STARTED=0; PROFESSIONAL_LINGUISTIC_REVIEW=NOT_PERFORMED");return 0;
        }catch(Exception error){Console.Error.WriteLine(error);return 1;}
    }
}
