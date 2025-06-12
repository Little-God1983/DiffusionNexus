using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DiffusionNexus.UI.Classes
{

    public class ModuleItem
    {
        public string Name { get; }
        public string Icon { get; }
        public object View { get; }

        public ModuleItem(string name, string icon, object view)
        {
            Name = name;
            Icon = icon;
            View = view;
        }

    }
}
