using System;
using System.ComponentModel;
using System.Linq;

namespace HVX.Controls
{
    public sealed partial class ProjectManagementControl
    {
        string currentProjectPath = string.Empty;
        ProjectRecord externalCurrentProject;
        bool currentProjectUnavailable;
        string statusBaseText = string.Empty;
        bool statusIsError;

        /// <summary>
        /// The host-confirmed loaded project, not the selected row or a pending
        /// load request. Set empty when the host releases its project context.
        /// </summary>
        [Browsable(false)]
        public string CurrentProjectPath
        {
            get { return currentProjectPath; }
            set
            {
                EnsureUiAccess();
                if (InvokeRequired) { Invoke(new Action<string>(v => CurrentProjectPath = v), value); return; }
                string path = NormalizePathOrEmpty(value);
                if (SameProjectPath(currentProjectPath, path)) return;
                currentProjectPath = path;
                // Do not restore a previously requested/tree-selected project
                // after the actual host context is switched or released.
                preferredProjectPath = string.Empty;
                IncludeCurrentProject();
                RenderGrid();
            }
        }

        static bool SameProjectPath(string left, string right)
        {
            return string.Equals(NormalizePathOrEmpty(left), NormalizePathOrEmpty(right), StringComparison.OrdinalIgnoreCase);
        }

        void IncludeCurrentProject()
        {
            if (externalCurrentProject != null)
            {
                projects = projects.Where(p => !ReferenceEquals(p, externalCurrentProject)).ToArray();
                checkedPaths.Remove(externalCurrentProject.ProjectPath);
                externalCurrentProject = null;
            }
            currentProjectUnavailable = false;
            if (currentProjectPath.Length == 0 || projects.Any(p => SameProjectPath(p.ProjectPath, currentProjectPath))) return;
            try
            {
                // Only read the specifically loaded project. Never broaden a
                // scan to its parent or enumerate unrelated external projects.
                externalCurrentProject = ProjectScanner.ReadProject(currentProjectPath);
                projects = projects.Concat(new[] { externalCurrentProject }).ToArray();
            }
            catch { currentProjectUnavailable = true; }
        }

        string ProjectPathToolTip(ProjectRecord project)
        {
            return (currentProjectPath.Length > 0 && SameProjectPath(project.ProjectPath, currentProjectPath)
                ? T("当前已加载项目（置顶）", "Currently loaded project (pinned)") + Environment.NewLine
                : string.Empty) + project.ProjectPath;
        }

        void RefreshProjectContextStatus()
        {
            string hint = string.Empty;
            if (currentProjectPath.Length > 0)
            {
                ProjectRecord current = projects.FirstOrDefault(p => SameProjectPath(p.ProjectPath, currentProjectPath));
                if (currentProjectUnavailable || current == null)
                    hint = T("当前项目不可读取，请检查路径或刷新。", "The loaded project is unavailable; check its path or refresh.");
                else if (!ProjectQuery.Matches(current, searchBox.Text))
                    hint = T("当前项目被筛选隐藏；清除筛选可显示。", "The loaded project is hidden by the filter; clear the filter to show it.");
            }
            string message = statusTextFactory == null ? statusBaseText : statusTextFactory();
            // Keep failure information first. Otherwise the filter explanation
            // takes priority so the absence of a pinned row is never silent.
            status.Text = hint.Length == 0 ? message : statusIsError && message.Length > 0
                ? message + "  ·  " + hint : hint + (message.Length == 0 ? string.Empty : "  ·  " + message);
            status.AccessibleDescription = status.Text + (hint.Length > 0 ? Environment.NewLine + currentProjectPath : string.Empty);
            status.ForeColor = statusIsError ? ProjectUiTheme.Danger : ProjectUiTheme.Ink;
        }
    }
}
