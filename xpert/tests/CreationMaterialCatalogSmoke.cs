using System;
using System.Collections;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Windows.Forms;

internal static class CreationMaterialCatalogSmoke
{
    const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    static int checks;
    static object Field(object value, string name) { return value.GetType().GetField(name, All).GetValue(value); }
    static object Get(object value, string name) { return value.GetType().GetProperty(name, All).GetValue(value, null); }
    static void Set(object value, string name, object content) { value.GetType().GetProperty(name, All).SetValue(value, content, null); }
    static object Call(object value, string name, params object[] args)
    { Type type = value as Type ?? value.GetType(); return type.GetMethods(All).Single(m => m.Name == name && m.GetParameters().Length == args.Length).Invoke(value is Type ? null : value, args); }
    static void Check(bool ok, string text) { checks++; if (!ok) throw new Exception(text); }
    static void Pump() { for (int i = 0; i < 4; i++) Application.DoEvents(); }
    static object[] Materials(object catalog) { return ((IEnumerable)Field(catalog, "Materials")).Cast<object>().ToArray(); }
    static string[] Names(object catalog) { return Materials(catalog).Select(m => (string)Field(m, "Name")).ToArray(); }
    static string Choice(object module, ComboBox combo) { return (string)Call(module, "GetChoiceValue", combo); }
    sealed class QaForm : Form { protected override bool ShowWithoutActivation { get { return true; } } }

