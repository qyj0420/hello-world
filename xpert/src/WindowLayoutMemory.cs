using System;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Windows.Forms;
using System.Xml;

namespace Xiongxin.OWTS.Desktop
{
    // Presentation preferences only. Never saves project, calibration or device
    // parameters, and never substitutes layout memory for fresh project data.
    internal sealed class WindowLayoutMemory
    {
        internal Rectangle Bounds, ScreenBounds, WorkingArea;
        internal string Monitor;
        internal int Dpi;
        internal bool Maximized;

        internal bool Matches(string monitor, Rectangle screen, Rectangle work, int dpi)
        {
            return String.Equals(Monitor, monitor, StringComparison.Ordinal) &&
                ScreenBounds == screen && WorkingArea == work && Dpi == dpi &&
                Bounds.Width >= 320 && Bounds.Height >= 240 && work.Contains(Bounds);
        }
        static int Read(XmlElement e, string name)
        { return Int32.Parse(e.GetAttribute(name), NumberStyles.Integer, CultureInfo.InvariantCulture); }
        static Rectangle RectangleFrom(XmlElement e, string prefix)
        { return new Rectangle(Read(e, prefix+"X"), Read(e, prefix+"Y"), Read(e, prefix+"W"), Read(e, prefix+"H")); }
        internal static WindowLayoutMemory Load(string path)
        {
            try {
                if (!File.Exists(path) || new FileInfo(path).Length > 8192) return null;
                var doc = new XmlDocument { XmlResolver = null };
                using (var reader = XmlReader.Create(path, new XmlReaderSettings { DtdProcessing=DtdProcessing.Prohibit, XmlResolver=null, MaxCharactersInDocument=8192 })) doc.Load(reader);
                var e=doc.DocumentElement;
                if(e==null || e.Name!="WindowLayout" || e.GetAttribute("version")!="1")return null;
                var value=new WindowLayoutMemory {Monitor=e.GetAttribute("monitor"),Dpi=Read(e,"dpi"),
                    Bounds=RectangleFrom(e,"normal"),ScreenBounds=RectangleFrom(e,"screen"),WorkingArea=RectangleFrom(e,"work"),Maximized=e.GetAttribute("maximized")=="true"};
                if(value.Dpi<48 || value.Dpi>960 || value.Monitor.Length==0 || value.Monitor.Length>256 ||
                    value.ScreenBounds.Width<320 || value.ScreenBounds.Height<240 || value.Bounds.Width>100000 || value.Bounds.Height>100000)return null;
                return value;
            } catch(Exception e) when(e is IOException || e is UnauthorizedAccessException || e is XmlException || e is FormatException || e is OverflowException || e is ArgumentException) { return null; }
        }
        static void WriteRectangle(XmlElement e,string prefix,Rectangle r)
        {
            e.SetAttribute(prefix+"X",r.X.ToString(CultureInfo.InvariantCulture));e.SetAttribute(prefix+"Y",r.Y.ToString(CultureInfo.InvariantCulture));
            e.SetAttribute(prefix+"W",r.Width.ToString(CultureInfo.InvariantCulture));e.SetAttribute(prefix+"H",r.Height.ToString(CultureInfo.InvariantCulture));
        }
        internal bool Save(string path)
        {
            string temporary=null;
            try {
                if(!Matches(Monitor,ScreenBounds,WorkingArea,Dpi))return false;
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
                var doc=new XmlDocument {XmlResolver=null};var e=doc.CreateElement("WindowLayout");doc.AppendChild(e);
                e.SetAttribute("version","1");e.SetAttribute("monitor",Monitor);e.SetAttribute("dpi",Dpi.ToString(CultureInfo.InvariantCulture));e.SetAttribute("maximized",Maximized?"true":"false");
                WriteRectangle(e,"normal",Bounds);WriteRectangle(e,"screen",ScreenBounds);WriteRectangle(e,"work",WorkingArea);
                temporary=path+"."+Guid.NewGuid().ToString("N")+".tmp";doc.Save(temporary);
                if(File.Exists(path))File.Replace(temporary,path,null);else File.Move(temporary,path);
                temporary=null;return true;
            } catch(Exception e) when(e is IOException || e is UnauthorizedAccessException || e is ArgumentException) { return false; }
            finally { if(temporary!=null)try{File.Delete(temporary);}catch(IOException){}catch(UnauthorizedAccessException){} }
        }
    }

    internal sealed partial class MainForm
    {
        void RestoreWindowLayout()
        {
            if(verificationMode)return;
            var saved=WindowLayoutMemory.Load(Path.Combine(AppPaths.LocalDataDirectory,"window-layout.xml"));
            if(saved==null)return;
            foreach(var screen in Screen.AllScreens)if(saved.Matches(screen.DeviceName,screen.Bounds,screen.WorkingArea,DeviceDpi)) {
                StartPosition=FormStartPosition.Manual;Bounds=saved.Bounds;
                if(saved.Maximized)WindowState=FormWindowState.Maximized;
                return;
            }
            // Changed monitor, resolution or DPI: normal adaptive fitting runs.
        }
        void SaveWindowLayout()
        {
            if(verificationMode || WindowState==FormWindowState.Minimized)return;
            var screen=Screen.FromControl(this);
            new WindowLayoutMemory {Monitor=screen.DeviceName,Dpi=DeviceDpi,ScreenBounds=screen.Bounds,WorkingArea=screen.WorkingArea,
                Bounds=WindowState==FormWindowState.Normal?Bounds:RestoreBounds,Maximized=WindowState==FormWindowState.Maximized}
                .Save(Path.Combine(AppPaths.LocalDataDirectory,"window-layout.xml"));
        }
    }
}
