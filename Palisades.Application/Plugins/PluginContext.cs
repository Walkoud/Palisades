using System;
using System.Collections.Generic;
using System.Windows;
using Palisades.ViewModels;

namespace Palisades.Plugins
{
    public class PluginContext
    {
        public MainViewModel MainViewModel { get; }
        public Window OverlayWindow { get; }

        public List<PluginMenuItem> MenuItems { get; } = new List<PluginMenuItem>();
        public List<PluginGadget> Gadgets { get; } = new List<PluginGadget>();

        public PluginContext(MainViewModel vm, Window overlayWindow)
        {
            MainViewModel = vm;
            OverlayWindow = overlayWindow;
        }

        public void RegisterGadget(string gadgetType, string name, Func<FrameworkElement> viewFactory, double defaultWidth, double defaultHeight)
        {
            Gadgets.Add(new PluginGadget(gadgetType, name, viewFactory, defaultWidth, defaultHeight));
        }

        public void AddMenuAction(string header, Action action)
        {
            MenuItems.Add(new PluginMenuItem(header, action));
        }
    }

    public class PluginMenuItem
    {
        public string Header { get; }
        public Action Action { get; }

        public PluginMenuItem(string header, Action action)
        {
            Header = header;
            Action = action;
        }
    }

    public class PluginGadget
    {
        public string GadgetType { get; }
        public string Name { get; }
        public Func<FrameworkElement> ViewFactory { get; }
        public double DefaultWidth { get; }
        public double DefaultHeight { get; }

        public PluginGadget(string gadgetType, string name, Func<FrameworkElement> viewFactory, double defaultWidth, double defaultHeight)
        {
            GadgetType = gadgetType;
            Name = name;
            ViewFactory = viewFactory;
            DefaultWidth = defaultWidth;
            DefaultHeight = defaultHeight;
        }
    }
}