    [STAThread] static int Main(string[] args)
    {
        try
        {
            if (args.Length != 2) throw new ArgumentException("Pass BuildRoot and QA directory.");
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            string qa = Path.Combine(Path.GetFullPath(args[1]), "materials-" + Guid.NewGuid().ToString("N").Substring(0, 12)); Directory.CreateDirectory(qa);
            Assembly assembly = Assembly.LoadFrom(Path.Combine(Path.GetFullPath(args[0]), "modules", "ProjectCreation", "Xiongxin.OWTS.ProjectCreation.dll"));
            Type catalogType = assembly.GetType("HVX.Controls.ReferenceCatalog", true), dialogType = assembly.GetType("HVX.Controls.MaterialEditorDialog", true);
            string settings = Path.Combine(qa, "settings.ini");
            object defaults = Call(catalogType, "CreateDefaults"); Call(defaults, "Save", settings);
            Check(Names(Call(catalogType, "Load", settings)).Length == 3, "Old INI defaults round-trip");
            string invalid = Path.Combine(qa, "legacy.ini");
            File.WriteAllText(invalid, "[Materials]\r\nitem=valid|161.1256789\r\nitem=VALID|180\r\nitem=bad|NaN\r\nitem=infinity|Infinity\r\nitem=zero|0\r\n", Encoding.UTF8);
            object loaded = Call(catalogType, "Load", invalid);
            Check(Names(loaded).SequenceEqual(new[] { "valid" }), "Legacy invalid, nonfinite, duplicate rows do not enter catalog");
            Check(Math.Abs((double)Field(Materials(loaded)[0], "Velocity") - 161.1256789) < 1e-10, "Velocity precision retained");
            foreach (double value in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity, 0, -1 })
                Check(!(bool)Call(catalogType, "IsValidMaterial", "test", value), "Reject nonphysical velocity " + value);

            using (var module = (Control)Activator.CreateInstance(assembly.GetType("HVX.Controls.ProjectCreationControl", true)))
            using (var host = new QaForm { StartPosition = FormStartPosition.Manual, Location = new Point(-20000, -20000), ShowInTaskbar = false, ClientSize = new Size(1280, 850) })
            {
                Set(module, "ShowDialogMessages", false); Set(module, "OnlineWeatherEnabled", false); Set(module, "AutoRefreshWeatherOnHardwareSample", false);
                Set(module, "EmbeddedMode", true); Set(module, "SettingsPath", settings); Check((bool)Call(module, "ReloadSettings"), "Private catalog loaded");
                module.Dock = DockStyle.Fill; host.Controls.Add(module); host.Show(); Pump();
                var before = (ComboBox)Field(module, "beforeBodyTypeCombo"); var after = (ComboBox)Field(module, "bodyTypeCombo");
                Check(before.ContextMenuStrip == after.ContextMenuStrip && !before.ContextMenuStrip.IsDisposed, "Both material editors own live shared menu");
                object catalog = Field(module, "catalog");
                Check((bool)Call(module, "TrySaveMaterial", "QA 新绝缘", 172.1234567, false, before), "Add material from before-side editor");
                Check(Choice(module, before) == "QA 新绝缘", "Added material selected in source editor");
                Check(after.Items.Cast<object>().Any(v => v.ToString() == "QA 新绝缘"), "Other-side list refreshed");
                Check(Names(Call(catalogType, "Load", settings)).Contains("QA 新绝缘"), "Added material persisted");
                Check(Math.Abs((double)Field(Materials(Call(catalogType, "Load", settings)).Single(v => (string)Field(v, "Name") == "QA 新绝缘"), "Velocity") - 172.1234567) < 1e-10, "Custom velocity persists without truncation");
                byte[] original = File.ReadAllBytes(settings);
                Check(!(bool)Call(module, "TrySaveMaterial", "QA 新绝缘", 150.0, false, before), "Duplicate requires explicit replacement");
                Check(original.SequenceEqual(File.ReadAllBytes(settings)), "Duplicate rejection leaves file unchanged");
                Check((bool)Call(module, "TrySaveMaterial", "QA 新绝缘", 155.5, true, after), "Confirmed duplicate updates velocity");
                Check(Names(catalog).Count(n => n == "QA 新绝缘") == 1 && after.Items.Cast<object>().Count(v => v.ToString() == "QA 新绝缘") == 1, "No mixed string/LocalizedChoice duplicate");
                bool beforeIntent = (bool)Field(module, "jointBeforeMaterialChanged"), afterIntent = (bool)Field(module, "jointAfterMaterialChanged");
                Check((bool)Call(module, "TryDeleteMaterial", "QA 新绝缘"), "Delete material from dictionary");
                Check(!Names(catalog).Contains("QA 新绝缘") && !Names(Call(catalogType, "Load", settings)).Contains("QA 新绝缘"), "Deletion persists without adding selected value back to catalog");
                Check(Choice(module, before) == "QA 新绝缘" && Choice(module, after) == "QA 新绝缘", "Current draft material is not silently substituted on deletion");
                Check(before.Text.Contains("不在材料字典") && after.Text.Contains("不在材料字典"), "Retained current value clearly distinguished from catalog entries");
                Check(beforeIntent == (bool)Field(module, "jointBeforeMaterialChanged") && afterIntent == (bool)Field(module, "jointAfterMaterialChanged"), "Catalog refresh does not manufacture material edits");
                Check((bool)Call(module, "ReloadSettings") && !Names(Field(module, "catalog")).Contains("QA 新绝缘"), "Reload does not resurrect deleted catalog item");
                catalog = Field(module, "catalog");
                original = File.ReadAllBytes(settings);
                using (var locked = new FileStream(settings, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                    Check(!(bool)Call(module, "TrySaveMaterial", "must-not-appear", 163.0, false, after), "Locked settings save fails visibly");
                Check(original.SequenceEqual(File.ReadAllBytes(settings)) && !Names(catalog).Contains("must-not-appear"), "Save failure preserves disk and live catalog");
                File.SetAttributes(settings, File.GetAttributes(settings) | FileAttributes.ReadOnly);
                try { Check(!(bool)Call(module, "TrySaveMaterial", "readonly-reject", 167.0, false, after), "Read-only catalog not silently overridden"); }
                finally { File.SetAttributes(settings, FileAttributes.Normal); }
                Check(original.SequenceEqual(File.ReadAllBytes(settings)) && Directory.GetFiles(qa, "*.tmp").Length == 0, "Failed atomic saves keep original and clean owned temp files");
                foreach (string bad in new[] { "", "a|b", "a\nb", "a\r", "a\\n", "a\0b" })
                    Check(!(bool)Call(module, "TrySaveMaterial", bad, 170.0, false, after), "Invalid material name rejected");

                string project = Path.Combine(qa, "project"); Directory.CreateDirectory(project);
                string item = "[ITEM]\r\n始端地点 = QA start\r\n终端地点 = QA end\r\n电缆芯数 = 一体三芯\r\n相电压 = 8.7kV\r\nA相电缆长度 = 400\r\nB相电缆长度 = 400\r\nC相电缆长度 = 400\r\n";
                string cable = "0\tA相\t\t\r\n1\t始端\t户内冷缩\t0m\r\n2\t本体\tQA 新绝缘\t\r\n1\t接头 1\t户内冷缩\t80m\r\n2\t本体\tQA 新绝缘\t\r\n1\t终端\t户内冷缩\t400m\r\n";
                File.WriteAllText(Path.Combine(project, "ITEM.in"), item, new UTF8Encoding(true)); File.WriteAllText(Path.Combine(project, "CABLE.in"), cable, new UTF8Encoding(true));
                Check((bool)Call(module, "LoadProject", project), "Project with removed material still loads");
                Check(!Names(Call(catalogType, "Load", settings)).Contains("QA 新绝缘"), "Loading history no longer restores removed material at invented 170 velocity");
                Check(Choice(module, before) == "QA 新绝缘" && Choice(module, after) == "QA 新绝缘", "Loaded historical material still shown in both editors");
                Check(File.ReadAllText(Path.Combine(project, "ITEM.in")) == item && File.ReadAllText(Path.Combine(project, "CABLE.in")) == cable, "Material edits/load do not mutate project files");

                for (int i = 0; i < 12; i++)
                {
                    Set(module, "LanguageIndex", i % 2 == 0 ? 1 : 18); Pump();
                    ContextMenuStrip menu = after.ContextMenuStrip;
                    menu.Show(after, new Point(1, 1)); Pump();
                    menu.Items[2].PerformClick(); menu.Close(); Pump();
                    Check(!after.ContextMenuStrip.IsDisposed && before.ContextMenuStrip == after.ContextMenuStrip, "Repeated restore/language interactions retain live shared menu " + i);
                    Check(Names(Call(catalogType, "Load", settings)).Length == 3, "Restore defaults persisted " + i);
                }
                foreach (string name in Names(Field(module, "catalog")).Take(2).ToArray()) Check((bool)Call(module, "TryDeleteMaterial", name), "Delete until one remains");
                Check(!(bool)Call(module, "TryDeleteMaterial", Names(Field(module, "catalog"))[0]), "Last item cannot vanish silently");
            }

            foreach (int language in new[] { 0, 1, 18 })
                foreach (float font in new[] { 9f, 18f })
                    using (var dialog = (Form)Activator.CreateInstance(dialogType, All, null, new object[] { language }, CultureInfo.InvariantCulture))
                    {
                        dialog.StartPosition = FormStartPosition.Manual; dialog.Location = new Point(-20000, -20000); dialog.ShowInTaskbar = false;
                        dialog.Font = new Font("Microsoft YaHei UI", font); dialog.ClientSize = new Size(440, 300); dialog.Show(); Pump();
                        var name = (TextBox)Field(dialog, "nameInput"); var velocity = (TextBox)Field(dialog, "velocityInput");
                        Check(name != velocity && !name.Bounds.IntersectsWith(velocity.Bounds), "Two independent nonoverlapping material/velocity inputs");
                        name.Text = "Test insulation"; velocity.Text = "NaN"; Check(!(bool)Call(dialog, "TryAccept"), "Dialog rejects NaN");
                        velocity.Text = "165.875"; Check((bool)Call(dialog, "TryAccept") && Math.Abs((double)Get(dialog, "Velocity") - 165.875) < 1e-9, "Separate name/value accepted");
                        var save = (Control)dialog.AcceptButton; Rectangle saveRect = dialog.RectangleToClient(save.RectangleToScreen(save.ClientRectangle));
                        Check(dialog.ClientRectangle.Contains(saveRect), "Save button visible at minimum dialog/high font");
                        using (var bitmap = new Bitmap(dialog.Width, dialog.Height)) { dialog.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size)); bitmap.Save(Path.Combine(qa, "dialog-" + language + "-" + font.ToString(CultureInfo.InvariantCulture) + ".png")); }
                    }
            CultureInfo culture = Thread.CurrentThread.CurrentCulture;
            try
            {
                Thread.CurrentThread.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
                using (var dialog = (Form)Activator.CreateInstance(dialogType, All, null, new object[] { 1 }, CultureInfo.InvariantCulture))
                { ((TextBox)Field(dialog, "nameInput")).Text = "French"; ((TextBox)Field(dialog, "velocityInput")).Text = "161,25"; Check((bool)Call(dialog, "TryAccept") && Math.Abs((double)Get(dialog, "Velocity") - 161.25) < 1e-9, "Locale decimal comma accepted without thousands ambiguity"); }
            }
            finally { Thread.CurrentThread.CurrentCulture = culture; }
            Console.WriteLine("CreationMaterialCatalogSmoke PASS " + checks + " QA=" + qa); return 0;
        }
        catch (Exception exception) { Console.Error.WriteLine(exception); return 1; }
    }
}
