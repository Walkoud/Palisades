# Palisades Plugin Developer Guide

Palisades features an extensible, WPF-compatible plugin architecture. Developers can extend the workspace overlay with custom widgets (Gadgets) or inject custom menu actions.

This guide provides details on how the plugin system works, how to create external DLL plugins, and how to register your UI components.

---

## 🏗️ 1. Architecture Overview

At the heart of the Palisades plugin system is the `IPlugin` interface and the `PluginContext`.

### The Plugin Interface: `IPlugin`
Every plugin, whether compiled directly into the application (built-in) or loaded dynamically from an external assembly (`.dll`), must implement the `IPlugin` interface located in the `Palisades.Plugins` namespace.

```csharp
namespace Palisades.Plugins
{
    public interface IPlugin
    {
        string Name { get; }
        string Id { get; }
        string Version { get; }
        string Author { get; }
        string Description { get; }

        void OnLoad(PluginContext context);
        void OnUnload();
    }
}
```

### The `PluginContext`
When Palisades initializes, it calls the `OnLoad` method of each loaded plugin, passing a `PluginContext` instance. The context provides plugins with API access to the core host application:

```csharp
public class PluginContext
{
    // Access core view model and state
    public MainViewModel MainViewModel { get; }

    // Access the full-screen WPF overlay window
    public Window OverlayWindow { get; }

    // Register a new custom widget (gadget) UI
    public void RegisterGadget(
        string gadgetType, 
        string name, 
        Func<FrameworkElement> viewFactory, 
        double defaultWidth, 
        double defaultHeight);

    // Register a global action menu command
    public void AddMenuAction(string header, Action action);
}
```

---

## 🛠️ 2. Creating an External DLL Plugin

To create an external plugin for Palisades, follow these steps:

### Step 1: Create a Class Library Project
Create a new C# Class Library project targeting **.NET 8.0-windows** with WPF support enabled in your `.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0-windows</TargetFramework>
    <Nullable>enable</Nullable>
    <UseWPF>true</UseWPF>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>

  <ItemGroup>
    <!-- Add reference to Palisades.Application.dll or project dependency -->
    <Reference Include="Palisades.Application">
      <HintPath>path/to/Palisades.Application.dll</HintPath>
      <Private>false</Private> <!-- Keep false so you don't duplicate host dlls -->
    </Reference>
  </ItemGroup>
</Project>
```

### Step 2: Implement the Plugin Class
Create a class implementing `IPlugin`. Here is a complete skeleton of a custom widget plugin:

```csharp
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Palisades.Plugins;

namespace MyCustomPlugin
{
    public class QuickNotesPlugin : IPlugin
    {
        public string Name => "Quick Notes Widget";
        public string Id => "com.example.quicknotes";
        public string Version => "1.0.0";
        public string Author => "Developer Name";
        public string Description => "Adds a stylized note widget to your desktop.";

        public void OnLoad(PluginContext context)
        {
            // Register a widget gadget that can be added via the Palisades Dashboard
            context.RegisterGadget(
                gadgetType: "QuickNoteGadget",
                name: "Quick Note",
                viewFactory: () => CreateWidgetUI(),
                defaultWidth: 250,
                defaultHeight: 200
            );

            // Add an action button in the system toolbar menu
            context.AddMenuAction("Quick Note Info", () =>
            {
                MessageBox.Show("Quick Notes plugin is running successfully!");
            });
        }

        public void OnUnload()
        {
            // Cleanup any timers, listeners, or resources if necessary
        }

        private FrameworkElement CreateWidgetUI()
        {
            // Return any WPF control (e.g. a Custom UserControl or standard controls)
            var grid = new Grid
            {
                Background = new SolidColorBrush(Color.FromRgb(0x1E, 0x29, 0x3B))
            };

            var textBox = new TextBox
            {
                Text = "Type something here...",
                Background = Brushes.Transparent,
                Foreground = Brushes.White,
                BorderThickness = new Thickness(0),
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                Padding = new Thickness(10)
            };

            grid.Children.Add(textBox);
            return grid;
        }
    }
}
```

### Step 3: Build & Install
1. Compile your Class Library project.
2. Locate your output DLL (e.g., `MyCustomPlugin.dll`).
3. Copy the DLL (and any unique external dependencies) to the Palisades plugins directory:
   `%LocalAppData%\Palisades\Plugins\`
4. Open the Palisades Dashboard, navigate to the **Plugins Manager** tab, click **Refresh Plugins**, select your new plugin, and click **Add [Widget Name] to Desktop**.

---

## 🎨 3. Best Practices for UI Gadgets

When developing WPF UI widgets that sit on the Desktop Overlay:
1. **Glassmorphic Styling**: Match the host styling by using semi-transparent background brushes, rounded borders, and clean fonts (e.g., Segoe UI or Inter).
2. **Focus Handling**: The desktop overlay is designed with `WS_EX_NOACTIVATE` to stay behind other active apps. When your control requires keyboard focus (like text boxes), ensure they handle click routing properly so the overlay does not steal focus, or register custom input dialogs.
3. **Responsive Size**: Ensure controls scale gracefully when resized. Use grid rows/columns with star (`*`) sizing.
