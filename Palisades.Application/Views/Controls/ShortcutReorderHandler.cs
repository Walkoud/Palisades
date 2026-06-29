using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
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
            if (dropInfo.Data is ShortcutItem || dropInfo.Data is System.Collections.IEnumerable)
            {
                dropInfo.DropTargetAdorner = DropTargetAdorners.Insert;
                dropInfo.Effects = DragDropEffects.Move;
            }
        }

        public void Drop(IDropInfo dropInfo)
        {
            try
            {
                // 1. Get visual target ContainerViewModel
                var itemsControl = dropInfo.VisualTarget as ItemsControl;
                if (itemsControl?.DataContext is not ContainerViewModel targetVM) return;

                // 2. Extract all dragged ShortcutItems
                var draggedItems = ExtractDraggedItems(dropInfo);
                if (draggedItems.Count == 0) return;

                var list = targetVM.Shortcuts;

                // 3. Determine the target index in the underlying collection
                int targetIdx;
                if (dropInfo.TargetItem is ShortcutItem targetItem)
                {
                    targetIdx = list.IndexOf(targetItem);
                }
                else
                {
                    targetIdx = dropInfo.InsertIndex;
                }

                if (targetIdx < 0) targetIdx = 0;
                if (targetIdx > list.Count) targetIdx = list.Count;

                // Keep track of all containers that need to be saved
                var modifiedVMs = new HashSet<ContainerViewModel>();
                modifiedVMs.Add(targetVM);

                // 4. Check if we are moving within the same container
                var firstSource = draggedItems[0];
                var srcVM = FindContainerForShortcut(firstSource);

                if (srcVM == targetVM)
                {
                    // Move within the same container
                    foreach (var item in draggedItems)
                    {
                        int currentIdx = list.IndexOf(item);
                        if (currentIdx >= 0)
                        {
                            int newIdx = targetIdx;

                            // Adjust target index if moving forward and using InsertIndex fallback
                            if (dropInfo.TargetItem is not ShortcutItem && currentIdx < targetIdx)
                            {
                                newIdx--;
                            }

                            if (newIdx >= 0 && newIdx < list.Count && newIdx != currentIdx)
                            {
                                list.Move(currentIdx, newIdx);
                            }
                        }
                    }
                }
                else
                {
                    // Move from a different container or unassigned
                    foreach (var item in draggedItems)
                    {
                        var itemSrcVM = FindContainerForShortcut(item);
                        if (itemSrcVM != null)
                        {
                            itemSrcVM.Shortcuts.Remove(item);
                            modifiedVMs.Add(itemSrcVM);
                        }
                        else
                        {
                            if (ContainerManager.Instance.UnassignedShortcuts.Contains(item))
                            {
                                ContainerManager.Instance.UnassignedShortcuts.Remove(item);
                            }
                        }

                        int insertAt = targetIdx;
                        if (insertAt < 0) insertAt = 0;
                        if (insertAt > list.Count) insertAt = list.Count;

                        list.Insert(insertAt, item);
                    }
                }

                // 5. Save all modified viewmodels & refresh unassigned
                foreach (var vm in modifiedVMs)
                {
                    vm.Save();
                }

                ContainerManager.Instance.RefreshUnassignedShortcuts();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error in drop handler: {ex.Message}");
            }
        }

        private static List<ShortcutItem> ExtractDraggedItems(IDropInfo dropInfo)
        {
            var draggedItems = new List<ShortcutItem>();
            if (dropInfo.Data is ShortcutItem single)
            {
                // Check if item was part of multi-selection in source container
                var srcVM = FindContainerForShortcut(single);
                if (srcVM != null && srcVM.SelectedShortcuts.Count > 1 && srcVM.SelectedShortcuts.Contains(single))
                {
                    draggedItems.AddRange(srcVM.SelectedShortcuts);
                }
                else
                {
                    draggedItems.Add(single);
                }
            }
            else if (dropInfo.Data is System.Collections.IEnumerable enumerable)
            {
                foreach (var item in enumerable)
                {
                    if (item is ShortcutItem s)
                    {
                        draggedItems.Add(s);
                    }
                }
            }
            return draggedItems;
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
