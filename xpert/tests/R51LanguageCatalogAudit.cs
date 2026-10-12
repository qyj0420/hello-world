using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Web.Script.Serialization;
using Xiongxin.OWTS.Desktop;
using Xiongxin.OWTS.Localization;

internal static class R51LanguageCatalogAudit
{
    static int Main(string[] args)
    {
        try {
            if(args.Length!=1||!Path.IsPathRooted(args[0]))throw new ArgumentException("Isolated output path required.");
            if(AppLanguages.SelectionItems.Length!=UiLanguage.Codes.Length)throw new Exception("Existing locales removed.");
            var packs=(Dictionary<string,Dictionary<string,string>>)typeof(InternationalText).GetField("Packs",BindingFlags.NonPublic|BindingFlags.Static).GetValue(null);
            string[] keys=packs.Values.SelectMany(p=>p.Keys).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(k=>k).ToArray();
            var rows=new List<object>();
            foreach(string code in UiLanguage.Codes) {
                if(AppLanguages.ResolveCode(code)!=code||AppLanguages.SelectionIndex(code)!=Array.IndexOf(UiLanguage.Codes,code))throw new Exception("Changed persisted index: "+code);
                var missing=new List<string>();
                foreach(string key in keys){string text;if(code!="zh-TW"&&!InternationalText.TryGet(code,key,out text))missing.Add(key);}
                rows.Add(new{Code=code,Selectable=true,CatalogKeys=keys.Length,MissingCatalogKeys=missing.ToArray(),TraditionalConversion=code=="zh-TW"});
            }
            var report=new{Schema="HVX.LanguageCatalogAudit/1",HardwareAccessed=false,ProductStarted=false,LocaleCount=rows.Count,FullTranslationVerified=false,
                Scope="Existing catalog key coverage only. Dynamic captions, tooltips, dialogs, reports and visual layout require additional audit. Missing translations retain the locale and fall back; no resources removed.",Languages=rows};
            File.WriteAllText(args[0],new JavaScriptSerializer{MaxJsonLength=4*1024*1024,RecursionLimit=32}.Serialize(report),new System.Text.UTF8Encoding(false));
            Console.WriteLine("LANGUAGE_CATALOG_AUDIT_PASS=40;FullTranslationVerified=False;Report="+args[0]);return 0;
        }catch(Exception error){Console.Error.WriteLine(error);return 1;}
    }
}
