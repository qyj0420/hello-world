using System.Collections.Generic;

namespace HVX.Controls
{
    internal static class ProjectCreationLocalization
    {
        static readonly Dictionary<string, string> English = new Dictionary<string, string>
        {
            { "任意值：在加压测试中直接设置电压。", "Free value: set the voltage directly in the pressure test." },
            { "项目建立", "Project Creation" },
            { "电缆接头编辑", "Edit cable joints" },
            { "接头列表", "Joint list" },
            { "接头前绝缘", "Insulation before" },
            { "接头后绝缘", "Insulation after joint" },
            { "PROJECT CREATION  ·  LabVIEW 2026", "PROJECT CREATION  ·  LabVIEW 2026" },
            { "PROJECT CREATION  /  LABVIEW 2026", "PROJECT CREATION  /  LABVIEW 2026" },
            { "中文", "Chinese" },
            { "检测信息", "Test Information" },
            { "环境与北斗 · 硬件与联网采集", "Environment & BeiDou" },
            { "环境与北斗 · 硬件采集", "Environment & BeiDou" },
            { "端点详细信息", "Endpoint Details" },
            { "试验配置", "Test Configuration" },
            { "补充资料 · 按需展开", "Optional Details" },
            { "线路与电气参数", "Circuit & Electrical Parameters" },
            { "电缆结构预览", "Cable Topology" },
            { "负载模型", "Load Model" },
            { "负载类型", "Load type" },
            { "补充资料", "Additional Details" },
            { "设备就绪", "Device ready" },
            { "设备离线", "Device offline" },
            { "设备名称", "Device name" },
            { "电容器名称", "Capacitor name" },
            { "设备侧名称", "Device-side name" },
            { "电容侧名称", "Capacitor-side name" },
            { "设备侧", "Device side" },
            { "电容侧", "Capacitor side" },
            { "线路区间", "Section" },
            { "测试连接", "Test link" },
            { "所有相长度 / m", "All-phase length / m" },
            { "A相 / L1 长度 / m", "L1 cable length / m" },
            { "公共长度", "Common length" },
            { "电容模式", "Capacitor mode" },
            { "刷新编号", "Refresh ID" },
            { "当前负载：电容", "Current load: capacitor" },
            { "长度参数不适用", "Length not applicable" },
            { "设备类型", "Device type" },
            { "电容类型", "Capacitor type" },
            { "设备编号", "Device ID" },
            { "电容编号", "Capacitor ID" },
            { "测试回路", "Test circuit" },
            { "电容型号", "Capacitor model" },
            { "电缆回路", "Cable Circuit" },
            { "带 * 的字段为必填项", "Fields marked * are required" },
            { "测试时间（小时）", "Test time (hour)" },
            { "联网天气", "Online weather" },
            { "加压程序", "Voltage program" },
            { "硬件采集", "Hardware acquisition" },
            { "三相单芯允许 A/B/C 长度不同；定位按当前相长度计算。", "Three-phase single-core cables may have different L1/L2/L3 lengths; location uses the active phase length." },
            { "始端名称必须填写", "Start name is required" },
            { "终端名称必须填写", "End name is required" },
            { "电缆长度必须大于 0", "Cable length must be greater than 0" },
            { "请选择相电压", "Select a phase voltage" },
            { "相电压必须是大于 0 的有效数值，单位为 kV。", "Phase voltage must be a valid number greater than 0, in kV." },
            { "项目尚未保存", "Project not saved" },
            { "请完成以下修改后再保存：", "Please correct the following before saving:" },
            { "已填写的内容将保留。关闭提示后，将定位到第一个需要修改的位置。", "Your entries are retained. After closing this message, the first field requiring attention will be selected." },
            { "{0}接头“{1}”的位置必须大于 0 且小于本相长度 {2} m。", "{0} joint “{1}” must be positioned above 0 and below the phase length of {2} m." },
            { "{0}存在重复接头位置。", "{0} contains duplicate joint positions." },
            { "项目已保存：", "Project saved:" },
            { "保存失败", "Save failed" },
            { "请填写所有必填项后再保存", "Complete all required fields before saving" },
            { "相对湿度必须在 0% 至 100% 之间。", "Relative humidity must be between 0% and 100%." },
            { "电容局放模式的三相长度必须全部为 0。", "All phase lengths must be 0 in capacitor PD mode." },
            { "电容局放模式不能包含电缆接头。", "Capacitor PD mode cannot contain cable joints." },
            { "单相模式的 B/C 相长度必须为 0。", "L2/L3 lengths must be 0 in single-phase mode." },
            { "三芯模式使用统一长度；长度不一致时请选择“三相单芯”。", "Three-core mode uses one length; choose three-phase single-core for different lengths." },
            { "线路端点", "Cable Endpoints" },
            { "绝缘记录与备注", "Insulation & Notes" },
            { "电缆与试验参数", "Cable Parameters" },
            { "三相结构预览 · 各相长度独立", "Three-Phase Preview" },
            { "接头与本体结构", "Joints & Cable Body" },
            { "始端", "Start" },
            { "终端", "End" },
            { "本体", "Cable body" },

            { "检测单位", "Org." },
            { "测试人员", "Operator" },
            { "测试地点", "Location" },
            { "测试时间", "Time" },
            { "检测方式", "Mode" },
            { "双端测试（数据文件追加 D）", "Dual-ended test (append D to data file)" },
            { "单位和人员支持直接输入；成功保存后进入历史下拉缓存（最近优先）。", "Organizations and operators may be entered directly; successful saves are cached with recent entries first." },

            { "温度 / ℃", "Temperature / °C" },
            { "湿度 / %", "Humidity / %" },
            { "天气", "Weather" },
            { "天气描述", "Weather description" },
            { "北斗纬度", "BeiDou latitude" },
            { "北斗经度", "BeiDou longitude" },
            { "海拔 / m", "Altitude / m" },
            { "定位状态", "Fix status" },
            { "采样时间", "Sample time" },
            { "数据源", "Data source" },
            { "数据源：硬件", "Source: hardware" },
            { "数据源：硬件 / 手动", "Source: hardware / manual" },
            { "数据源：联网天气", "Source: online weather" },
            { "硬件", "Hardware" },
            { "硬件 / 手动", "Hardware / manual" },
            { "读取硬件", "Read Hardware" },
            { "刷新天气", "Refresh Weather" },
            { "获取中", "Retrieving" },
            { "获取中…", "Retrieving…" },
            { "天气获取中…", "Retrieving weather…" },
            { "展开", "Expand" },
            { "收起", "Collapse" },
            { "展开环境与北斗", "Expand Environment & BeiDou" },
            { "收起环境与北斗", "Collapse Environment & BeiDou" },

            { "始端名称", "Start name" },
            { "始端类型", "Start type" },
            { "始端调度号", "Start ID" },
            { "终端名称", "End name" },
            { "终端类型", "End type" },
            { "终端调度号", "End ID" },
            { "线路名称", "Line name" },
            { "电缆型号", "Model" },

            { "A相绝缘1", "L1 insulation 1" },
            { "A相绝缘2", "L1 insulation 2" },
            { "B相绝缘1", "L2 insulation 1" },
            { "B相绝缘2", "L2 insulation 2" },
            { "C相绝缘1", "L3 insulation 1" },
            { "C相绝缘2", "L3 insulation 2" },
            { "备注", "Notes" },

            { "电缆结构", "Structure" },
            { "相电压", "Voltage" },
            { "A相长度 / m", "L1 length / m" },
            { "B相长度 / m", "L2 length / m" },
            { "C相长度 / m", "L3 length / m" },
            { "铺设时间", "Installed" },
            { "加压模式", "HV mode" },
            { "编辑加压列表", "Edit Voltage Steps" },
            { "任务35汇总长度：0.000 m（取 A/B/C 最大值）", "Task 35 summary length: 0.000 m (maximum of L1/L2/L3)" },
            { "任务35汇总长度：{0} m（取 A/B/C 最大值；定位使用本相长度）", "Task 35 summary length: {0} m (maximum of L1/L2/L3; location uses the selected phase length)" },
            { "任务35汇总长度：", "Task 35 summary: " },
            { " m（取 A/B/C 最大值；定位使用本相长度）", " m (max L1/L2/L3; location uses this phase)" },

            { "相别", "Phase" },
            { "节点", "Node" },
            { "附件类型", "Type" },
            { "本体材料", "Material" },
            { "位置 / m", "Pos. / m" },
            { "添加接头", "Add Joint" },
            { "编辑选中", "Edit Selected" },
            { "删除选中", "Delete Selected" },

            { "清空", "Clear" },
            { "加载项目", "Load Project" },
            { "保存项目", "Save Project" },
            { "退出", "Exit" },
            { "未保存", "Not saved" },
            { "已保存", "Saved" },
            { "有未保存更改", "Unsaved changes" },
            { "就绪 · 请选择电缆结构并填写项目资料", "Ready · Select a cable configuration and enter project information" },
            { "就绪 · 请选择负载类型并填写项目资料", "Ready · Select a load type and enter project information" },
            { "已请求采集温湿度和北斗数据 · 等待 LabVIEW 硬件驱动回填", "Temperature, humidity, and BeiDou acquisition requested · Waiting for the LabVIEW hardware driver" },
            { "硬件数据已更新", "Hardware data updated" },
            { "硬件数据无效", "Invalid hardware data" },
            { "天气数据已更新", "Weather data updated" },
            { "天气获取失败", "Weather retrieval failed" },
            { "等待经纬度", "Waiting for coordinates" },
            { "联网天气已禁用", "Online weather is disabled" },
            { "天气服务地址无效", "Invalid weather service address" },
            { "天气数据已更新 · ", "Weather updated · " },
            { "天气获取失败 · ", "Weather retrieval failed · " },
            { "硬件数据已更新 · 北斗 ", "Hardware updated · BeiDou " },
            { "硬件数据无效 · ", "Invalid hardware data · " },
            { "已保存 · ", "Saved · " },
            { "保存失败 · ", "Save failed · " },
            { "加载失败 · ", "Load failed · " },
            { "已加载 · 修改后保存到原项目", "Loaded · Changes will update this project" },
            { "已清空 · 磁盘上的既有项目未被删除", "Cleared · Existing projects on disk were not deleted" },
            { "已向 LabVIEW 发出退出请求", "Exit request sent to LabVIEW" },
            { "已请求打开任务35项目管理 · 等待 LabVIEW 返回项目路径", "Task 35 project manager requested · Waiting for LabVIEW to return a project path" },

            { "取消", "Cancel" },
            { "确定", "OK" },
            { "保存", "Save" },
            { "恢复默认", "Restore Defaults" },
            { "加压列表编辑", "Voltage Step Editor" },
            { "加压列表编辑 · ", "Voltage Step Editor · " },
            { "每行表示：加压倍数 × 次数。电压值由相电压与倍数计算。", "Each row is a voltage multiplier × repetition count. Voltage is calculated from phase voltage and the multiplier." },
            { "加压倍数", "Voltage multiplier" },
            { "序号", "Step" },
            { "计算电压 / kV", "Calculated voltage / kV" },
            { "个步骤", "steps" },
            { "电压 = 相电压 × 倍数", "Voltage = phase voltage × multiplier" },
            { "另存为", "Save as" },
            { "加载列表", "Load plan" },
            { "另存加压计划", "Save Voltage Plan As" },
            { "加载加压计划", "Load Voltage Plan" },
            { "加压计划", "Voltage Plan" },
            { "加压计划 (*.hvplan)", "Voltage plans (*.hvplan)" },
            { "加压计划已另存", "Voltage plan saved" },
            { "加压计划已加载，请核对计算电压", "Voltage plan loaded; review the calculated voltages" },
            { "无法读取或保存加压计划，请检查文件及文件夹权限。", "The voltage plan could not be read or saved. Check the file and folder permissions." },
            { "加压计划无效：需包含 1–512 行；倍数应为 0–100，次数应为 1–10000 的整数，总次数不超过 1000000。", "Invalid voltage plan: use 1–512 steps, multipliers from 0 to 100, and 1–10000 repetitions per step (1000000 repetitions maximum in total)." },
            { "计划名称应为 1–100 个字符，不能包含控制字符。", "Use a plan name of 1–100 characters without control characters." },
            { "加压计划文件过大。", "The voltage plan file is too large." },
            { "加压计划文件大小无效。", "The voltage plan file size is invalid." },
            { "无法读取加压计划文件，请选择有效的 .hvplan 文件。", "The voltage plan could not be read. Choose a valid .hvplan file." },
            { "不支持此加压计划格式或版本。", "This voltage plan format or version is not supported." },
            { "请选择 .hvplan 加压计划文件。", "Choose a .hvplan voltage plan file." },
            { "次数", "Repetitions" },
            { "数据错误", "Data Error" },
            { "输入无效", "Invalid Input" },
            { "接头数据无效", "Invalid Joint Data" },
            { "几何信息受保护", "Geometry Protected" },
            { "新增材料…", "Add material…" },
            { "删除当前材料", "Delete current material" },
            { "恢复材料默认值", "Restore default materials" },
            { "新增电缆材料", "Add Cable Material" },
            { "格式应为 名称|正数速度。", "Use the format Name|positive velocity." },
            { "请输入 名称|传播速度(m/μs)，例如：塑料绝缘|170", "Enter Name|propagation velocity (m/μs), for example Plastic insulation|170" },
            { "新增", "Add " },
            { "请输入", "Enter " },
            { "删除当前", "Delete current " },
            { "恢复", "Restore " },
            { "默认值", " defaults" },
            { "位置必须位于本相范围内且不能重复。", "The position must be within this phase and must not be duplicated." },
            { "加压倍数必须为数字，次数必须为正整数。", "The multiplier must be numeric and repetitions must be a positive integer." },
            { "请至少保留一条加压记录。", "Keep at least one voltage-step record." }
        };

