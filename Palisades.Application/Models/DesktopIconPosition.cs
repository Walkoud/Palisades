using System.Collections.Generic;

namespace Palisades.Models
{
    public class DesktopIconPosition
    {
        public string ShortcutPath { get; set; } = string.Empty;
        public double X { get; set; }
        public double Y { get; set; }
    }

    public class DesktopIconPositionsData
    {
        public Dictionary<string, List<DesktopIconPosition>> Positions { get; set; } = new();
    }
}
