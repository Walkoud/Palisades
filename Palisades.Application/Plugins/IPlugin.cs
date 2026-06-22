using System;

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