        static readonly Dictionary<string, string> CanonicalEnglish = new Dictionary<string, string>
        {
            { "三芯", "Three-core" },
            { "单相", "Single-phase" },
            { "三相单芯", "Three-phase single-core" },
            { "电容局放", "Capacitor PD" },
            { "一体三芯", "Integrated three-core" },
            { "单相单芯", "Single-phase single-core" },
            { "一体三芯电缆", "Integrated three-core cable" },
            { "单芯三相电缆", "Three-phase single-core cable" },
            { "单芯电缆", "Single-core cable" },
            { "电容", "Capacitor" },
            { "设备侧", "Device side" },
            { "电容侧", "Capacitor side" },
            { "A相", "L1" },
            { "B相", "L2" },
            { "C相", "L3" },
            { "旧电缆", "Existing cable" },
            { "新电缆", "New cable" },
            { "自定义", "Custom" },
            { "任意值", "Free value" },
            { "户内冷缩", "Indoor cold-shrink" },
            { "户外冷缩", "Outdoor cold-shrink" },
            { "户内热缩", "Indoor heat-shrink" },
            { "户外热缩", "Outdoor heat-shrink" },
            { "套管", "Bushing" },
            { "油终端", "Oil-filled termination" },
            { "塑料绝缘", "Plastic insulation" },
            { "橡胶绝缘", "Rubber insulation" },
            { "油纸绝缘", "Oil-paper insulation" },
            { "始端", "Start" },
            { "终端", "End" },
            { "本体", "Cable body" },
            { "固定解", "Fixed solution" },
            { "浮点解", "Float solution" },
            { "未知", "Unknown" },
            { "未采集", "Not acquired" }
        };

