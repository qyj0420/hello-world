using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Windows.Forms;

internal static class CreationPrecisionTimestampSmoke
{
    const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    static int checks;
    static void Check(bool value, string message) { checks++; if (!value) throw new Exception(message); }
    static object Get(object owner, string name) { var p = owner.GetType().GetProperty(name, All); return p != null ? p.GetValue(owner, null) : owner.GetType().GetField(name, All).GetValue(owner); }
    static void Set(object owner, string name, object value) { owner.GetType().GetProperty(name, All).SetValue(owner, value, null); }
    static object Call(object owner, string name, params object[] args) { var t = owner as Type ?? owner.GetType(); return t.GetMethods(All).Single(m => m.Name == name && m.GetParameters().Length == args.Length).Invoke(owner is Type ? null : owner, args); }
    static string Read(string path) { byte[] bytes = File.ReadAllBytes(path); try { return new UTF8Encoding(false, true).GetString(bytes); } catch { return Encoding.GetEncoding(936).GetString(bytes); } }
    static string Fixture(string qa, string name, string timestamp)
    {
        string path = Path.Combine(qa, name); Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "ITEM.in"), "[ITEM]\r\n始端地点 = start\r\n终端地点 = end\r\n电缆芯数 = 单相\r\n相电压 = 8.7kV\r\n电缆长度 = 400.123\r\nA相电缆长度 = 400.123\r\nB相电缆长度 = 0\r\nC相电缆长度 = 0\r\n硬件采样时间 = " + timestamp + "\r\n天气更新时间 = " + timestamp + "\r\n", new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(path, "CABLE.in"), "0\tA\t\t\r\n1\t人工始端\t冷缩\t0m\r\n2\t本体\tXLPE\t\r\n1\t人工终端\t冷缩\t400.123m\r\n", new UTF8Encoding(true));
        File.WriteAllBytes(Path.Combine(path, "raw.dem"), new byte[] { 5, 3, 1 }); return path;
    }
    [STAThread] static int Main(string[] args)
    {
        var oldCulture = Thread.CurrentThread.CurrentCulture; var oldUi = Thread.CurrentThread.CurrentUICulture;
        try
        {
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            var module = Assembly.LoadFrom(Path.Combine(Path.GetFullPath(args[0]), "modules", "ProjectCreation", "Xiongxin.OWTS.ProjectCreation.dll"));
            string qa = Path.Combine(Path.GetFullPath(args[1]), "precision-" + Guid.NewGuid().ToString("N").Substring(0, 12)); Directory.CreateDirectory(qa);
            var store = module.GetType("HVX.Controls.ProjectCreationStorage", true);
            var reader = store.GetMethod("TryReadSampleTime", All);
            string[] times = { "2026-05-05T08:31:17Z", "2026-05-05T08:31:17.123Z", "2026-05-05T08:31:17.1234567Z", "2026-05-05T16:31:17+08:00", "2026-05-05T06:31:17-02:00", "2026-05-05T08:31:17", "2026-05-05 08:31:17" };
            foreach (string culture in new[] { "zh-CN", "en-US", "fr-FR" })
            {
                Thread.CurrentThread.CurrentCulture = CultureInfo.GetCultureInfo(culture); Thread.CurrentThread.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
                for (int i = 0; i < times.Length; i++)
                {
                    object[] parserArguments = { times[i], DateTime.MinValue }; Check((bool)reader.Invoke(null, parserArguments), "complete ISO/legacy parses " + times[i]);
                    DateTime expected = DateTime.Parse(times[i], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
                    Check(((DateTime)parserArguments[1]).ToBinary() == expected.ToBinary(), "instant/kind preserved under " + culture);
                    string path = Fixture(qa, culture + "-" + i, times[i]);
                    using (var c = (Control)Activator.CreateInstance(module.GetType("HVX.Controls.ProjectCreationControl", true)))
                    {
                        Set(c, "ShowDialogMessages", false); Set(c, "OnlineWeatherEnabled", false); Set(c, "AutoRefreshWeatherOnHardwareSample", false); Set(c, "SettingsPath", Path.Combine(qa, "catalog.ini"));
                        Check((bool)Call(c, "LoadProject", path), "precise measured project loads");
                        Check((double)Get(c, "CableLengthA") == 400.123 && !(bool)Call(c, "GeometryChangedFromLoaded"), "one-decimal presentation does not alter authoritative length or dirty geometry");
                        var length = (NumericUpDown)Get(c, "lengthANumber"); Check(length.DecimalPlaces == 1 && !length.Enabled, "one decimal display and measured length protection retained");
                        Check(((DateTime)Get(c, "HardwareSampleTime")).ToBinary() == expected.ToBinary(), "hardware timestamp loads without loss");
                        byte[] item = File.ReadAllBytes(Path.Combine(path, "ITEM.in"));
                        Check((bool)Call(c, "AddJoint", "A相", "冷缩", "", 80d), "append allowed without changing precise measured length");
                        Check(item.SequenceEqual(File.ReadAllBytes(Path.Combine(path, "ITEM.in"))), "joint append preserves exact original ITEM bytes");
                        Check(Read(Path.Combine(path, "CABLE.in")).Contains("400.123m") && !(bool)Call(c, "GeometryChangedFromLoaded"), "endpoint and refreshed baseline retain precise length");
                        // Simulate another page adding a joint, then use metadata-only refresh.
                        var jointStore = module.GetType("HVX.ProjectCables.CableJointStore", true);
                        var snapshot = Call(jointStore, "Read", path);
                        var plan = Call(snapshot, "Prepare", new[] { "A" }, 120d, "接头 2", "冷缩", "", ""); Call(jointStore, "Commit", plan);
                        Check((bool)Call(c, "RefreshCableStructure") && (double)Get(c, "CableLengthA") == 400.123 && !(bool)Call(c, "GeometryChangedFromLoaded"), "cross-page synchronization retains precise loaded length");
                        Set(c, "Notes", "timestamp precision test"); Check((bool)Call(c, "SaveProject"), "metadata save succeeds with no false geometry edit");
                        Check((bool)Call(c, "LoadProject", path), "saved metadata reloads");
                        Check(((DateTime)Get(c, "HardwareSampleTime")).ToBinary() == expected.ToBinary(), "saved ISO retains exact instant/kind");
                        Check((double)Get(c, "CableLengthA") == 400.123 && File.ReadAllBytes(Path.Combine(path, "raw.dem")).SequenceEqual(new byte[] { 5, 3, 1 }), "precision and raw data preserved");
                    }
                }
                foreach (string invalid in new[] { "prefix 2026-05-05T08:31:17Z", "2026-05-05T08:31:17Z suffix", "2026-05-05T08:31:17.12345678Z", "2026-02-30T08:31:17Z" })
                { object[] a = { invalid, DateTime.MinValue }; Check(!(bool)reader.Invoke(null, a), "partial/invalid ISO is not guessed"); }
            }
            Console.WriteLine("CREATION_PRECISION_TIMESTAMP_PASS=" + checks + "; HARDWARE=False; EVIDENCE=" + qa); return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error is TargetInvocationException ? ((TargetInvocationException)error).InnerException : error); return 1; }
        finally { Thread.CurrentThread.CurrentCulture = oldCulture; Thread.CurrentThread.CurrentUICulture = oldUi; }
    }
}
