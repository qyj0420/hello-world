
using System;

namespace Xiongxin.Owts.Contracts.Project
{
    /// <summary>← ApplicationState + ProjectCreationStorage.Load / ProjectValidator</summary>
    public interface IProjectWorkspace
    {
        event EventHandler Changed;
        string ProjectPath { get; }
        bool IsLoaded { get; }
        ProjectDraftDto CurrentDraft { get; }

        bool TryOpen(string path, out string message);
        bool TryCreate(ProjectDraftDto draft, string directory, out string message);
        void Close();
        bool TrySaveDraft(ProjectDraftDto draft, out string message);
        ProjectPressurePlanDto GetPressurePlan();
    }
}
