using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace Palisades.Models
{
public class SnapshotModel
{
    public string Identifier { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public string Type { get; set; } = "Manual"; // "Manual" or "Auto"
    public List<ContainerModel> Containers { get; set; } = new();
    public List<NoteItem> Notes { get; set; } = new();
    public List<PluginGadgetItem> Gadgets { get; set; } = new();
    public bool IsDarkMode { get; set; } = true;
    public string SelectedTheme { get; set; } = "Dark";
    public double GlobalOpacity { get; set; }
    public string? ScreenshotPath { get; set; }
}
}
