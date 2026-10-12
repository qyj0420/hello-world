using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;
using Xiongxin.OWTS.Localization;

namespace HVX.Controls
{
    public partial class ProjectCreationControl
    {
        readonly List<ContextMenuStrip> retiredReferenceMenus = new List<ContextMenuStrip>();

        string MaterialText(string chinese, string english) { return UiLanguage.Text(languageIndex, chinese, english); }

        void RefreshSharedMaterialChoices()
        {
            // Refresh the complete reference snapshot before accepting later
            // writes, not only its hash, so external settings are not overwritten.
            ReferenceCatalog latest = ReferenceCatalog.Load(settingsPath);
            if (latest.RecoveryRequired) { SetStatus(MaterialText("材料字典读取失败：", "Material catalog could not be read: ") + latest.LoadWarning, false); return; }
            // Existing reference menus capture these list identities. Refresh
            // their contents in place so an unrelated menu cannot edit an orphan.
            catalog.TerminalTypes.Clear(); catalog.TerminalTypes.AddRange(latest.TerminalTypes);
            catalog.AttachmentTypes.Clear(); catalog.AttachmentTypes.AddRange(latest.AttachmentTypes);
            catalog.PhaseVoltages.Clear(); catalog.PhaseVoltages.AddRange(latest.PhaseVoltages);
            catalog.Organizations.Clear(); catalog.Organizations.AddRange(latest.Organizations);
            catalog.Operators.Clear(); catalog.Operators.AddRange(latest.Operators);
            catalog.Materials.Clear(); catalog.Materials.AddRange(latest.Materials.Select(item => item.Clone()));
            catalog.PressurePresets.Clear(); foreach (var entry in latest.PressurePresets) catalog.PressurePresets[entry.Key] = entry.Value.Select(item => item.Clone()).ToList();
            catalog.AcceptPersistenceFrom(latest);
            bool previous = suppressChanges; suppressChanges = true;
            try { foreach (ComboBox box in new[] { beforeBodyTypeCombo, bodyTypeCombo }) { string value = GetChoiceValue(box); FillMaterialChoice(box, value); } }
            finally { suppressChanges = previous; }
        }

