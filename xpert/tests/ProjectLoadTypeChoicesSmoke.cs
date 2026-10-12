using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Windows.Forms;

// Synthetic projects and managed controls only. No host app, discovery, SDK,
// weather request, acquisition or hardware command is invoked.
internal static class ProjectLoadTypeChoicesSmoke
{
    const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    static readonly string[] Modes = { "三芯", "三相单芯", "单相", "电容局放" };
    static readonly string[] Labels = { "一体三芯电缆", "单芯三相电缆", "单芯电缆", "电容" };
    static readonly string[] EnglishLabels = { "Integrated three-core cable", "Three-phase single-core cable", "Single-core cable", "Capacitor" };
    static readonly string[] TraditionalLabels = { "一體三芯電纜", "單芯三相電纜", "單芯電纜", "電容" };
    static Type pageType, modeType, localizationType, measurementType, cableStoreType;
    static int checks;
    static void Check(bool condition, string message) { checks++; if (!condition) throw new InvalidOperationException(message); }
    static object Field(object owner, string name) { return owner.GetType().GetField(name, All).GetValue(owner); }
    static object Get(object owner, string name) { return owner.GetType().GetProperty(name, All).GetValue(owner, null); }
    static void Set(object owner, string name, object value) { owner.GetType().GetProperty(name, All).SetValue(owner, value, null); }
    static object Call(object owner, string name, params object[] args)
    {
        Type type = owner as Type ?? owner.GetType();
        return type.GetMethods(All).Single(m => m.Name == name && m.GetParameters().Length == args.Length).Invoke(owner is Type ? null : owner, args);
    }
    static string ChoiceValue(object item) { return (string)item.GetType().GetField("Value", All).GetValue(item); }
    static bool Refused(Action action)
    {
        try { action(); return false; }
        catch (TargetInvocationException error) { return error.InnerException is InvalidDataException || error.InnerException is IOException; }
    }
    static Control Page(string qa)
    {
        var page = (Control)Activator.CreateInstance(pageType);
        Set(page, "ShowDialogMessages", false); Set(page, "OnlineWeatherEnabled", false);
        Set(page, "AutoRefreshWeatherOnHardwareSample", false);
        Set(page, "SettingsPath", Path.Combine(qa, "refs-" + Guid.NewGuid().ToString("N") + ".ini"));
        return page;
    }
    static void CheckChoices(Control page, int language, string mode)
    {
        Set(page, "LanguageIndex", language);
        var combo = (ComboBox)Field(page, "cableModeCombo");
        Check(combo.Items.Count == 4, "exactly four load choices: " + language);
        Check(combo.Items.Cast<object>().Select(ChoiceValue).SequenceEqual(Modes), "stable canonical order: " + language);
        Check((string)Get(page, "CableMode") == mode && ChoiceValue(combo.SelectedItem) == mode, "language keeps selection: " + language);
        for (int i = 0; i < Labels.Length; i++)
        {
            string expected = (string)Call(localizationType, "TranslateCanonical", language, Labels[i]);
            Check(!String.IsNullOrWhiteSpace(expected) && combo.Items[i].ToString() == expected, "translated display matches canonical item: " + language + "/" + i);
            if (language == 0) Check(expected == Labels[i], "exact Chinese load label: " + i);
            else if (language == 1) Check(expected == EnglishLabels[i], "exact English load label: " + i);
            else if (language == 18) Check(expected == TraditionalLabels[i], "exact Traditional load label: " + i);
            else if (language >= 2) Check(expected != Labels[i], "numbered language has load vocabulary: " + language + "/" + i);
        }
    }
    static string Fixture(string qa, int index)
    {
        string root = Path.Combine(qa, "existing-" + index); Directory.CreateDirectory(root);
        double a = index == 3 ? 0 : 400, b = index == 0 ? 400 : index == 1 ? 500 : 0, c = index == 0 ? 400 : index == 1 ? 600 : 0;
        File.WriteAllText(Path.Combine(root, "ITEM.in"), "[ITEM]\r\n所属单位 = Synthetic Org\r\n始端地点 = 客户始端\r\n终端地点 = 客户终端\r\n调度号2 = CAP-TEST\r\n电缆芯数 = " + Modes[index] +
            "\r\n相电压 = 8.7kV\r\nA相电缆长度 = " + a + "\r\nB相电缆长度 = " + b + "\r\nC相电缆长度 = " + c + "\r\n备注 = 客户原文\r\n", new UTF8Encoding(false));
        var cable = new StringBuilder();
        string[] phases = index == 1 ? new[] { "A相", "B相", "C相" } : index == 2 ? new[] { "A相" } : new[] { "ALL" };
        foreach (string phase in phases)
        {
            double length = phase == "B相" ? b : phase == "C相" ? c : a;
            cable.Append("0\t").Append(phase).Append("\t\t\r\n1\t始端\t户内冷缩\t0m\r\n2\t本体\t塑料绝缘\t\r\n");
            if (index != 3) cable.Append("1\t客户接头_").Append(phase).Append("\t户内冷缩\t200m\r\n2\t本体\t塑料绝缘\t\r\n");
            cable.Append("1\t终端\t户内冷缩\t").Append(length).Append("m\r\n");
        }
        File.WriteAllText(Path.Combine(root, "CABLE.in"), cable.ToString(), new UTF8Encoding(false));
        return root;
    }
    static void Aliases(string qa)
    {
        string[][] aliases = {
            new[] { "三芯", "一体三芯", "一体三芯电缆", "一體三芯電纜", "Three-core", "Integrated three-core", "Integrated three-core cable" },
            new[] { "三相单芯", "单芯", "三相单芯电缆", "单芯三相电缆", "單芯三相電纜", "Three-phase single-core", "Three-phase single-core cable" },
            new[] { "单相", "单相单芯", "单芯电缆", "單芯電纜", "Single-phase", "Single-phase single-core", "Single-core cable" },
            new[] { "电容局放", "电容", "电容器", "Capacitor PD", "Capacitor" }
        };
        using (Control page = Page(qa))
        for (int i = 0; i < aliases.Length; i++)
        foreach (string alias in aliases[i])
        {
            Check((string)Call(modeType, "Normalize", alias) == Modes[i], "compatible alias: " + alias);
            Set(page, "LoadType", alias);
            Check((string)Get(page, "CableMode") == Modes[i] && (string)Get(page, "LoadType") == Labels[i], "public setter keeps protocol and new display: " + alias);
        }
        string conflicts = Path.Combine(qa, "conflicts"); Directory.CreateDirectory(conflicts);
        for (int i = 0; i < 3; i++)
        foreach (string alias in aliases[i])
        {
            string path = Path.Combine(conflicts, "ITEM.in");
            File.WriteAllText(path, "负载类型 = " + alias + "\r\n", new UTF8Encoding(false));
            Check(!(bool)Call(measurementType, "IsCapacitor", conflicts), "cable alias is explicit cable: " + alias);
            File.WriteAllText(path, "负载类型 = 电容\r\n电缆芯数 = " + alias + "\r\n", new UTF8Encoding(false));
            bool refused = false;
            try { Call(measurementType, "IsCapacitor", conflicts); }
            catch (TargetInvocationException error) { refused = error.InnerException is InvalidDataException; }
            Check(refused, "capacitor/cable contradiction rejected: " + alias);
        }
    }
    static void StrictZeroGeometry(string qa)
    {
        string cap = Path.Combine(qa, "existing-3");
        object snapshot = Call(cableStoreType, "Read", cap);
        byte[] capCable = File.ReadAllBytes(Path.Combine(cap, "CABLE.in"));
        foreach (double position in new[] { 0d, .001, 1d })
            Check(Refused(() => Call(snapshot, "Prepare", new[] { "ALL" }, position, "New joint", "户内冷缩", "塑料绝缘", "塑料绝缘")), "zero-length capacitor cannot add a joint: " + position);
        Check(File.ReadAllBytes(Path.Combine(cap, "CABLE.in")).SequenceEqual(capCable), "rejected capacitor joint requests do not write structure");
        string[] invalidTypes = { "三芯", "三相单芯", "单相", "单芯", "一体三芯电缆", "单芯三相电缆", "单芯电缆", "" };
        for (int i = 0; i < invalidTypes.Length + 4; i++)
        {
            string root = Path.Combine(qa, "invalid-geometry-" + i); Directory.CreateDirectory(root);
            string item = "[ITEM]\r\n始端地点 = Start\r\n终端地点 = End\r\n相电压 = 8.7kV\r\n电缆芯数 = " + (i < invalidTypes.Length ? invalidTypes[i] : "电容局放") + "\r\nA相电缆长度 = 0\r\n";
            string cable = Encoding.UTF8.GetString(capCable);
            // The canonical fixture is UTF-8 until a metadata save; its exact
            // cable bytes stay untouched by the save transaction.
            if (i == invalidTypes.Length) item += "负载类型 = 单芯电缆\r\n";
            if (i == invalidTypes.Length + 1) cable += "2\t本体\t塑料绝缘\t\r\n1\t接头\t户内冷缩\t0m\r\n";
            if (i == invalidTypes.Length + 2) cable += "2\t本体\t塑料绝缘\t\r\n1\t接头\t户内冷缩\t1m\r\n";
            if (i == invalidTypes.Length + 3) cable = cable.Replace("2\t本体\t塑料绝缘\t\r\n", "");
            string itemPath = Path.Combine(root, "ITEM.in"), cablePath = Path.Combine(root, "CABLE.in");
            File.WriteAllText(itemPath, item, new UTF8Encoding(false)); File.WriteAllText(cablePath, cable, new UTF8Encoding(false));
            byte[] beforeItem = File.ReadAllBytes(itemPath), beforeCable = File.ReadAllBytes(cablePath);
            Check(Refused(() => Call(cableStoreType, "Read", root)), "unverified cable overlap or invalid capacitor structure rejected: " + i);
            using (Control page = Page(qa))
            {
                bool loaded = (bool)Call(page, "LoadProject", root);
                if (loaded) { Set(page, "Notes", "Must not commit"); Check(!(bool)Call(page, "SaveProject"), "invalid geometry cannot be committed: " + i); }
                else Check(true, "invalid project already refused by loading: " + i);
            }
            Check(File.ReadAllBytes(itemPath).SequenceEqual(beforeItem) && File.ReadAllBytes(cablePath).SequenceEqual(beforeCable), "bad-file load/save leaves both original files unchanged: " + i);
        }
    }
    [STAThread] static int Main(string[] args)
    {
        CultureInfo culture = Thread.CurrentThread.CurrentUICulture;
        try
        {
            if (args.Length != 2) throw new ArgumentException("Pass independent build root and QA output directory.");
            string qa = Path.Combine(Path.GetFullPath(args[1]), "load-types-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(qa);
            Assembly module = Assembly.LoadFrom(Path.Combine(Path.GetFullPath(args[0]), "modules", "ProjectCreation", "Xiongxin.OWTS.ProjectCreation.dll"));
            pageType = module.GetType("HVX.Controls.ProjectCreationControl", true); modeType = module.GetType("HVX.Controls.ProjectModes", true);
            localizationType = module.GetType("HVX.Controls.ProjectCreationLocalization", true); measurementType = module.GetType("HVX.ProjectModel.ProjectMeasurementMode", true);
            cableStoreType = module.GetType("HVX.ProjectCables.CableJointStore", true);
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            Thread.CurrentThread.CurrentUICulture = CultureInfo.GetCultureInfo("zh-CN");
            Check(((string[])modeType.GetField("All", All).GetValue(null)).SequenceEqual(Modes), "canonical list order is unchanged in meaning");
            using (Control page = Page(qa))
            {
                Check((string)Get(page, "CableMode") == Modes[0], "new control defaults to integrated three-core");
                CheckChoices(page, 0, Modes[0]); Set(page, "CableMode", Modes[1]); Call(page, "ClearProject");
                Check((string)Get(page, "CableMode") == Modes[0], "cleared/new draft defaults to integrated three-core");
            }
            Aliases(qa);
            for (int i = 0; i < Modes.Length; i++)
            {
                string root = Fixture(qa, i), cablePath = Path.Combine(root, "CABLE.in"), itemPath = Path.Combine(root, "ITEM.in");
                byte[] cable = File.ReadAllBytes(cablePath), item = File.ReadAllBytes(itemPath);
                using (Control page = Page(qa))
                {
                    Check((bool)Call(page, "LoadProject", root), "load existing canonical project: " + Modes[i] + ": " + Get(page, "LastError"));
                    double a = (double)Get(page, "CableLengthA"), b = (double)Get(page, "CableLengthB"), c = (double)Get(page, "CableLengthC");
                    for (int language = -1; language < 19; language++) CheckChoices(page, language, Modes[i]);
                    Check((double)Get(page, "CableLengthA") == a && (double)Get(page, "CableLengthB") == b && (double)Get(page, "CableLengthC") == c, "language retains per-phase geometry: " + Modes[i]);
                    Check(File.ReadAllBytes(cablePath).SequenceEqual(cable) && File.ReadAllBytes(itemPath).SequenceEqual(item), "loading/display never changes project files: " + Modes[i]);
                    Set(page, "Notes", "客户原文 / offline roundtrip");
                    Check((bool)Call(page, "SaveProject"), "save existing canonical project: " + Modes[i] + ": " + Get(page, "LastError"));
                    Check(File.ReadAllBytes(cablePath).SequenceEqual(cable), "metadata save retains phases, sections and customer joints: " + Modes[i]);
                    string saved = File.ReadAllText(itemPath, Encoding.GetEncoding(936));
                    string typeLine = saved.Replace("\r", "").Split('\n').Single(line => line.Contains("=") && line.Substring(0, line.IndexOf('=')).Trim().TrimEnd(':', '：').Trim() == "电缆芯数");
                    Check(typeLine.Substring(typeLine.IndexOf('=') + 1).Trim().Trim('"') == Modes[i], "save retains old protocol type: " + Modes[i]);
                    Check((bool)Call(page, "LoadProject", root) && (string)Get(page, "CableMode") == Modes[i], "roundtrip keeps existing selection: " + Modes[i]);
                }
                if (i == 3)
                {
                    string rawPath = Path.Combine(root, "synthetic.dem"); byte[] raw = { 1, 2, 3, 4 }; File.WriteAllBytes(rawPath, raw);
                    using (Control page = Page(qa))
                    {
                        Check((bool)Call(page, "LoadProject", root) && (bool)Get(page, "HasExistingTestData"), "load capacitor with synthetic acquired-data marker");
                        Check(!(bool)Get(page, "CanAppendMeasuredJoint"), "measured capacitor has no joint append action");
                        Set(page, "Notes", "Measured capacitor metadata only");
                        Check((bool)Call(page, "SaveProject"), "measured capacitor metadata saves without cable geometry: " + Get(page, "LastError"));
                        Check(File.ReadAllBytes(cablePath).SequenceEqual(cable) && File.ReadAllBytes(rawPath).SequenceEqual(raw), "measured capacitor save keeps structure and raw marker unchanged");
                        Check((bool)Call(page, "LoadProject", root) && (string)Get(page, "CableMode") == Modes[i], "measured capacitor metadata roundtrip preserves type");
                    }
                }
            }
            StrictZeroGeometry(qa);
            Console.WriteLine("PROJECT_LOAD_TYPE_CHOICES_PASS=" + checks + "; HARDWARE_COMMANDS=0; NETWORK_REQUESTS=0; QA=" + qa); return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        finally { Thread.CurrentThread.CurrentUICulture = culture; }
    }
}
