using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Reflection;
using System.Collections;
using System.Windows.Forms;
using Xiongxin.OWTS.Desktop;

internal static class ModuleCableSyncSmoke
{
    static int checks;
    static void Check(bool value,string message){checks++;if(!value)throw new Exception(message);}
    sealed class Module:UserControl
    {
        public string ProjectPath {get;set;}
        public bool IsBusy {get;set;}
        public bool Conflict, Throw;
        public int Refreshes;
        public int RefreshThread;
        public Action OnRefresh;
        public event EventHandler CableStructureChanged;
        public int Subscribers {get{return CableStructureChanged==null?0:CableStructureChanged.GetInvocationList().Length;}}
        public void Commit(){var h=CableStructureChanged;if(h!=null)h(this,EventArgs.Empty);}
        public bool RefreshCableStructure(){Refreshes++;RefreshThread=Thread.CurrentThread.ManagedThreadId;var callback=OnRefresh;OnRefresh=null;if(callback!=null)callback();if(Throw)throw new IOException("QA conflict");return !Conflict;}
    }
    [STAThread]static int Main(string[] args)
    {
        try {
            string qa=Path.GetFullPath(args[0]);Directory.CreateDirectory(qa);
            using(var source=new Module{ProjectPath=Path.Combine(qa,"project")})
            using(var browser=new Module{ProjectPath=source.ProjectPath})
            using(var analysis=new Module{ProjectPath=source.ProjectPath.ToUpperInvariant()+"\\"})
            using(var other=new Module{ProjectPath=Path.Combine(qa,"different")})
            using(var host=new ModuleHost(new object(),new ApplicationState(),new Diagnostics(qa),Path.Combine(qa,"modules"))) {
                source.CreateControl();browser.CreateControl();analysis.CreateControl();other.CreateControl();
                host.ConnectCableStructure(ModuleHost.ProjectCreation,source);
                host.ConnectCableStructure(ModuleHost.DataBrowser,browser);
                host.ConnectCableStructure(ModuleHost.DataAnalysis,analysis);
                host.ConnectCableStructure(ModuleHost.CalibrationTest,other);
                source.Commit();
                Check(browser.Refreshes==1&&analysis.Refreshes==1&&source.Refreshes==0&&other.Refreshes==0,"Same-project peers only; no self reload");
                host.ConnectCableStructure(ModuleHost.DataBrowser,browser);source.Commit();
                Check(browser.Refreshes==2,"Subscriptions are idempotent");
                analysis.IsBusy=true;source.Commit();
                Check(analysis.Refreshes==2&&browser.Refreshes==3,"Busy capture/analysis deferred; other pages update");
                analysis.IsBusy=false;
                var until=DateTime.UtcNow.AddSeconds(2);
                while(DateTime.UtcNow<until&&analysis.Refreshes==2){Application.DoEvents();Thread.Sleep(15);}
                Check(analysis.Refreshes==3,"Busy peer automatically refreshed once idle");
                browser.Conflict=true;source.Commit();int afterConflict=browser.Refreshes;
                until=DateTime.UtcNow.AddMilliseconds(650);while(DateTime.UtcNow<until){Application.DoEvents();Thread.Sleep(15);}
                Check(browser.Refreshes==afterConflict,"Unsaved-edit conflict is not continually overwritten/reloaded");
                browser.Conflict=false;host.RefreshPendingCableStructure(browser);
                Check(browser.Refreshes==afterConflict+1,"Conflict can retry on page activation");
                browser.Throw=true;source.Commit();browser.Throw=false;host.RefreshPendingCableStructure(browser);
                Check(browser.Refreshes==afterConflict+3,"Refresh failure preserves pending state without crashing commit");
                analysis.IsBusy=true;source.Commit();int beforeSwitch=analysis.Refreshes;
                analysis.ProjectPath=other.ProjectPath;analysis.IsBusy=false;host.RefreshPendingCableStructure(analysis);
                Check(analysis.Refreshes==beforeSwitch,"Pending project data never applied to another project");
                browser.Dispose();source.Commit();Check(other.Refreshes==0,"Disposed/different pages safely ignored");
                int beforeDispose=source.Refreshes;host.Dispose();source.Commit();Check(source.Refreshes==beforeDispose,"Host unsubscribes on disposal");
            }
            CheckThreadAndLifetime(qa);
            Console.WriteLine("MODULE_CABLE_SYNC_PASS="+checks+"; hardware not accessed");return 0;
        }catch(Exception ex){Console.Error.WriteLine(ex);return 1;}
    }
    static object Field(object value,string name){return value.GetType().GetField(name,BindingFlags.Instance|BindingFlags.NonPublic).GetValue(value);}
    static void PumpUntil(Func<bool> condition){var until=DateTime.UtcNow.AddSeconds(3);while(!condition()&&DateTime.UtcNow<until){Application.DoEvents();Thread.Sleep(10);}Check(condition(),"Queued cable sync completes");}
    static void CheckThreadAndLifetime(string qa)
    {
        string first=Path.Combine(qa,"first"),second=Path.Combine(qa,"second");int ui=Thread.CurrentThread.ManagedThreadId;
        using(var source=new Module{ProjectPath=first})
        using(var source2=new Module{ProjectPath=first})
        using(var target=new Module{ProjectPath=first})
        using(var other=new Module{ProjectPath=second})
        using(var host=new ModuleHost(new object(),new ApplicationState(),new Diagnostics(qa),Path.Combine(qa,"modules")))
        {
            host.ConnectCableStructure(ModuleHost.ProjectCreation,source);
            host.ConnectCableStructure(ModuleHost.CalibrationTest,source2);
            host.ConnectCableStructure(ModuleHost.DataBrowser,target);
            host.ConnectCableStructure(ModuleHost.DataAnalysis,other);
            Check(!source.IsHandleCreated&&!target.IsHandleCreated,"Module HWNDs need not exist for safe event dispatch");
            Task.Run(()=>source.Commit()).Wait();source.ProjectPath=second;
            PumpUntil(()=>target.Refreshes==1);
            Check(target.RefreshThread==ui&&other.Refreshes==0,"Worker commit uses UI thread and retains commit-time project across a page switch");
            source.ProjectPath=first;
            ((IDictionary)Field(host,"appliedProjectPaths"))[ModuleHost.DataBrowser]=first;
            target.ProjectPath="";source.Commit();Check(target.Refreshes==1,"Explicitly unloaded page never falls back to previous host project");
            target.ProjectPath=first;int prior=source.Refreshes;
            target.OnRefresh=()=>source2.Commit();source.Commit();
            Check(source.Refreshes==prior+1&&target.Refreshes==3,"Independent nested commit is queued, not discarded by refresh suppression");
            prior=target.Refreshes;target.OnRefresh=()=>target.Commit();source.Commit();
            Check(target.Refreshes==prior+1,"Refresh-triggered notification does not start a refresh loop");
            source2.IsBusy=true;source.Commit();source2.ProjectPath=second;source2.IsBusy=false;
            host.RefreshPendingCableStructure(source2);
            Check(!((System.Windows.Forms.Timer)Field(host,"cableRefreshTimer")).Enabled,"Project switch clears deferred timer when no busy targets remain");
            target.Dispose();
            Check(target.Subscribers==0&&!((IDictionary)Field(host,"cableControls")).Contains(target),"Disposed modules release event subscriptions and retained references");
            int count=other.Refreshes;Task.Run(()=>source.Commit()).Wait();host.Dispose();Application.DoEvents();
            Check(other.Refreshes==count&&source.Subscribers==0&&source2.Subscribers==0,"Host disposal unsubscribes and discards already queued callbacks");
        }
    }
}
