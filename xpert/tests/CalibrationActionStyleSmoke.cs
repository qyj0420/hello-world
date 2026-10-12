using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;

internal static class CalibrationActionStyleSmoke
{
    const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    static Type type;
    static int checks;
    static object Get(object value, string name) { return value.GetType().GetProperty(name, All).GetValue(value, null); }
    static object Field(object value, string name) { return value.GetType().GetField(name, All).GetValue(value); }
    static void Set(object value, string name, object data) { value.GetType().GetProperty(name, All).SetValue(value, data, null); }
    static object Call(object value, string name, params object[] args)
    { return value.GetType().GetMethods(All).Single(m => m.Name == name && m.GetParameters().Length == args.Length).Invoke(value, args); }
    static void Check(bool result, string message) { checks++; if (!result) throw new InvalidOperationException(message); }
    static RectangleF Rect(object geometry, string name) { return (RectangleF)Field(geometry, name); }
    static Point Mid(RectangleF rect) { return Point.Round(new PointF(rect.Left + rect.Width / 2, rect.Top + rect.Height / 2)); }
    static Control NewControl(int height, bool embedded)
    {
        var control = (Control)Activator.CreateInstance(type);
        Set(control, "SimulationMode", true); Set(control, "EmbeddedMode", embedded);
        control.Size = new Size(1280, height); Set(control, "SelectedLevelCode", "TDR");
        return control;
    }
    [STAThread]
    static int Main(string[] args)
    {
        try
        {
            if (args.Length != 2) throw new ArgumentException("Pass isolated release root and evidence directory.");
            string output = Path.GetFullPath(args[1]); Directory.CreateDirectory(output);
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            type = Assembly.LoadFrom(Path.Combine(Path.GetFullPath(args[0]), "modules", "CalibrationTest", "Xiongxin.OWTS.CalibrationTest.dll"))
                .GetType("HVX.Controls.CalibrationProgramControl", true);
            Check((float)type.GetField("CompactActionFontSize", All).GetRawConstantValue() == 10f,
                "Action text keeps the original primary 10pt size, not a 30% font increase");
            foreach (bool embedded in new[] { true, false }) foreach (int height in new[] { 720, 600 })
            using (Control control = NewControl(height, embedded))
            {
                object ui = Get(control, "CompactLayout");
                RectangleF confirm = Rect(ui, "ConfirmJoints"), save = Rect(ui, "Save"), start = Rect(ui, "Start"),
                    right = Rect(ui, "Right"), levels = Rect(ui, "AllLevels");
                Check(start.Height == 94, "Primary height rounds 72 * 1.30 to 94 pixels");
                Check(Math.Abs(start.Bottom - (height - 20)) < .01, "Primary action retains bottom anchor");
                Check(confirm.Height == 36 && save.Height == 40, "Secondary heights unchanged");
                Check(save.Top - confirm.Bottom == 8 && start.Top - save.Bottom == 8, "Action order and eight-pixel gaps preserved");
                Check(confirm.Left == start.Left && save.Left == start.Left && confirm.Width == start.Width && save.Width == start.Width,
                    "Three actions keep the same right-column alignment");
                Check(levels.Bottom + 8 <= confirm.Top, "Actions do not overlap calibration levels");
                foreach (RectangleF r in new[] { confirm, save, start })
                {
                    Check(right.Contains(r) && control.ClientRectangle.Contains(Rectangle.Ceiling(r)), "Action remains fully visible");
                    Point p = Mid(r);
                    Check(new[] { confirm, save, start }.Count(candidate => candidate.Contains(p)) == 1, "Action center has exactly one hit target");
                }
                string suffix = (embedded ? "embedded-" : "standalone-") + height;
                Call(control, "SavePng", Path.Combine(output, "calibration-actions-ranging-" + suffix + ".png"), 1280, height);
                Check(((string)Get(control, "CalibrationSaveCaption")).Contains("保存电缆长度"), "Ranging save caption unchanged");
                Set(control, "SelectedLevelCode", "5nC");
                Call(control, "SavePng", Path.Combine(output, "calibration-actions-calibrating-" + suffix + ".png"), 1280, height);
                // Exercise the newly enlarged 22px top strip. Simulation prevents hardware access.
                Point expanded = new Point((int)(start.Left + start.Width / 2), (int)start.Top + 5);
                Check(!save.Contains(expanded) && !confirm.Contains(expanded), "Expanded primary strip cannot hit adjacent actions");
                long sequence = (long)Get(control, "StartRequestSequence");
                Call(control, "HandleCompactMouseUp", new MouseEventArgs(MouseButtons.Left, 1, expanded.X, expanded.Y, 0));
                Check((long)Get(control, "StartRequestSequence") == sequence + 1, "Expanded primary strip dispatches Start exactly once");
            }
            foreach (int height in new[] { 600, 720 }) using (Control control = NewControl(height, true))
            {
                object ui = Get(control, "CompactLayout"); RectangleF save = Rect(ui, "Save"), start = Rect(ui, "Start");
                Point gap = new Point((int)(start.Left + start.Width / 2), (int)(save.Bottom + 4));
                Call(control, "HandleCompactMouseUp", new MouseEventArgs(MouseButtons.Left, 1, gap.X, gap.Y, 0));
                Check((long)Get(control, "StartRequestSequence") == 0, "Inter-button gap does not start acquisition");
            }
            Console.WriteLine("CALIBRATION_ACTION_STYLE_PASS=" + checks);
            Console.WriteLine("EVIDENCE=" + output); return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
