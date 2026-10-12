using System;
using System.IO;
using System.Reflection;
using System.Windows.Forms;
static class BrowserPostCommitStatusSmoke
{
 const BindingFlags F=BindingFlags.Instance|BindingFlags.Static|BindingFlags.NonPublic|BindingFlags.Public;
 [STAThread] static int Main(string[] args){try{
  string qa=Path.Combine(args[1],"postcommit-"+Guid.NewGuid().ToString("N"));
  var assembly=Assembly.LoadFrom(Path.Combine(args[0],"modules","DataBrowser","Xiongxin.OWTS.DataBrowser.dll"));
  var type=typeof(BrowserMeanSourceIntegritySmoke).GetNestedType("Fixture",F);
  using(var fixture=(IDisposable)Activator.CreateInstance(type,F,null,new object[]{assembly,qa,true},null)) {
   type.GetMethod("Mean",F).Invoke(fixture,null);
   object plan=type.GetMethod("Plan",F).Invoke(fixture,null);
   object browser=type.GetField("Browser",F).GetValue(fixture);
   Control information=(Control)browser.GetType().GetField("information",F).GetValue(browser);
   EventHandler failure=(s,e)=>{throw new InvalidOperationException("synthetic post-commit UI fault");};information.TextChanged+=failure;
   try{type.GetMethod("Commit",F).Invoke(fixture,new[]{plan});}finally{information.TextChanged-=failure;}
   if(!File.ReadAllText(Path.Combine(qa,"CABLE.in")).Contains("680")||Directory.GetFiles(qa,"CABLE.before-joint-*.bak").Length!=1)throw new Exception("commit/backup missing");
   string error=(string)browser.GetType().GetField("lastError",F).GetValue(browser);
   if(!error.Contains("synthetic post-commit UI fault"))throw new Exception("post-commit warning missing");
  }
  Console.WriteLine("BROWSER_POST_COMMIT_PASS; QA="+qa);return 0;
 }catch(Exception ex){Console.Error.WriteLine(ex);return 1;}}
}
