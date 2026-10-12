using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Xiongxin.FactoryTest
{
    internal sealed class FactoryConnectionJournal
    {
        internal readonly string FilePath;
        internal readonly List<string> Lines = new List<string>();
        internal int Incidents { get; private set; }
        internal bool Interrupted { get; private set; }
        private readonly string art, pico;
        private readonly Dictionary<string,DateTime> interrupted = new Dictionary<string,DateTime>();
        internal string TaskName = "操作等待", Progress = "等待操作", Record = "";
        internal FactoryConnectionJournal(string root, string art, string pico)
        {
            this.art=art;this.pico=pico;
            string key=art.Length+":"+art+pico.Length+":"+pico,digest;
            using(var hash=SHA256.Create())digest=BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(key))).Replace("-","");
            string directory=Path.Combine(root,digest);Directory.CreateDirectory(directory);
            FilePath=Path.Combine(directory,DateTime.Now.ToString("yyyyMMdd_HHmmss_fff")+"_"+Guid.NewGuid().ToString("N")+".tsv");
            File.WriteAllText(FilePath,"时间(含时区)\t设备\tART序列号\tPico序列号\t任务\t进度\t事件\t中断秒数\t处理详情\t原始记录\r\n",new UTF8Encoding(true));
        }
        internal void Fault(string device, DateTime at, string detail, string kind = "通信中断/读取异常")
        {
            Interrupted=true;
            if(interrupted.ContainsKey(device))return;
            interrupted[device]=at;++Incidents;Write(device,at,kind,"",detail);
        }
        internal void Event(string device,DateTime at,string kind,string detail)
        { Write(device,at,kind,"",detail); }
        internal void EnterPostProcessing()
        { Progress="本轮收尾已返回，后处理/轮间等待"; }
        internal void EnterIdle()
        { TaskName="操作等待";Progress="等待用户操作";Record=""; }
        internal void Online(string device, DateTime at)
        {
            DateTime began;if(!interrupted.TryGetValue(device,out began))return;
            interrupted.Remove(device);
            Write(device,at,"原设备通信恢复",Math.Max(0,(at-began).TotalSeconds).ToString("0.000",CultureInfo.InvariantCulture),"仅恢复检测；不自动恢复HV/光触发，不补记中断轮为完成");
        }
        internal void Result(string detail)
        {
            var now=DateTime.UtcNow;
            Write("控制/任务",now,"处理结果","",detail);
            foreach(var item in interrupted)
                Write(item.Key,now,"尚未恢复",Math.Max(0,(now-item.Value).TotalSeconds).ToString("0.000",CultureInfo.InvariantCulture),"截至本次处理结果仍未获得原设备恢复证据");
        }
        private void Write(string device,DateTime at,string kind,string duration,string detail)
        {
            string line=String.Join("\t",Array.ConvertAll(new[]{at.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.fff zzz"),device,art,pico,TaskName,Progress,kind,duration,detail,Record},Clean));
            File.AppendAllText(FilePath,line+Environment.NewLine,new UTF8Encoding(true));Lines.Add(line);
        }
        private static string Clean(string s) {return (s??"").Replace("\r"," ").Replace("\n"," ").Replace("\t"," ");}
        internal string Describe()
        {
            string pending="";
            foreach(var item in interrupted)pending+="\r\n"+item.Key+" 尚未恢复；开始 "+item.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.fff")+"；已持续 "+Math.Max(0,(DateTime.UtcNow-item.Value).TotalSeconds).ToString("0.000")+" 秒。";
            return "ART " + art + " / Pico " + pico + "\r\n时间(含时区)\t设备\tART序列号\tPico序列号\t任务\t进度\t事件\t中断秒数\t处理详情\t原始记录\r\n" + String.Join("\r\n",Lines.ToArray()) + pending+"\r\n日志："+FilePath;
        }
    }
}
