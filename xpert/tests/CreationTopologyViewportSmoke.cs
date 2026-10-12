using System;
using System.Collections;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Windows.Forms;

internal static class CreationTopologyViewportSmoke
{
    const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    static int checks;
    static object Field(object o, string n) { return o.GetType().GetField(n, All).GetValue(o); }
    static void Set(object o, string n, object v) { o.GetType().GetProperty(n, All).SetValue(o, v, null); }
    static object Call(object o, string n, params object[] p) { return o.GetType().GetMethods(All).Single(m => m.Name == n && m.GetParameters().Length == p.Length).Invoke(o, p); }
    static void Check(bool ok, string text) { checks++; if (!ok) throw new Exception(text); }
    static void Pump() { for (int i = 0; i < 5; i++) Application.DoEvents(); }
    static string Block(string p) { return "0\t" + p + "\t\t\r\n1\t始端\t冷缩\t0m\r\n2\t本体\t塑料绝缘\t\r\n1\t接头 1\t冷缩\t80m\r\n2\t本体\t塑料绝缘\t\r\n1\t终端\t冷缩\t414.8m\r\n"; }
    static void Draw(Control c) { using (var b = new Bitmap(c.Width, c.Height)) c.DrawToBitmap(b, c.ClientRectangle); }
    [STAThread] static int Main(string[] args)
    {
        try
        {
            Application.EnableVisualStyles(); string qa = Path.Combine(Path.GetFullPath(args[1]), "topology-scroll-" + Guid.NewGuid().ToString("N").Substring(0, 10)); Directory.CreateDirectory(qa);
            var a = Assembly.LoadFrom(Path.Combine(Path.GetFullPath(args[0]), "modules", "ProjectCreation", "Xiongxin.OWTS.ProjectCreation.dll"));
            using (var c = (Control)Activator.CreateInstance(a.GetType("HVX.Controls.ProjectCreationControl", true)))
            using (var host = new Form { AutoScaleMode = AutoScaleMode.None, StartPosition = FormStartPosition.Manual, Location = new Point(-20000, -20000), ShowInTaskbar = false, ClientSize = new Size(1065, 500) })
            {
                Set(c, "EmbeddedMode", true); Set(c, "ShowDialogMessages", false); Set(c, "OnlineWeatherEnabled", false); Set(c, "AutoRefreshWeatherOnHardwareSample", false); Set(c, "SettingsPath", Path.Combine(qa, "settings.ini"));
                c.Dock = DockStyle.Fill; host.Controls.Add(c); host.Show(); Pump();
                foreach (string mode in new[] { "三相单芯", "一体三芯" })
                {
                    string p = Path.Combine(qa, mode); Directory.CreateDirectory(p);
                    File.WriteAllText(Path.Combine(p, "ITEM.in"), "[ITEM]\r\n始端地点=QA始端\r\n终端地点=QA终端\r\n相电压=8.7kV\r\n电缆芯数=" + mode + "\r\nA相电缆长度=414.8\r\nB相电缆长度=414.8\r\nC相电缆长度=414.8\r\n", Encoding.UTF8);
                    File.WriteAllText(Path.Combine(p, "CABLE.in"), Block("A相") + Block("B相") + Block("C相"), Encoding.UTF8);
                    byte[] original = File.ReadAllBytes(Path.Combine(p, "CABLE.in"));
                    Check((bool)Call(c, "LoadProject", p), "synthetic fixture loads"); Call(c, "SetOptionalSectionExpanded", "JointsAndBody", true);
                    foreach (int language in new[] { 0, 18, 1 }) foreach (int height in new[] { 500, 560, 620, 720, 900 }) foreach (bool longName in new[] { false, true })
                    {
                        Set(c, "LanguageIndex", language); host.ClientSize = new Size(1065, height);
                        string name = longName ? String.Concat(Enumerable.Repeat("用户完整端点名称 / Endpoint long name ", 7)) : "变电站050537 / Substation050537";
                        Set(c, "StartName", name); Set(c, "EndName", name); Call(c, "RefreshCableView"); Pump();
                        Control canvas = (Control)Field(c, "topology"); var viewport = canvas.Parent as Panel;
                        Check(viewport != null && viewport.AutoScroll && viewport.Name == "CableTopologyViewport", "independent scroll viewport");
                        viewport.AutoScrollPosition = Point.Empty; Pump(); Draw(canvas);
                        Check(!viewport.HorizontalScroll.Visible, "no horizontal scrollbar at supported width");
                        var table = (TableLayoutPanel)viewport.Parent;
                        int row = table.GetRow(viewport), span = table.GetRowSpan(viewport);
                        var heights = table.GetRowHeights();
                        int top = table.Padding.Top + heights.Take(row).Sum(), bottom = table.Padding.Top + heights.Take(row + span).Sum();
                        Check(viewport.Top >= top && viewport.Bottom <= bottom, "viewport remains in its own row below terminal selectors");
                        Control footer = table.GetControlFromPosition(0, row + span);
                        Check(footer != null && viewport.Bottom <= footer.Top, "viewport cannot enter fixed footer row");
                        Size before = canvas.Size; Draw(canvas); Check(canvas.Size == before, "paint does not mutate layout");
                        Rectangle label = (Rectangle)Field(canvas, "StartNameBounds"); Check(label.Bottom < canvas.Height, "complete endpoint caption remains part of scroll content");
                        var markers = ((IEnumerable)canvas.GetType().GetProperty("RenderedJointMarkers", All).GetValue(canvas, null)).Cast<object>().ToArray();
                        Check(markers.Length == (mode == "三相单芯" ? 3 : 1), "all physical phase rows rendered");
                        foreach (object marker in markers)
                        {
                            Rectangle r = (Rectangle)Field(marker, "Bounds");
                            viewport.AutoScrollPosition = new Point(0, Math.Max(0, r.Top - viewport.ClientSize.Height / 2)); Pump();
                            Rectangle actual = canvas.RectangleToScreen(r), visible = viewport.RectangleToScreen(viewport.ClientRectangle);
                            for (Control ancestor = viewport.Parent; ancestor != null; ancestor = ancestor.Parent) visible = Rectangle.Intersect(visible, ancestor.RectangleToScreen(ancestor.ClientRectangle));
                            Check(visible.Contains(actual), "actual scrolling reaches marker fully: " + mode + "/" + language + "/" + height + "/" + longName + "/" + Field(marker, "Phase"));
                        }
                        if (language == 0 && height == 500 && mode == "三相单芯") using (var b = new Bitmap(c.Width, c.Height)) { c.DrawToBitmap(b, c.ClientRectangle); b.Save(Path.Combine(qa, longName ? "long-names-C-visible.png" : "C-visible.png")); }
                    }
                    Check(File.ReadAllBytes(Path.Combine(p, "CABLE.in")).SequenceEqual(original), "layout never changes project bytes");
                }
            }
            Console.WriteLine("PASS " + checks + "; " + qa); return 0;
        }
        catch (Exception e) { Console.Error.WriteLine(e); return 1; }
    }
}
