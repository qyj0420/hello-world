using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows.Forms;

namespace Xiongxin.OWTS.Desktop
{
    // Unified maintenance defaults, not session persistence or an algorithm.
    // A loaded/analyzed session owns its saved filtering parameters. Applying
    // language/context repeatedly must not replace its manual or saved choice.
    internal static class LocationFilterSettingsDefaults
    {
        sealed class Applied {internal string Project,Algorithm;internal bool Initialized;}
        static readonly ConditionalWeakTable<Control,Applied> applied=new ConditionalWeakTable<Control,Applied>();
        internal static void Apply(Control control,string property,string algorithm,string project)
        {
            if(control==null)throw new ArgumentNullException("control");
            if(algorithm=="A")algorithm="B"; // Retired maintenance preference.
            if(algorithm!="B")throw new ArgumentException("定位过滤算法无效，请检查设置。","algorithm");
            var stamp=applied.GetValue(control,c=>new Applied());string nextProject=project??String.Empty;
            bool newProject=stamp.Initialized&&!String.Equals(stamp.Project,nextProject,StringComparison.OrdinalIgnoreCase);
            if(LateBound.ReadBoolean(control,false,"IsAnalysisRunning")||LateBound.ReadBoolean(control,false,"IsBusy")||
                LateBound.ReadBoolean(control,false,"HasActiveHardwareSession")||LateBound.ReadBoolean(control,false,"ActionPending"))return;
            if(!newProject&&(LateBound.ReadString(control,"LoadedAnalysisId").Length>0||
                Convert.ToInt32(LateBound.Read(control,"SourceRowCount")??0)>0||LateBound.ReadBoolean(control,false,"DataSaved"))){
                // The first host attachment can already contain a loaded session.
                // Remember its project without applying a default, so a later
                // explicit project switch is still recognized as a new session.
                if(!stamp.Initialized){stamp.Project=nextProject;stamp.Algorithm=algorithm;stamp.Initialized=true;}
                return;
            }
            if(!newProject&&stamp.Initialized&&stamp.Algorithm==algorithm)return;
            var target=control.GetType().GetProperty(property,BindingFlags.Public|BindingFlags.Instance);
            if(target==null||!target.CanRead||!target.CanWrite||target.PropertyType!=typeof(string))
                throw new InvalidOperationException("当前组件不支持定位过滤设置，请检查组件版本。");
            if(!String.Equals((string)target.GetValue(control,null),algorithm,StringComparison.Ordinal))target.SetValue(control,algorithm,null);
            stamp.Project=nextProject;stamp.Algorithm=algorithm;stamp.Initialized=true;
        }
    }
}
