using System;
using System.Collections.Generic;
using System.Drawing;
using System.Reflection;
using System.Windows.Forms;
namespace HVX.Presentation
{
    // Only BCL objects cross this slot. Dialog creation never reads files.
    internal static class OemUiBranding
    {
        const string Key="HVX.OemUi.Snapshot.v1", Listeners="HVX.OemUi.Windows.v1";
        internal const string NeutralIconResource="HVX.NeutralWindow.ico";
        static readonly Lazy<Icon> NeutralIcon=new Lazy<Icon>(LoadNeutralIcon);
        static Icon LoadNeutralIcon()
        {
            var entry=Assembly.GetEntryAssembly();var own=typeof(OemUiBranding).Assembly;
            foreach(var assembly in new[]{entry,own}) {
                if(assembly==null)continue;
                using(var stream=assembly.GetManifestResourceStream(NeutralIconResource))
                    if(stream!=null)using(var icon=new Icon(stream))return (Icon)icon.Clone();
            }
            // Unpackaged test/embedding hosts may omit the resource. Production
            // host and every owned UI module embed the exact neutral ICO.
            return (Icon)SystemIcons.Application.Clone();
        }
        internal static Icon LoadIcon(){lock(AppDomain.CurrentDomain){var s=AppDomain.CurrentDomain.GetData(Key) as object[];if(s!=null&&s[0] is Icon)return (Icon)((Icon)s[0]).Clone();}return (Icon)NeutralIcon.Value.Clone();}
        internal static Image LoadLogo(){lock(AppDomain.CurrentDomain){var s=AppDomain.CurrentDomain.GetData(Key) as object[];return s!=null&&s[1] is Image?new Bitmap((Image)s[1]):null;}}
        internal static void Publish(Icon icon,Image logo)
        {
            Action[] actions;
            lock(AppDomain.CurrentDomain){var old=AppDomain.CurrentDomain.GetData(Key) as object[];AppDomain.CurrentDomain.SetData(Key,new object[]{icon==null?null:(Icon)icon.Clone(),logo==null?null:new Bitmap(logo)});if(old!=null)foreach(var item in old){var d=item as IDisposable;if(d!=null)d.Dispose();}var list=AppDomain.CurrentDomain.GetData(Listeners) as Dictionary<Form,Action>;actions=list==null?new Action[0]:new List<Action>(list.Values).ToArray();}
            var errors=new List<Exception>();
            foreach(var action in actions)try{action();}catch(Exception e){System.Diagnostics.Trace.TraceError("OEM window refresh failed: "+e);errors.Add(e);}
            if(errors.Count>0)throw new AggregateException("部分窗口的品牌显示未能更新。",errors);
        }
        internal static void Apply(Form form)
        {
            if(form==null)throw new ArgumentNullException("form");
            if(form.IsDisposed||form.Disposing)return;
            lock(AppDomain.CurrentDomain){var existing=AppDomain.CurrentDomain.GetData(Listeners) as Dictionary<Form,Action>;if(existing!=null&&existing.ContainsKey(form)){existing[form]();return;}}
            Icon owned=null;
            Action update=()=>{if(form.IsDisposed||form.Disposing)return;var next=LoadIcon();var previous=owned;form.Icon=next;owned=next;if(previous!=null)previous.Dispose();};
            Action refresh=()=>{if(form.IsDisposed||form.Disposing)return;try{if(form.InvokeRequired)form.BeginInvoke(update);else update();}catch(InvalidOperationException){if(!form.IsDisposed&&!form.Disposing&&form.IsHandleCreated)throw;System.Diagnostics.Trace.TraceWarning("OEM refresh skipped because the window is closing or has no handle.");}};
            lock(AppDomain.CurrentDomain){var list=AppDomain.CurrentDomain.GetData(Listeners) as Dictionary<Form,Action>;if(list==null){list=new Dictionary<Form,Action>();AppDomain.CurrentDomain.SetData(Listeners,list);}list.Add(form,refresh);}
            form.Disposed+=(s,e)=>{lock(AppDomain.CurrentDomain){var list=AppDomain.CurrentDomain.GetData(Listeners) as Dictionary<Form,Action>;if(list!=null)list.Remove(form);}if(owned!=null){owned.Dispose();owned=null;}};
            refresh();
        }
    }
}
