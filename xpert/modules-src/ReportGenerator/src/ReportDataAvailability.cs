using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using HVX.Controls;

namespace HVX.ReportGenerator
{
    public sealed partial class ReportGeneratorControl
    {
        static bool IsNodeAvailable(TreeNode node)
        {
            var model = node == null ? null : node.Tag as ReportTreeNodeModel;
            return model != null && model.IsAvailable;
        }

        bool ApplyAvailability(TreeNode node)
        {
            var model = node.Tag as ReportTreeNodeModel;
            bool available = false;
            foreach (TreeNode child in node.Nodes) available = ApplyAvailability(child) || available;
            if (node.Nodes.Count == 0) available = IsSelectionAvailable(model);
            if (model != null) model.IsAvailable = available;
            if (!available) node.Checked = false;
            else if (node.Nodes.Count > 0) node.Checked = node.Nodes.Cast<TreeNode>().Any(child => child.Checked);
            node.ForeColor = available ? ReportTheme.Ink : Color.FromArgb(153, 163, 171);
            if (!available) node.ToolTipText = UnavailableSelectionText;
            return available;
        }

        string UnavailableSelectionText
        {
            get { return ReportLanguages.Ui(languageIndex, "此选择没有可验证的有效数据，不能加入报告。", "No verifiable data are available for this selection; it cannot be included in the report."); }
        }

        // Also used at generation time: UI state is never trusted as a data-validity check.
        bool IsSelectionAvailable(ReportTreeNodeModel model)
        {
            if (model == null) return false;
            if (model.Kind == ReportTreeNodeKind.ProjectInformation) return true;
            var location = model.Tag as LocationSelection;
            if (location != null)
                return projectData != null && LocationChartRenderer.HasData(locationMatrix, location.Interval,
                    location.Phase, projectData.CableLengthM, projectData.DeviceU0Kv);
            var trend = model.Tag as TrendSelection;
            if (trend != null) return TrendStatisticsChartRenderer.HasData(locationMatrix, projectPath, trend);
            var waveform = model.Tag as BrowserRecord;
            if (waveform != null)
                return waveform.IsValid && waveform.SampleCount > 0 && File.Exists(waveform.Path);
            var attachment = model.Tag as ReportImageInfo;
            if (attachment == null || attachment.IsHistoricalOrStale || attachment.AcquisitionReferenceOnly) return false;
            string kind = attachment.ChartKind ?? "";
            if (kind.IndexOf("PDmap", StringComparison.OrdinalIgnoreCase) >= 0)
                return PdmapChartRenderer.HasData(attachment, projectData);
            if ((attachment.Title ?? "").IndexOf("PRPD", StringComparison.OrdinalIgnoreCase) >= 0 ||
                kind.IndexOf("PRPD", StringComparison.OrdinalIgnoreCase) >= 0)
                return PrpdChartRenderer.HasData(Path.ChangeExtension(attachment.ImagePath, ".csv"));
            if (kind.IndexOf("Spectrum", StringComparison.OrdinalIgnoreCase) >= 0 || !string.IsNullOrWhiteSpace(attachment.RegionSummary))
                return RegionStatisticsChartRenderer.HasData(attachment, projectPath);
            // A legacy image alone has no source-data validity metadata. Do not present
            // axes or a screenshot as verified measurement data.
            return false;
        }
    }
}