        internal static int NormalizeLanguage(int language)
        {
            ProjectCreationLanguageContract.Validate(language);
            return Xiongxin.OWTS.Localization.UiLanguage.ResolveIndex(language);
        }

        internal static string T(int language, string key)
        {
            if (key == null) return string.Empty;
            int normalized = NormalizeLanguage(language);
            string translated;
            string english = English.TryGetValue(key, out translated) ? translated : key;
            return ProjectCreationLanguageContract.Text(normalized, key, english);
        }

        internal static string TranslateCanonical(int language, string canonical)
        {
            if (canonical == null) return string.Empty;
            int normalized = NormalizeLanguage(language);
            // Chinese uses the familiar phase names. Every other supported
            // language follows the IEC conductor notation requested by the
            // host application so the model and form always agree.
            if (!Xiongxin.OWTS.Localization.UiLanguage.IsChinese(normalized))
            {
                if (canonical == "A相") return "L1";
                if (canonical == "B相") return "L2";
                if (canonical == "C相") return "L3";
            }
            string translated;
            string english = CanonicalEnglish.TryGetValue(canonical, out translated) ? translated : canonical;
            if (canonical.StartsWith("接头_", System.StringComparison.Ordinal))
                return ProjectCreationLanguageContract.Canonical(normalized, "接头", "Joint") + "_" + canonical.Substring(3);
            return ProjectCreationLanguageContract.Canonical(normalized, canonical, english);
        }

