using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using System;

namespace DiffusionNexus.UI.Classes
{
    public class ModuleItem
    {
        public string Name { get; }
        public IImage Icon { get; }
        public object View { get; }

        public ModuleItem(string name, string iconUri, object view)
        {
            Name = name;
            View = view;
            using var stream = AssetLoader.Open(new Uri(iconUri));
            Icon = new Bitmap(stream);
        }
    }
}
