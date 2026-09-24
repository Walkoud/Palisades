using System;
using System.IO;

namespace Palisades.Services
{
    /// <summary>Trace DIAG du Dynamic Island : %LocalAppData%/Palisades/island_hover.log.
    /// Partagé service + VM + vues (un seul fichier).</summary>
    internal static class IslandDiag
    {
        private static readonly string Path = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Palisades", "island_hover.log");

        public static void Log(string msg)
        {
            try
            {
                var f = new FileInfo(Path);
                if (f.Exists && f.Length > 300 * 1024)
                    f.Delete();
                File.AppendAllText(Path, $"[{DateTime.Now:HH:mm:ss.fff}] {msg}\n");
            }
            catch { }
        }
    }
}