        internal static string Weather(int language, int code)
        {
            int normalized = NormalizeLanguage(language);
            string chinese;
            string english;
            switch (code)
            {
                case 0: chinese = "晴朗"; english = "Clear sky"; break;
                case 1: chinese = "大致晴朗"; english = "Mainly clear"; break;
                case 2: chinese = "局部多云"; english = "Partly cloudy"; break;
                case 3: chinese = "阴"; english = "Overcast"; break;
                case 45: chinese = "雾"; english = "Fog"; break;
                case 48: chinese = "雾凇"; english = "Depositing rime fog"; break;
                case 51: chinese = "小毛毛雨"; english = "Light drizzle"; break;
                case 53: chinese = "中度毛毛雨"; english = "Moderate drizzle"; break;
                case 55: chinese = "强毛毛雨"; english = "Dense drizzle"; break;
                case 56: chinese = "小冻毛毛雨"; english = "Light freezing drizzle"; break;
                case 57: chinese = "强冻毛毛雨"; english = "Dense freezing drizzle"; break;
                case 61: chinese = "小雨"; english = "Slight rain"; break;
                case 63: chinese = "中雨"; english = "Moderate rain"; break;
                case 65: chinese = "大雨"; english = "Heavy rain"; break;
                case 66: chinese = "小冻雨"; english = "Light freezing rain"; break;
                case 67: chinese = "强冻雨"; english = "Heavy freezing rain"; break;
                case 71: chinese = "小雪"; english = "Slight snow"; break;
                case 73: chinese = "中雪"; english = "Moderate snow"; break;
                case 75: chinese = "大雪"; english = "Heavy snow"; break;
                case 77: chinese = "米雪"; english = "Snow grains"; break;
                case 80: chinese = "小阵雨"; english = "Slight rain showers"; break;
                case 81: chinese = "中阵雨"; english = "Moderate rain showers"; break;
                case 82: chinese = "强阵雨"; english = "Violent rain showers"; break;
                case 85: chinese = "小阵雪"; english = "Slight snow showers"; break;
                case 86: chinese = "强阵雪"; english = "Heavy snow showers"; break;
                case 95: chinese = "雷暴"; english = "Thunderstorm"; break;
                case 96: chinese = "雷暴伴小冰雹"; english = "Thunderstorm with slight hail"; break;
                case 99: chinese = "雷暴伴大冰雹"; english = "Thunderstorm with heavy hail"; break;
                default: chinese = "未知"; english = "Unknown"; break;
            }
            return ProjectCreationLanguageContract.Text(normalized, chinese, english);
        }
    }
}
