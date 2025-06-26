using System;
using System.IO;

namespace DiffusionNexus.UI.Classes
{
    public static class AppDataHelper
    {
        private const string AppFolderName = "DiffusionNexus";

        public static string GetDataFolder()
        {
            var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppFolderName);
            Directory.CreateDirectory(folder);
            return folder;
        }
    }
}
