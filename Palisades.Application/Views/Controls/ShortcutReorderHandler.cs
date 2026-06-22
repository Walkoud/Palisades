using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using GongSolutions.Wpf.DragDrop;
using Palisades.Models;
using Palisades.Services;
using Palisades.ViewModels;

namespace Palisades.Views.Controls
{
    public class ShortcutReorderHandler : IDropTarget
    {
        public void DragOver(IDropInfo dropInfo)
        {
            if (dropInfo.Data is ShortcutItem || dropInfo.Data is IList<ShortcutItem>)
            {
                dropInfo.DropTargetAdorner = DropTargetAdorners.Insert;
                dropInfo.Effects = DragDropEffects.Move;
            }
        }

        public void Drop(IDropInfo dropInfo)
        {
            if (dropInfo.Data is ShortcutItem source)
            {
                DropSingleWithMulti(source, dropInfo);
            }
            else if (dropInfo.Data is List<ShortcutItem> sources && sources.Count > 0)
            {
                var visualTarget = dropInfo.VisualTarget as FrameworkElement;
                if (visualTarget?.DataContext is not ContainerViewModel vm) return;
                DropMultiple(sources, dropInfo, vm);
            }
        }

        private void DropSingleWithMulti(ShortcutItem source, IDropInfo dropInfo)
        {
            var visualTarget = dropInfo.VisualTarget as FrameworkElement;
            if (visualTarget?.DataContext is not ContainerViewModel targetVM) return;

            // Check if item was part of multi-selection in source container
            var srcVM = FindContainerForShortcut(source);
            if (srcVM != null && srcVM.SelectedShortcuts.Count > 1
                && srcVM.SelectedShortcuts.Contains(source))
            {
                var allSelected = srcVM.SelectedShortcuts.ToList();
                DropMultiple(allSelected, dropInfo, targetVM);
                return;
            }

            // Single item drop
            var list = targetVM.Shortcuts;
            int oldIdx = list.IndexOf(source);

            if (oldIdx >= 0)
            {
                if (srcVM != null && srcVM != targetVM)
                    srcVM.Shortcuts.Remove(source);
                if (!list.Contains(source))
                    list.Add(source);
            }
            else if (srcVM != null)
            {
                srcVM.Shortcuts.Remove(source);
                list.Add(source);
            }
            else
            {
                ContainerManager.Instance.MoveToContainer(source, targetVM.Model);
                return;
            }

            InsertAtTarget(source, dropInfo, list);
            targetVM.Save();
        }

        private void DropMultiple(List<ShortcutItem> sources, IDropInfo dropInfo, ContainerViewModel targetVM)
        {
            var list = targetVM.Shortcuts;

            foreach (var source in sources)
            {
                int oldIdx = list.IndexOf(source);
                if (oldIdx >= 0)
                {
                    var srcVM = FindContainerForShortcut(source);
                    if (srcVM != null && srcVM != targetVM)
                        srcVM.Shortcuts.Remove(source);
                }
                else
                {
                    var srcVM = FindContainerForShortcut(source);
                    if (srcVM != null)
                        srcVM.Shortcuts.Remove(source);
                    else
                        ContainerManager.Instance.MoveToContainer(source, targetVM.Model);
                }
            }

            var targetItem = dropInfo.TargetItem as ShortcutItem;
            int targetIdx = targetItem != null ? list.IndexOf(targetItem) : list.Count;
            if (targetIdx < 0) targetIdx = list.Count;

            for (int i = 0; i < sources.Count; i++)
            {
                int curIdx = list.IndexOf(sources[i]);
                if (curIdx >= 0)
                {
                    int insertAt = targetIdx + i;
                    if (curIdx < insertAt)
                        list.Move(curIdx, Math.Min(insertAt, list.Count - 1));
                    else
                        list.Move(curIdx, insertAt);
                }
            }

            targetVM.Save();
        }

        private static void InsertAtTarget(ShortcutItem source, IDropInfo dropInfo, ObservableCollection<ShortcutItem> list)
        {
            if (dropInfo.TargetItem is ShortcutItem targetItem)
            {
                int targetIdx = list.IndexOf(targetItem);
                if (targetIdx < 0) targetIdx = 0;

                int currentIdx = list.IndexOf(source);
                if (currentIdx >= 0)
                {
                    if (currentIdx > targetIdx)
                        list.Move(currentIdx, targetIdx);
                    else
                        list.Move(currentIdx, targetIdx + 1);
                }
            }
        }

        public static ContainerViewModel? FindContainerForShortcut(ShortcutItem shortcut)
        {
            foreach (var c in ContainerManager.Instance.Containers)
            {
                if (c.Shortcuts.Contains(shortcut))
                {
                    return Application.Current.Windows
                        .OfType<Window>()
                        .Select(w => w.DataContext)
                        .OfType<MainViewModel>()
                        .SelectMany(m => m.Containers)
                        .FirstOrDefault(vm => vm.Model == c);
                }
            }
            return null;
        }
    }
}
