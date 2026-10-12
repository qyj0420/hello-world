using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace HVX.Controls
{
    /// <summary>Stable task-20-compatible search and sort projection used by the visual control.</summary>
    public static class ProjectQuery
    {
        public static ProjectRecord[] FilterAndSort(IEnumerable<ProjectRecord> source, string keyword,
            ProjectSortField field, ProjectSortDirection direction)
        {
            IEnumerable<ProjectRecord> query = source ?? Enumerable.Empty<ProjectRecord>();
            string term = (keyword ?? string.Empty).Trim();
            if (term.Length > 0) query = query.Where(p => Matches(p, term));
            IComparer<ProjectRecord> comparer = new RecordComparer(field, direction);
            return query.OrderBy(p => p, comparer).ThenBy(p => p.ProjectPath,
                StringComparer.OrdinalIgnoreCase).ToArray();
        }

        public static bool Matches(ProjectRecord project, string keyword)
        {
            if (project == null) return false;
            string term = (keyword ?? string.Empty).Trim();
            if (term.Length == 0) return true;
            StringComparison comparison = StringComparison.CurrentCultureIgnoreCase;
            if (Contains(project.ProjectName, term, comparison) || Contains(project.StartTerminal, term, comparison) ||
                Contains(project.EndTerminal, term, comparison) || Contains(project.ProjectPath, term, comparison) ||
                Contains(project.DisplayTestTime, term, comparison) || Contains(project.ReportSummary, term, comparison)) return true;
            if (project.InsulationStatus != null &&
                (Contains(project.InsulationStatus.StatusText, term, comparison) ||
                 Contains(project.InsulationStatus.AnalysisName, term, comparison))) return true;
            if (ProjectScanner.TryParseTime(term, out DateTime wanted) && project.HasTestTime && project.TestTime.Date == wanted.Date)
                return true;
            if (!double.IsNaN(project.CableLengthMetres) &&
                project.CableLengthMetres.ToString("0.0###", CultureInfo.InvariantCulture).IndexOf(term,
                    StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return project.Reports.Any(report => Contains(report.RelativePath, term, comparison));
        }

        static bool Contains(string value, string term, StringComparison comparison)
        {
            return (value ?? string.Empty).IndexOf(term, comparison) >= 0;
        }

        sealed class RecordComparer : IComparer<ProjectRecord>
        {
            readonly ProjectSortField field;
            readonly int direction;

            internal RecordComparer(ProjectSortField field, ProjectSortDirection direction)
            {
                this.field = field;
                this.direction = direction == ProjectSortDirection.Ascending ? 1 : -1;
            }

            public int Compare(ProjectRecord left, ProjectRecord right)
            {
                if (ReferenceEquals(left, right)) return 0;
                if (left == null) return 1;
                if (right == null) return -1;
                int compared;
                switch (field)
                {
                    case ProjectSortField.ProjectName:
                        compared = StringComparer.CurrentCultureIgnoreCase.Compare(left.ProjectName, right.ProjectName);
                        break;
                    case ProjectSortField.CableLength:
                        compared = CompareFinite(left.CableLengthMetres, right.CableLengthMetres);
                        break;
                    default:
                        compared = CompareTime(left, right);
                        break;
                }
                if (compared == 0) compared = StringComparer.CurrentCultureIgnoreCase.Compare(left.ProjectName, right.ProjectName);
                return compared * direction;
            }

            static int CompareTime(ProjectRecord left, ProjectRecord right)
            {
                if (left.HasTestTime && right.HasTestTime) return left.TestTime.CompareTo(right.TestTime);
                if (left.HasTestTime != right.HasTestTime) return left.HasTestTime ? 1 : -1;
                return 0;
            }

            static int CompareFinite(double left, double right)
            {
                bool lf = !double.IsNaN(left) && !double.IsInfinity(left);
                bool rf = !double.IsNaN(right) && !double.IsInfinity(right);
                if (lf && rf) return left.CompareTo(right);
                if (lf != rf) return lf ? 1 : -1;
                return 0;
            }
        }
    }
}
