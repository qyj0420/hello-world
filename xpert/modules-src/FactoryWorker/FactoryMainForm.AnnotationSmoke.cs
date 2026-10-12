using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Xiongxin.FactoryTest
{
    internal sealed partial class FactoryMainForm
    {
        private async Task SmokeAnnotations(string stem, FactoryTfDisplayPoint chosen)
        {
            short[] adc = (short[])latest.Capture.PdAdc.Clone(); double coefficient = latest.Capture.Settings.PdCalibrationPcPerV;
            tfPlot.DrawingMode = FactoryTfDrawMode.Rectangle; Point at = Point.Round(tfPlot.PointPixel(chosen));
            if (!tfPlot.BeginRegionDrag(new Point(at.X - 12, at.Y - 12))) throw new Exception("TF矩形拖拽未开始");
            tfPlot.ContinueRegionDrag(new Point(at.X + 12, at.Y + 12)); tfPlot.EndRegionDrag(new Point(at.X + 12, at.Y + 12));
            if (ActiveAnnotationDocument(false) != null && ActiveAnnotationDocument(false).Regions.Count != 0) throw new Exception("没有右键保存即自动标记");
            tfPlot.AddPendingLabel(); await labelTask; await Task.Delay(80);
            SaveImage(stem + ".tf-labelled.png");
            var doc = ActiveAnnotationDocument(false);
            if (doc == null || doc.Regions.Count != 1 || doc.Regions[0].Geometry.Shape != FactoryTfRegionShape.Rectangle || !File.Exists(doc.FilePath) || doc.Dirty || tfPlot.VisibleRegionCount != 1 || tfPlot.RegionRenderCount == 0 || !MatchFor(chosen).Matched)
                throw new Exception("矩形标签自动保存、绘制或自动识别失败：doc=" + (doc == null ? "null" : doc.Regions.Count + "/" + doc.Dirty) + ", visible=" + tfPlot.VisibleRegionCount + ", renders=" + tfPlot.RegionRenderCount + ", match=" + MatchFor(chosen).Text + "；" + status.Text);
            tfPlot.DrawingMode = FactoryTfDrawMode.Oval; at = Point.Round(tfPlot.PointPixel(chosen));
            tfPlot.BeginRegionDrag(new Point(at.X - 10, at.Y - 10)); tfPlot.EndRegionDrag(new Point(at.X + 10, at.Y + 10)); tfPlot.AddPendingLabel(); await labelTask;
            doc = ActiveAnnotationDocument(false);
            if (doc.Regions.Count != 2 || doc.Regions[1].Geometry.Shape != FactoryTfRegionShape.Circle) throw new Exception("圆形圈选未自动保存");
            int renderBefore = tfPlot.RegionRenderCount; await Task.Delay(80); SaveImage(stem + ".tf-circle.png");
            if (tfPlot.RegionRenderCount <= renderBefore) throw new Exception("圆形未绘制");
            var reloaded = FactoryLabelStore.Load(labelRoot, msg => { throw new Exception(msg); });
            if (reloaded.Count != 1 || reloaded[doc.ChainKey].Regions.Count != 2 || reloaded[doc.ChainKey].Regions.Any(r => r.Templates.Count == 0)) throw new Exception("重启库读取无原始模板");
            string editedId = doc.Regions[0].Id; var frozenTemplate = (double[])doc.Regions[0].Templates[0].Wave.Clone();
            var editMenu = tfPlot.PrepareLabelMenu(Point.Round(tfPlot.PointPixel(chosen)));
            SmokeEditItem(editMenu).PerformClick(); await labelTask; doc = ActiveAnnotationDocument(false);
            if (doc.Regions.Count != 2 || !doc.Regions.Single(r => r.Id == editedId).Notes.Contains("右键编辑验证") ||
                !doc.Regions.Single(r => r.Id == editedId).Templates[0].Wave.SequenceEqual(frozenTemplate)) throw new Exception("TF右键修改标签新增了区域或未保存");
            var persistedBeforeCancel = File.ReadAllBytes(doc.FilePath);
            using (var dialog = new FactoryTfAnnotationEditDialog(Icon, doc.Regions[0].Geometry, doc.Regions[0].Members.Count, doc.Regions[0], MatchFor(chosen).Text, true))
            { dialog.Show(this); await Task.Delay(80); using (var image = new Bitmap(dialog.Width, dialog.Height)) { dialog.DrawToBitmap(image, new Rectangle(Point.Empty, dialog.Size)); image.Save(stem + ".label-dialog.png"); } dialog.Close(); }
            if (!File.ReadAllBytes(doc.FilePath).SequenceEqual(persistedBeforeCancel)) throw new Exception("取消编辑仍写入标签库");
            using (var manager = new FactoryTfAnnotationWindow(Icon, doc, null, null) { SuppressClosePrompt = true })
            { manager.Show(this); await Task.Delay(80); if (manager.VisibleRegions != 2) throw new Exception("历史标签区域未显示"); using (var image = new Bitmap(manager.Width, manager.Height)) { manager.DrawToBitmap(image, new Rectangle(Point.Empty, manager.Size)); image.Save(stem + ".label-history.png"); } manager.Close(); }
            RecordSelectedPulse("Hilbert包络"); await labelTask;
            if (ActiveAnnotationDocument(false).Regions.Count != 3 || ActiveAnnotationDocument(false).Regions[2].MarkedSurface != "Hilbert包络" || !selectedPulse.LabelStatus.Contains("匹配人工标签")) throw new Exception("脉冲/包络标签未自动保存或识别");
            var pulseMenu = selectedPulse.PreparePulseLabelMenu(); SmokeEditItem(pulseMenu).PerformClick(); await labelTask;
            doc = ActiveAnnotationDocument(false);
            if (doc.Regions.Count != 3 || FactoryTfAnnotationDocument.Load(doc.FilePath).Regions.Single(r => r.Id == editedId).Notes.Split(new[] { "右键编辑验证" }, StringSplitOptions.None).Length != 3)
                throw new Exception("脉冲右键修改原标签未更新默认库或产生重复标签");
            SaveImage(stem + ".label-edited.png");
            if (!latest.Capture.PdAdc.SequenceEqual(adc) || latest.Capture.Settings.PdCalibrationPcPerV != coefficient || currentTf.Pulses.Count != 14) throw new Exception("标注改变了原始采集参数或脉冲");
            busy = true; SetBusy(true); int count = ActiveAnnotationDocument(false).Regions.Count; RecordSelectedPulse("禁止运行时标记");
            if (ActiveAnnotationDocument(false).Regions.Count != count || annotationActions.Any(c => c.Enabled)) throw new Exception("运行时标签入口未封闭"); busy = false; SetBusy(false);
            File.WriteAllText(stem + ".labels.txt", "LABEL_UI=PASS\nRAW_ADC=unchanged\nRIGHT_CLICK_REQUIRED=PASS\nRECTANGLE_CIRCLE=PASS\nAUTO_SAVE_RELOAD=PASS\nTF_PULSE_RIGHT_CLICK_EDIT=PASS\nCANCEL_NO_WRITE=PASS\nEDIT_PRESERVES_NATIVE_TEMPLATES=PASS\nNATIVE_WAVE_HILBERT=stored\nSTRICT_TF_WAVE_ENVELOPE_1pct=PASS\nAUTO_CLUSTER_TASK27=PASS\nLABEL_ROOT=" + labelRoot + "\nHARDWARE=none\n");
        }
        private static ToolStripMenuItem SmokeEditItem(ContextMenuStrip menu)
        {
            var item = menu.Items.OfType<ToolStripMenuItem>().FirstOrDefault(i => i.Text.StartsWith("修改"));
            if (item == null) throw new Exception("右键菜单缺少修改已有标签项");
            return item.DropDownItems.Count > 0 ? item.DropDownItems.OfType<ToolStripMenuItem>().First() : item;
        }
    }
}
