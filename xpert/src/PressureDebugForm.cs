using System;
using System.Drawing;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Xiongxin.OWTS.Desktop
{
    // Standalone diagnostic view only: no SDK, reconnect, capture or output calls.
    internal sealed class PressureDebugForm:Form
    {
        readonly Diagnostics log;
        readonly Func<string> snapshot;
        readonly TextBox stateBox,logBox,pathBox;
        readonly CheckBox follow,showPolling;
        readonly Timer timer;
        bool reading;
        string lastVersion="";
        string rawTail="";
        internal PressureDebugForm(Diagnostics diagnostics,Func<string> status,Icon icon)
        {
            log=diagnostics;snapshot=status;Icon=icon;
            Text="加压测试 · 调试信息";Name="PressureDebugWindow";
            ClientSize=new Size(1040,700);MinimumSize=new Size(600,400);
            StartPosition=FormStartPosition.CenterParent;ShowInTaskbar=false;
            Font=new Font("Microsoft YaHei UI",10);
            var layout=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=1,RowCount=5,Padding=new Padding(10)};
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute,90));layout.RowStyles.Add(new RowStyle(SizeType.Absolute,35));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent,100));layout.RowStyles.Add(new RowStyle(SizeType.Absolute,38));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute,44));Controls.Add(layout);
            stateBox=new TextBox{Multiline=true,ReadOnly=true,Dock=DockStyle.Fill,ScrollBars=ScrollBars.Vertical};
            pathBox=new TextBox{ReadOnly=true,Dock=DockStyle.Fill};
            logBox=new TextBox{Multiline=true,ReadOnly=true,Dock=DockStyle.Fill,ScrollBars=ScrollBars.Both,WordWrap=false,MaxLength=0};
            layout.Controls.Add(stateBox,0,0);layout.Controls.Add(pathBox,0,1);layout.Controls.Add(logBox,0,2);
            var actions=new FlowLayoutPanel{Dock=DockStyle.Fill};layout.Controls.Add(actions,0,3);
            follow=new CheckBox{Text="自动刷新（只读）",Checked=true,AutoSize=true};actions.Controls.Add(follow);
            showPolling=new CheckBox{Text="显示连接轮询",Checked=false,AutoSize=true};actions.Controls.Add(showPolling);
            showPolling.CheckedChanged+=delegate{logBox.Text=FilterPolling(rawTail,showPolling.Checked);};
            var copy=new HVX.Presentation.R2RoundedButton{Text="复制信息",AutoSize=true};copy.Click+=delegate{try{Clipboard.SetText(stateBox.Text+Environment.NewLine+pathBox.Text+Environment.NewLine+logBox.Text);}catch(Exception e){stateBox.AppendText(Environment.NewLine+e.Message);}};actions.Controls.Add(copy);
            var export=new HVX.Presentation.R2RoundedButton{Text="导出日志",AutoSize=true};export.Click+=Export;actions.Controls.Add(export);
            layout.Controls.Add(new Label{Text="读取最近128 KiB；默认隐藏重复在线轮询，导出保留原文。完整日志保留在上述目录。时间为 UTC。此窗口不操作设备，不清除故障，也不恢复加压。",Dock=DockStyle.Fill},0,4);
            timer=new Timer{Interval=1000};timer.Tick+=delegate{if(follow.Checked)RefreshLog();};
            Shown+=delegate{RefreshLog();timer.Start();};FormClosed+=delegate{timer.Stop();timer.Dispose();};
        }
        internal static string ReadTail(string path)
        {
            if(!File.Exists(path))return "尚无运行日志。";
            using(var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete))
            {
                bool truncated=stream.Length>131072;
                stream.Position=Math.Max(0,stream.Length-131072);
                using(var reader=new StreamReader(stream,Encoding.UTF8,true))
                {
                    if(truncated)reader.ReadLine(); // discard the potentially split first UTF-8 line
                    return reader.ReadToEnd();
                }
            }
        }
        internal static string FilterPolling(string text,bool show)
        {
            if(show||String.IsNullOrEmpty(text))return text;
            var result=new StringBuilder();
            using(var reader=new StringReader(text)){string line;while((line=reader.ReadLine())!=null){
                if(line.IndexOf("[INFO] PressureWorker.LINK_ART_NAME",StringComparison.Ordinal)>=0||
                   line.IndexOf("[INFO] PressureWorker.LINK_PICO_ONLINE",StringComparison.Ordinal)>=0)continue;
                result.AppendLine(line);
            }}return result.ToString();
        }
        async void RefreshLog()
        {
            if(reading||IsDisposed)return;reading=true;
            try {
                stateBox.Text=snapshot();string path=log.CurrentLogPath;pathBox.Text=path;
                string version=await Task.Run(()=>File.Exists(path)?new FileInfo(path).Length+"|"+File.GetLastWriteTimeUtc(path).Ticks:path);
                if(IsDisposed||Disposing)return;
                if(version==lastVersion)return;
                string text=await Task.Run(()=>ReadTail(path));
                if(IsDisposed||Disposing)return;
                rawTail=text;logBox.Text=FilterPolling(text,showPolling.Checked);logBox.SelectionStart=logBox.TextLength;logBox.ScrollToCaret();lastVersion=version;
            } catch(Exception error){if(!IsDisposed&&!Disposing)stateBox.Text="日志读取失败："+error.Message;}
            finally {reading=false;}
        }
        void Export(object sender,EventArgs args)
        {
            using(var dialog=new SaveFileDialog{Filter="文本日志|*.txt",FileName="加压调试-"+DateTime.Now.ToString("yyyyMMdd-HHmmss")+".txt"})
                if(dialog.ShowDialog(this)==DialogResult.OK)
                    try {File.WriteAllText(dialog.FileName,stateBox.Text+Environment.NewLine+pathBox.Text+Environment.NewLine+rawTail,Encoding.UTF8);}
                    catch(Exception e){MessageBox.Show(this,e.Message,"导出失败",MessageBoxButtons.OK,MessageBoxIcon.Error);}
        }
    }
}
