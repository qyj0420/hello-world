using System;
using System.IO;
using Xiongxin.Owts.Contracts.Persistence;
using Xiongxin.Owts.Contracts.Project;
using Xiongxin.Owts.Persistence;

namespace Xiongxin.Owts.HostAdapters
{
    /// <summary>
    /// 最小 IProjectWorkspace：优先 SQLite；目录含 ITEM.in+CABLE.in 时只读导入。
    /// </summary>
    public sealed class SqliteProjectWorkspace : IProjectWorkspace
    {
        readonly IProjectRepository _repository;
        ProjectDraftDto _draft = new ProjectDraftDto();
        string _projectPath = string.Empty;

        public event EventHandler Changed;

        public SqliteProjectWorkspace(IProjectRepository repository)
        {
            _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        }

        public string ProjectPath { get { return _projectPath; } }
        public bool IsLoaded { get { return !string.IsNullOrEmpty(_projectPath); } }
        public ProjectDraftDto CurrentDraft { get { return _draft; } }

        public bool TryOpen(string path, out string message)
        {
            message = string.Empty;
            if (string.IsNullOrWhiteSpace(path))
            {
                message = "请选择项目路径。";
                return false;
            }

            string full = Path.GetFullPath(path.Trim());
            try
            {
                if (File.Exists(full) && IsSqliteProjectFile(full))
                {
                    _repository.OpenOrCreate(full);
                    _draft = _repository.LoadProject() ?? new ProjectDraftDto();
                    _projectPath = full;
                    RaiseChanged();
                    message = "已打开 SQLite 工程。";
                    return true;
                }

                if (Directory.Exists(full))
                {
                    string dbBeside = Path.Combine(full, "project.owts.db");
                    string item = Path.Combine(full, "ITEM.in");
                    string cable = Path.Combine(full, "CABLE.in");

                    if (File.Exists(dbBeside))
                    {
                        _repository.OpenOrCreate(dbBeside);
                        _draft = _repository.LoadProject() ?? new ProjectDraftDto();
                        _projectPath = dbBeside;
                        RaiseChanged();
                        message = "已打开目录内 project.owts.db。";
                        return true;
                    }

                    if (File.Exists(item) && File.Exists(cable))
                    {
                        if (!File.Exists(dbBeside))
                            _repository.OpenOrCreate(dbBeside);
                        else
                            _repository.OpenOrCreate(dbBeside);

                        MigrationReport report = _repository.ImportLegacyFolder(full);
                        if (!report.Success)
                        {
                            message = report.Message;
                            return false;
                        }
                        _draft = report.Draft ?? _repository.LoadProject();
                        _projectPath = dbBeside;
                        RaiseChanged();
                        message = "已从 ITEM.in/CABLE.in 只读导入到 project.owts.db。";
                        return true;
                    }

                    message = "目录中既无 project.owts.db，也无 ITEM.in+CABLE.in。";
                    return false;
                }

                // 新建 db / .owts 路径
                if (IsSqliteProjectFile(full) || full.EndsWith(".db", StringComparison.OrdinalIgnoreCase)
                    || full.EndsWith(".owts", StringComparison.OrdinalIgnoreCase))
                {
                    _repository.OpenOrCreate(full);
                    _draft = _repository.LoadProject() ?? new ProjectDraftDto();
                    _projectPath = full;
                    RaiseChanged();
                    message = "已创建/打开 SQLite 工程。";
                    return true;
                }

                message = "无法识别的项目路径。";
                return false;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                return false;
            }
        }

        public bool TryCreate(ProjectDraftDto draft, string directory, out string message)
        {
            message = string.Empty;
            if (draft == null)
            {
                message = "draft 为空。";
                return false;
            }
            if (string.IsNullOrWhiteSpace(directory))
            {
                message = "请指定目录。";
                return false;
            }
            try
            {
                string dir = Path.GetFullPath(directory);
                Directory.CreateDirectory(dir);
                string db = Path.Combine(dir, "project.owts.db");
                _repository.OpenOrCreate(db);
                _repository.UpsertProject(draft);
                _draft = draft;
                _projectPath = db;
                RaiseChanged();
                message = "工程已创建。";
                return true;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                return false;
            }
        }

        public void Close()
        {
            _projectPath = string.Empty;
            _draft = new ProjectDraftDto();
            RaiseChanged();
        }

        public bool TrySaveDraft(ProjectDraftDto draft, out string message)
        {
            message = string.Empty;
            if (!IsLoaded)
            {
                message = "未打开工程。";
                return false;
            }
            try
            {
                _repository.UpsertProject(draft ?? _draft);
                _draft = draft ?? _draft;
                RaiseChanged();
                message = "已保存。";
                return true;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                return false;
            }
        }

        public ProjectPressurePlanDto GetPressurePlan()
        {
            var plan = new ProjectPressurePlanDto();
            int seq = 1;
            foreach (var step in _draft.PressureSteps)
            {
                plan.Rows.Add(new PressurePlanRowDto
                {
                    Sequence = seq++,
                    U0Multiplier = step.Multiplier,
                    RepetitionCount = step.Repetitions,
                    VoltageKv = double.NaN
                });
            }
            return plan;
        }

        static bool IsSqliteProjectFile(string full)
        {
            if (string.IsNullOrEmpty(full)) return false;
            if (full.EndsWith(".db", StringComparison.OrdinalIgnoreCase)) return true;
            if (full.EndsWith(".owts", StringComparison.OrdinalIgnoreCase)) return true;
            return ProjectLibraryCatalog.IsSqliteLibraryFileName(full);
        }

        void RaiseChanged()
        {
            EventHandler handler = Changed;
            if (handler != null) handler(this, EventArgs.Empty);
        }
    }
}
