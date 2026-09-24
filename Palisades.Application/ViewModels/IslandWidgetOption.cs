using System;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using Palisades.Services;

namespace Palisades.ViewModels
{
    /// <summary>One row of the Dynamic Island widget checklist (dashboard).</summary>
    public sealed class IslandWidgetOption : INotifyPropertyChanged
    {
        public string GadgetType { get; }
        public string DisplayName { get; }

        public IslandWidgetOption(string gadgetType, string displayName)
        {
            GadgetType = gadgetType;
            DisplayName = displayName;
        }

        public bool IsPinned
        {
            get => DynamicIslandService.Instance.IsPinned(GadgetType);
            set
            {
                if (value && !DynamicIslandService.Instance.IsPinned(GadgetType))
                    MaybeImportFromDesktop(GadgetType);
                DynamicIslandService.Instance.SetPinned(GadgetType, value);
                OnPropertyChanged();
            }
        }

        /// <summary>Si un widget du même type existe sur le bureau, propose de
        /// reprendre ses réglages dans l'îlot (une fois, à l'épinglage).</summary>
        private static void MaybeImportFromDesktop(string type)
        {
            try
            {
                var desktop = PluginService.Instance.LoadGadgets()
                    .FirstOrDefault(g => string.Equals(g.GadgetType, type, StringComparison.OrdinalIgnoreCase));
                if (desktop == null || string.IsNullOrEmpty(desktop.CustomData)) return;
                if (string.Equals(GadgetTypeDefaults.Instance.Get(type), desktop.CustomData, StringComparison.Ordinal))
                    return;
                string msg = string.Format(
                    TranslationService.Instance["Db_IslandImportPrompt"]
                        ?? "A \"{0}\" widget already exists on the desktop. Reuse its settings in the island?",
                    desktop.Title);
                var res = System.Windows.MessageBox.Show(msg, "Palisades",
                    System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Question);
                if (res == System.Windows.MessageBoxResult.Yes)
                    GadgetTypeDefaults.Instance.Remember(type, desktop.CustomData);
            }
            catch { }
        }

        /// <summary>Expanded height of this widget in the island (px).</summary>
        public double Height
        {
            get => DynamicIslandService.Instance.GetWidgetHeight(GadgetType);
            set
            {
                DynamicIslandService.Instance.SetWidgetHeight(GadgetType, value);
                OnPropertyChanged();
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? n = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }
}
