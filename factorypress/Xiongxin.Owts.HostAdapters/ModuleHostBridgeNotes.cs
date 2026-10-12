namespace Xiongxin.Owts.HostAdapters
{
    /// <summary>
    /// WinForms 薄桥接已落地（见 dac-rewrite/shell-bridge/）。
    ///
    /// 挂点（dac-source，可 Revert）：
    /// 1. MainForm 构造：OwtsRewriteBridge.TryAttach() — 反射加载 owts-bridge\HostAdapters
    /// 2. Shift+Help：OwtsRewriteBridge.ShowStatusDialog
    /// 3. ModuleHost 项目加载：OwtsRewriteBridge.NotifyProjectLoaded — ITEM.in 工程镜像到 project.owts.db
    ///
    /// 默认无 owts-bridge 目录时桥接 Inactive，原壳行为不变。永远不通过桥接启用真机。
    /// </summary>
    public static class ModuleHostBridgeNotes
    {
        public const string SuggestedEntry = "ModuleHost / MainForm project-open & analysis navigation";
        public const string SimulationOnly = "CreateSimulation 强制 AllowLiveHardware=false";
        public const string BridgeFolder = "owts-bridge";
        public const string StatusFile = "owts-bridge-status.txt";
    }
}
