using System;
using System.IO;
using System.Linq;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using System.Web.Script.Serialization;
using Xiongxin.OWTS.Localization;
internal static class InternationalCatalogAudit
{
    const BindingFlags All=BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance;
    static int checks;
    static void Check(bool ok,string message){checks++;if(!ok)throw new Exception(message);}
    static int Main(string[] args)
    {
        try
        {
            string root=Path.GetFullPath(args[0]),output=Path.GetFullPath(args[1]);Directory.CreateDirectory(output);
            var catalog=(Dictionary<string,Dictionary<string,string>>)typeof(InternationalText).GetField("Packs",All).GetValue(null);
            var keys=new SortedSet<string>(catalog["en"].Keys,StringComparer.Ordinal);
            var assemblies=new Dictionary<string,Assembly>();
            foreach(string entry in new[]{"ProjectCreation|Xiongxin.OWTS.ProjectCreation","ProjectManagement|Xiongxin.OWTS.ProjectManagement","DataBrowser|Xiongxin.OWTS.DataBrowser","DataAnalysis|HVX.PdReview.V1.0","ReportGenerator|Xiongxin.OWTS.ReportGenerator"})
            {var p=entry.Split('|');assemblies[p[0]]=Assembly.LoadFrom(Path.Combine(root,"modules",p[0],p[1]+".dll"));}
            foreach(string entry in new[]{"ProjectCreation|HVX.Controls.ProjectCreationLanguageContract|Texts|array", "ProjectManagement|HVX.Controls.ProjectManagementLanguages|Texts|key", "DataBrowser|HVX.Controls.BrowserLanguages|Words|key", "DataAnalysis|HVX.PdReview.ReviewLanguages|Extra|key", "DataAnalysis|HVX.PdReview.ReviewWorkspaceText|Words|key", "ReportGenerator|HVX.ReportGenerator.ReportLanguages|English|string"})
            {
                var p=entry.Split('|');var dictionary=(IDictionary)assemblies[p[0]].GetType(p[1]).GetField(p[2],All).GetValue(null);
                foreach(DictionaryEntry item in dictionary) keys.Add(p[3]=="array"?((string[])item.Value)[1]:p[3]=="string"?(string)item.Value:(string)item.Key);
            }
            var report=assemblies["ReportGenerator"].GetType("HVX.ReportGenerator.ReportLanguages");
            var reportKeys=(string[])report.GetMethod("GetUiKeys",All).Invoke(null,null);
            var getText=report.GetMethod("GetText",All);
            var progress=assemblies["DataAnalysis"].GetType("HVX.PdReview.ReviewAnalysisProgressText");
            var progressKeys=(string[])progress.GetField("Keys",All).GetValue(null);
            var progressText=progress.GetMethod("Text",All);
            var conclusionType=assemblies["ReportGenerator"].GetType("HVX.ReportGenerator.ConclusionService");
            object conclusion=Activator.CreateInstance(conclusionType);
            var connector=Assembly.LoadFrom(Path.Combine(root,"modules","HardwareIntegration","HardwareIntegration.exe")).GetType("Xiongxin.HvDacXpert.L10n");
            var connectorKeys=(string[])connector.GetField("Keys",All).GetValue(null);
            var entries=new List<object>();
            for(int language=0;language<40;language++)
            {
                string code=UiLanguage.Codes[language];
                foreach(string key in reportKeys) Check(!String.IsNullOrEmpty((string)getText.Invoke(null,new object[]{language,key})),"Report text "+code+" "+key);
                foreach(string key in progressKeys) Check(!String.IsNullOrEmpty((string)progressText.Invoke(null,new object[]{language,key})),"Analysis progress "+code+" "+key);
                var recommendation=(string)conclusionType.GetMethod("GetIeeeRecommendation").Invoke(conclusion,new object[]{language,"Synthetic test conclusion"});
                Check(UiLanguage.IsChinese(language)||!String.IsNullOrEmpty(recommendation),"Report recommendation fallback "+code);
                connector.GetMethod("SetChoice").Invoke(null,new object[]{code,false});
                foreach(string key in connectorKeys) Check(!String.IsNullOrEmpty((string)connector.GetMethod("T").Invoke(null,new object[]{key,new object[]{1,2,3,4}})),"Connector text "+code+" "+key);
                Check(!String.IsNullOrEmpty((string)connector.GetProperty("StopWaitText").GetValue(null,null)),"Connector stop label "+code);
                var missing=new List<string>();int translated=0;
                foreach(string key in keys){string value;if(UiLanguage.TryTranslate(language,key,out value))translated++;else missing.Add(key);}
                entries.Add(new {Code=code,Name=UiLanguage.Names[language],Index=language,SharedCatalogMatches=translated,AuditedKeys=keys.Count,LegacyFallbackIndex=UiLanguage.LegacyIndex(language),MissingFromSharedCatalog=missing,FullTranslationCertified=false});
            }
            var data=new{Schema="XIONGXIN.LanguageCoverage.v1",Date="2026-10-02",Scope="Shared catalog audit only; legacy module dictionaries provide additional translations. MissingFromSharedCatalog is not the count of untranslated screens. English/source text is retained when no resource is available.",NativeReview="Pending",Assertions=checks,Locales=entries};
            File.WriteAllText(Path.Combine(output,"language-coverage.json"),new JavaScriptSerializer{MaxJsonLength=10000000}.Serialize(data),new UTF8Encoding(true));
            File.WriteAllText(Path.Combine(output,"catalog-audit.txt"),"PASS\r\nAssertions="+checks+"\r\nReportKeys="+reportKeys.Length+"\r\nConnectorKeys="+connectorKeys.Length+"\r\nAuditedSharedKeys="+keys.Count+"\r\nHardwareCommands=0\r\n",new UTF8Encoding(true));
            Console.WriteLine("INTERNATIONAL_CATALOG_PASS="+checks+"; AUDITED_SHARED_KEYS="+keys.Count);return 0;
        }
        catch(Exception e){Console.Error.WriteLine(e);return 1;}
    }
}
