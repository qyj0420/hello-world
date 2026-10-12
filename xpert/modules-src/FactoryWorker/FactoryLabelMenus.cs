using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;

namespace Xiongxin.FactoryTest
{
    internal static class FactoryLabelMenus
    {
        internal static void Clear(ToolStripItemCollection items)
        { foreach (var item in items.Cast<ToolStripItem>().ToArray()) { items.Remove(item); item.Dispose(); } }
        internal static void AddEdits(ToolStripItemCollection items, IEnumerable<FactoryTfAnnotationRegion> source,
            Action<FactoryTfAnnotationRegion> edit)
        {
            if (edit == null) return;
            var labels = source.GroupBy(r => r.Id).Select(g => g.First()).ToList();
            if (labels.Count == 0) return;
            if (labels.Count == 1)
            { var label = labels[0]; items.Add("修改标签：“" + label.Name + "”…", null, (s,e) => edit(label)); return; }
            var group = new ToolStripMenuItem("修改已有标签…（选择一个）");
            foreach (var label in labels)
            {
                var target = label;
                group.DropDownItems.Add(label.Name + " · " + FactoryTfAnnotationRegion.KindText(label.Kind) + " · " + label.Id.Substring(0, 6), null, (s,e) => edit(target));
            }
            items.Add(group);
        }
    }
}