        void AttachMaterialCatalogMenu()
        {
            ContextMenuStrip menu = new ContextMenuStrip();
            menu.Items.Add(L("新增材料…"), null, delegate
            {
                ComboBox source = menu.SourceControl as ComboBox ?? bodyTypeCombo;
                using (MaterialEditorDialog dialog = new MaterialEditorDialog(languageIndex))
                {
                    if (dialog.ShowDialog(this) != DialogResult.OK) return;
                    bool replace = catalog.Materials.Any(item => string.Equals(item.Name, dialog.MaterialName, StringComparison.OrdinalIgnoreCase));
                    if (replace && MessageBox.Show(this,
                        MaterialText("材料名称已存在。是否更新此材料的建议波速？\r\n不会修改已保存项目的波速。", "This material already exists. Update its suggested velocity?\r\nSaved project velocities will not change."),
                        L("新增电缆材料"), MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
                    TrySaveMaterial(dialog.MaterialName, dialog.Velocity, replace, source);
                }
            });
            menu.Items.Add(L("删除当前材料"), null, delegate
            {
                ComboBox source = menu.SourceControl as ComboBox ?? bodyTypeCombo;
                string current = GetChoiceValue(source);
                if (ShowDialogMessages && MessageBox.Show(this,
                    MaterialText("从材料字典删除“", "Remove material \"") + current +
                    MaterialText("”？\r\n已有项目中的材料记录不会删除。", "\" from the catalog?\r\nExisting project material records will be retained."),
                    L("删除当前材料"), MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
                TryDeleteMaterial(current);
            });
            menu.Items.Add(L("恢复材料默认值"), null, delegate
            {
                bool recover = catalog.RecoveryRequired;
                if (ShowDialogMessages && MessageBox.Show(this,
                    recover ? MaterialText("材料列表无法正常读取。恢复默认材料并保留备份？已有项目不变。", "The material list could not be read. Restore default materials and keep a backup? Existing projects are unchanged.") :
                    MaterialText("恢复材料字典默认值？已有项目中的材料记录不会改变。", "Restore the default material catalog? Existing project material records will not change."),
                    L("恢复材料默认值"), MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
                ReferenceCatalog candidate = catalog.Clone();
                candidate.Materials.Clear();
                candidate.Materials.AddRange(ReferenceCatalog.CreateDefaults().Materials.Select(item => item.Clone()));
                CommitMaterialCatalog(candidate, recover);
            });
            bodyTypeCombo.ContextMenuStrip = menu;
            if (beforeBodyTypeCombo != null) beforeBodyTypeCombo.ContextMenuStrip = menu;
        }

        bool TrySaveMaterial(string name, double velocity, bool replaceExisting, ComboBox source)
        {
            bool containsControl = name != null && name.Any(char.IsControl);
            name = (name ?? string.Empty).Trim();
            if (containsControl || !ReferenceCatalog.IsValidMaterial(name, velocity))
                return MaterialCatalogFailure(MaterialText("请分别填写有效材料名称与大于零的有限波速。名称不能含竖线、换行或控制字符。", "Enter a valid material name and a finite velocity greater than zero. Names cannot contain pipes, line breaks or control characters."));
            ReferenceCatalog candidate = catalog.Clone();
            MaterialData existing = candidate.Materials.FirstOrDefault(item => string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase));
            if (existing != null && !replaceExisting)
                return MaterialCatalogFailure(MaterialText("材料名称已存在，请确认后更新。", "The material already exists. Confirm before updating it."));
            if (existing == null) candidate.Materials.Add(new MaterialData { Name = name, Velocity = velocity });
            else existing.Velocity = velocity;
            if (!CommitMaterialCatalog(candidate)) return false;
            // Selecting the added item is a deliberate editor change; refreshing
            // catalog entries itself must not change existing cable segments.
            if (source == bodyTypeCombo || source == beforeBodyTypeCombo) SelectChoice(source, existing == null ? name : existing.Name);
            return true;
        }

        bool TryDeleteMaterial(string name)
        {
            ReferenceCatalog candidate = catalog.Clone();
            int removed = candidate.Materials.RemoveAll(item => string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase));
            if (removed == 0) return MaterialCatalogFailure(MaterialText("此材料来自项目记录，不在材料字典中；项目记录未更改。", "This material belongs to a project record, not the catalog. The project record was not changed."));
            if (candidate.Materials.Count == 0)
                return MaterialCatalogFailure(MaterialText("请至少保留一种材料；可先新增替代材料。", "Keep at least one material. Add a replacement first."));
            return CommitMaterialCatalog(candidate);
        }

        bool CommitMaterialCatalog(ReferenceCatalog candidate)
        { return CommitMaterialCatalog(candidate, false); }

        bool CommitMaterialCatalog(ReferenceCatalog candidate, bool recover)
        {
            string backup = string.Empty;
            try { if (recover) backup = candidate.RecoverAndSave(settingsPath); else candidate.Save(settingsPath); }
            catch (Exception exception) { string notice=CustomerProjectMessage(exception.ToString(),"材料列表未保存。请检查本地保存位置是否可写后重试。","The material list was not saved. Check local storage access and retry.");return MaterialCatalogFailure(notice,exception.Message); }
            // Keep the catalog/list identities used by the other reference menus.
            catalog.Materials.Clear();
            catalog.Materials.AddRange(candidate.Materials.Select(item => item.Clone()));
            catalog.AcceptPersistenceFrom(candidate);
            bool previous = suppressChanges;
            suppressChanges = true;
            try
            {
                foreach (ComboBox combo in new[] { bodyTypeCombo, beforeBodyTypeCombo })
                {
                    string selected = GetChoiceValue(combo);
                    FillMaterialChoice(combo, selected);
                }
            }
            finally { suppressChanges = previous; }
            CompleteOperation("SaveMaterialCatalog", true, string.Empty);
            SetStatus(MaterialText("材料字典已保存；已有项目材料未更改。", "Material catalog saved; existing project materials were not changed.") +
                (String.IsNullOrEmpty(backup) ? "" : MaterialText(" 损坏原件备份：", " Damaged original backup: ") + backup), true);
            return true;
        }

        bool MaterialCatalogFailure(string message,string diagnostic=null)
        {
            CompleteOperation("SaveMaterialCatalog", false, diagnostic??message);
            SetStatus(message, false);
            if (ShowDialogMessages) MessageBox.Show(this, message, L("输入无效"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }

        void FillMaterialChoice(ComboBox combo, string selected)
        {
            FillChoiceCombo(combo, catalog.Materials.Select(item => item.Name), selected);
            if (combo == null || combo.SelectedIndex < 0 || string.IsNullOrEmpty(selected) ||
                catalog.Materials.Any(item => string.Equals(item.Name, selected, StringComparison.OrdinalIgnoreCase))) return;
            // The project/editor value is not a catalog entry. Preserve it rather
            // than silently replacing a cable segment when a catalog item is deleted.
            int index = combo.SelectedIndex;
            combo.Items[index] = new LocalizedChoice(selected,
                ProjectCreationLocalization.TranslateCanonical(languageIndex, selected) +
                MaterialText("（当前值，不在材料字典）", " (current value, not in catalog)"));
            combo.SelectedIndex = index;
        }

        void RetireReferenceMenu(ContextMenuStrip menu)
        {
            if (menu == null || menu.IsDisposed) return;
            if (Disposing || IsDisposed || !IsHandleCreated) { menu.Dispose(); return; }
            // An ItemClicked handler may refresh language/catalog menus. Dispose
            // only after WinForms has finished dispatching that click.
            retiredReferenceMenus.Add(menu);
            try { BeginInvoke((Action)DisposeRetiredReferenceMenus); }
            catch (InvalidOperationException) { DisposeRetiredReferenceMenus(); }
        }

        void DisposeRetiredReferenceMenus()
        {
            foreach (ContextMenuStrip menu in retiredReferenceMenus) menu.Dispose();
            retiredReferenceMenus.Clear();
        }
    }
}
