using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using System.Drawing;

namespace Xiongxin.HvDacXpert
{
    static partial class L10n
    {
        static string choice="auto";
        public static string Choice {get{return choice;}}
        public static string Code {get{return choice=="auto"?Resolve(CultureInfo.CurrentUICulture.Name):choice;}}
        public static event Action Changed;
        public static string PersistentModeText {get{return Code=="zh-CN"?"关闭程序后仍自动连接（取消勾选：断线后需打开程序连接）":"Auto-connect with application closed (off: reconnect using application)";}}
        public static string StopWaitText {get{int index=Xiongxin.OWTS.Localization.UiLanguage.Index(Code);return index==18?Xiongxin.OWTS.Localization.UiLanguage.ToTraditional(StopLabels[0]):StopLabels[Xiongxin.OWTS.Localization.UiLanguage.LegacyIndex(index)];}}
        static readonly string[] StopLabels={"结束等待 / 关闭","Stop waiting / Close","Спри изчакването / Затвори","Prekini čekanje / Zatvori","Zastavit čekání / Zavřít","Arrêter l’attente / Fermer","Warten beenden / Schließen","Várakozás leállítása / Bezárás","Interrompi attesa / Chiudi","Zakończ oczekiwanie / Zamknij","Oprește așteptarea / Închide","Прекратить ожидание / Закрыть","Прекини чекање / Затвори","Ukončiť čakanie / Zavrieť","Ustavi čakanje / Zapri","Detener espera / Cerrar","Beklemeyi durdur / Kapat","Dừng chờ / Đóng"};
        static string Preference {get{return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"XIONGXIN","HvDacXpert","language.txt");}}
        public static string Resolve(string culture)
        {
            return Xiongxin.OWTS.Localization.UiLanguage.ResolveCode(culture);
        }
        public static void Initialize(string[] args)
        {
            string requested="auto";
            try {if(File.Exists(Preference))requested=File.ReadAllText(Preference).Trim();}catch(IOException){}catch(UnauthorizedAccessException){}
            foreach(string arg in args)if(arg.StartsWith("--lang="))requested=arg.Substring(7);
            if(requested!="auto" && !Codes.Contains(requested))requested="auto";
            SetChoice(requested,false);
        }
        public static void SetChoice(string value,bool save)
        {
            if(value!="auto" && !Codes.Contains(value))throw new ArgumentException("Unsupported language");
            if(save){Directory.CreateDirectory(Path.GetDirectoryName(Preference));File.WriteAllText(Preference,value);}
            choice=value;if(Changed!=null)Changed();
        }
        public static string DeviceName(string model)
        {
            return model.EndsWith("art")?"HV DAC Xpert Controller":model.EndsWith("scope")?"HV DAC Xpert Acquisition":"HV DAC Xpert System";
        }
        // Customer UI is allowlisted; raw diagnostics continue to the support log only.
        public static string CustomerMessage(string raw)
        {
            if(string.IsNullOrWhiteSpace(raw))return null;
            if(raw==StopWaitText)return StopWaitText;
            if(raw.Contains("未完成")||raw.Contains("失败")||raw.Contains("incomplete")||raw.Contains("failed")||Regex.IsMatch(raw,@"\bE\d{3}\b"))return T("error")+" "+T("retry");
            if(raw.Contains("超时")||raw.StartsWith("校验等待结束"))return T("timeout");
            if(raw.StartsWith("系统绑定完成"))return T("complete");
            if(raw.StartsWith("系统绑定尚未"))return T("incomplete");
            if(raw.StartsWith("发现 "))return Text(raw);
            if(raw.StartsWith("未发现"))return T("notfound");
            if(raw.StartsWith("正在保存绑定")||raw.StartsWith("正在校验"))return T("processing");
            if(raw.StartsWith("步骤 1/3"))return T("processing");
            if(raw.StartsWith("步骤 2/3")||raw.StartsWith("步骤 3/3"))return T("wait");
            if(raw.StartsWith("自动绑定设置"))return T("settingsSaved");
            if(raw.Contains("被其他电脑占用"))return T("occupied");
            if(raw.Contains("已取消自动绑定")||raw.StartsWith("已保存取消勾选"))return T("settingsSaved");
            if(raw.StartsWith("演示数据"))return T("demo");
            return null;
        }
        static bool Han(string value){return Regex.IsMatch(value,@"[\u4e00-\u9fff]");}
        static readonly Dictionary<string,string> Aliases=new Dictionary<string,string> {
            {"刷新设备","refresh"},{"自动连接 / Apply","connect"},{"重新连接","reconnect"},{"保存取消勾选 / Apply","save"},
            {"同时取消本次未发现的旧汇集器 / 采集模块自动绑定","cleanup"},{"准备检测信号汇集器","searching"},
            {"正在实时搜索信号汇集器…","searching"},{"未发现在线信号汇集器，请检查电源、网线及网段后刷新。","notfound"},
            {"选择已变更，请点击自动连接保存并重新校验。","selectionChanged"},
            {"发现多个汇集器，请选择要绑定的模块并点击自动连接。","selectHub"},{"等待硬件接入；每 10 秒重新检测，也可手动刷新。","waitHardware"},
            {"系统绑定完成：Xpert 已绑定，art / scope 驱动加载校验通过。","complete"},{"系统绑定尚未完成，请查看各项状态。","incomplete"},
            {"操作进行中，请等待结束后关闭窗口。","processing"},{"操作记录（连接成功后，请等待设备自身驱动初始化完成）","log"},
            {"Xpert 绑定 → art 驱动校验 → scope 驱动校验","steps"},{"关闭进度窗口","close"},
            {"正在校验在线状态、保存勾选并连接…","processing"},{"正在保存绑定设置并建立通信连接，请勿关闭窗口…","processing"},
            {"界面演示 · 设备名称为示例，未执行连接","demo"},{"演示数据仅用于界面检查；正常启动时显示实时发现结果。","demo"},
            {"状态校验暂时失败，请刷新重试。","error"},{"操作未完成，请查看下方原因。","error"},{"检测失败","error"},
            {"未选择模块，未执行驱动加载。","pending"},{"已保存取消勾选，未执行系统绑定校验。","settingsSaved"},
            {"自动绑定设置已保存，原设置已自动备份。","settingsSaved"},{"自动绑定设置已与当前勾选一致。","settingsSaved"},
            {"重新检查 / 安装","retry"},{"开始安装","install"},{"正在校验安装包…","verify"},{"正在释放组件…","extract"},
            {"通信服务、配套文件和 x64 驱动包已存在，保留当前版本。","already"},
            {"未连接的采集模块将在首次连接并刷新时同步显示名称。","names"},
            {"组件安装完成。请通过桌面 HV-DAC Xpert 600 打开程序并连接设备。","installed"},
            {"正在打开连接程序：检测硬件 → 自动绑定 → Windows 驱动加载校验…","openApp"},
            {"另一个安装程序正在运行。","installing"},{"正在安装，请等待当前步骤完成。","installing"}
        };
        public static string Text(string raw)
        {
            string value=TranslateText(raw);
            if(string.IsNullOrEmpty(value))return value;
            value=value.Replace("HV-DAC Xpert 600","HV-DAC Xpert System");
            value=Regex.Replace(value,@"\bart\b","Controller",RegexOptions.IgnoreCase);
            value=Regex.Replace(value,@"\bscope\b","Acquisition",RegexOptions.IgnoreCase);
            return value.Replace("信号汇集器","总控系统").Replace("汇集器","系统").Replace("HV-DAC","HV DAC");
        }
        static string TranslateText(string raw)
        {
            if(string.IsNullOrEmpty(raw)||Code=="zh-CN"||!Han(raw))return raw;
            if(raw=="Xpert 绑定 → art 驱动校验 → scope 驱动校验")return "Xpert "+T("binding")+" → art / scope: "+T("driver");
            string key;if(Aliases.TryGetValue(raw,out key))return T(key);
            for(int i=0;i<Keys.Length;i++)if(raw==Packs[0][i])return T(Keys[i]);
            if(raw.StartsWith("● "))return "● "+(raw.Contains("Xpert")?"Xpert "+T("binding"):(raw.Contains("scope")?"scope ":"art ")+T("driver"))+": "+T(raw.EndsWith("通过")?"pass":"pending");
            if(raw=="雄新电力科技 · HV-DAC Xpert 600 · 信号汇集器")return "XIONGXIN · HV-DAC Xpert 600 · "+T("hub");
            if(raw.Contains("HV-DAC Xpert")&&raw.Contains("安装程序"))return "XIONGXIN · HV-DAC Xpert · "+T("installTitle");
            if(raw=="HV-DAC Xpert · 一体化安装")return "HV-DAC Xpert · "+T("installTitle");
            if(raw=="HV-DAC Xpert · 系统绑定进度")return "HV-DAC Xpert · "+T("progress");
            if(raw.StartsWith("信号汇集器  |"))return T("hub")+" | "+T("steps");
            var match=Regex.Match(raw,@"^已勾选 (\d+) 个模块$");if(match.Success)return T("selected",match.Groups[1].Value);
            match=Regex.Match(raw,@"^发现 (\d+) 台信号汇集器，(\d+) 个采集模块。");if(match.Success)return T("found",match.Groups[1].Value,match.Groups[2].Value);
            if(raw.StartsWith("安装日志："))return T("log")+": "+raw.Substring(5);
            if(raw.StartsWith("安装通信组件")||raw.StartsWith("请完成通信组件官方向导"))return T("installIntro");
            if(raw.StartsWith("1/4"))return "1/4  "+T("service")+" — "+T("verify");
            if(raw.StartsWith("2/4"))return "2/4  art — "+T("installing");
            if(raw.StartsWith("3/4"))return "3/4  scope — "+T("installing");
            if(raw.StartsWith("4/4"))return "4/4  HV-DAC Xpert — "+T("installing")+" / "+T("names");
            if(raw.StartsWith("步骤 1/3"))return "1/3  Xpert — "+T("binding");
            if(raw.StartsWith("步骤 2/3"))return "2/3  Windows — "+T("loadRequest");
            if(raw.StartsWith("步骤 3/3"))
            {
                match=Regex.Match(raw,@"剩余等待约 (\d+)");
                return "3/3  art: "+T(raw.Contains("art 已通过")?"pass":"pending")+"; scope: "+T(raw.Contains("scope 已通过")?"pass":"pending")+"; "+T("wait")+(match.Success?" ≤ "+match.Groups[1].Value+" s":"");
            }
            if(raw.StartsWith("校验等待结束"))return T("timeout");
            if(raw.StartsWith("Xpert 尚未"))return "Xpert — "+T("pending")+". "+T("wait");
            if(raw.Contains("演示"))return T("demo");
            if(raw.StartsWith("软件/驱动包检查"))return T("installed");
            string module=raw.Contains("scope")?"scope: ":raw.Contains("art")?"art: ":"";
            string diagnostic=Regex.Match(raw,@"E\d{3}|(?:状态码|状态|返回码)[=： ]*(-?\d+)").Value;
            diagnostic=Regex.Replace(diagnostic,@"[\u4e00-\u9fff]","").Trim('=',':','：',' ');
            string suffix=diagnostic.Length>0?" ["+diagnostic+"]":"";
            if(raw.Contains("E103"))return T("timeout")+suffix;
            if(raw.Contains("E201"))return T("offline")+suffix;
            if(raw.Contains("E202"))return T("wait")+suffix;
            if(raw.Contains("E203"))return T("missing")+suffix;
            if(raw.Contains("E204"))return T("notfound")+suffix;
            if(raw.Contains("E205"))return T("service")+": "+T("error")+suffix+". "+T("details");
            if(raw.Contains("E101")||raw.Contains("缺失")||raw.Contains("缺少")||raw.Contains("不兼容"))return module+T("missing")+suffix+". "+T("details");
            if(raw.Contains("E102")||raw.Contains("权限"))return T("permissions")+suffix;
            if(raw.Contains("占用"))return module+T("occupied")+suffix;
            if(raw.Contains("未同步"))return T("names")+": "+T("pending")+". "+T("details");
            if(raw.Contains("已请求系统")&&raw.Contains("驱动"))return module+T("loadRequest");
            if(raw.Contains("设备实例")||raw.Contains("实例尚未"))return module+T("portError")+suffix;
            if(raw.Contains("超时"))return module+T("timeout")+suffix;
            if(raw.Contains("失败")||raw.Contains("异常")||raw.Contains("未通过")||raw.Contains("未完成"))return module+T("error")+suffix+". "+T("details");
            if(raw.Contains("重启"))return T("reboot");
            if(raw.Contains("离线"))return module+T("offline")+suffix;
            if(raw.Contains("未就绪")||raw.Contains("等待")||raw.Contains("尚未"))return module+T("wait")+suffix;
            if(raw.Contains("已满足"))return module+T("version")+": "+T("already");
            if(raw.Contains("状态正常"))return module+T("driver")+": "+T("pass");
            if(raw.StartsWith("已取消自动绑定"))return module+T("settingsSaved");
            return T("details")+suffix; // Full original diagnostics remain in the on-disk support log.
        }
        public static ComboBox AddSelector(Form form,Action onChanged)
        {
            var panel=new FlowLayoutPanel {Dock=DockStyle.Top,Height=46,Padding=new Padding(24,5,24,5),FlowDirection=FlowDirection.RightToLeft};
            var selector=new ComboBox {DropDownStyle=ComboBoxStyle.DropDownList,Width=260,IntegralHeight=false,DropDownHeight=440};
            selector.Items.Add(T("system"));selector.Items.AddRange(Names.Cast<object>().ToArray());
            selector.SelectedIndex=choice=="auto"?0:Array.IndexOf(Codes,choice)+1;
            panel.Controls.Add(selector);panel.Controls.Add(new Label{Text="Language",AutoSize=true,Margin=new Padding(0,6,12,0)});form.Controls.Add(panel);panel.SendToBack();
            bool refreshing=false;
            Action changed=()=>{refreshing=true;selector.Items[0]=T("system");selector.SelectedIndex=choice=="auto"?0:Array.IndexOf(Codes,choice)+1;refreshing=false;if(onChanged!=null)onChanged();};
            Changed+=changed;form.Disposed+=delegate{Changed-=changed;};
            selector.SelectedIndexChanged+=delegate{
                if(refreshing)return;
                try{SetChoice(selector.SelectedIndex==0?"auto":Codes[selector.SelectedIndex-1],true);}
                catch(Exception){refreshing=true;selector.SelectedIndex=choice=="auto"?0:Array.IndexOf(Codes,choice)+1;refreshing=false;MessageBox.Show(form,T("error")+". "+T("permissions"),"HV-DAC Xpert",MessageBoxButtons.OK,MessageBoxIcon.Warning);}
            };
            return selector;
        }
        public static void Attach(Form form)
        {
            var original=new Dictionary<Control,string>();bool updating=false;
            Action<Control> update=c=>{if(c.IsDisposed)return;updating=true;try{c.Text=Text(original[c]);}finally{updating=false;}};
            Action<Control> visit=null;
            visit=c=>{
                if(c is Label||c is ButtonBase||c is Form)
                {
                    original[c]=c.Text;
                    c.TextChanged+=delegate{if(updating)return;original[c]=c.Text;update(c);};update(c);
                }
                foreach(Control child in c.Controls)visit(child);
            };
            visit(form);Action changed=()=>{foreach(var c in original.Keys.ToArray())update(c);};
            Changed+=changed;form.Disposed+=delegate{Changed-=changed;original.Clear();};
        }
    }
}
