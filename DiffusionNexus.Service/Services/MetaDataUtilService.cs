using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DiffusionNexus.Service.Services
{
    public static class MetaDataUtilService
    {
        public static CivitaiBaseCategories GetCategoryFromTags(List<string> tags)
        {
            foreach (var tag in tags)
            {
                if (Enum.TryParse(tag.Replace(" ", "_").ToUpper(), out CivitaiBaseCategories category))
                {
                    return category;
                }
            }
            return CivitaiBaseCategories.UNKNOWN;
        }
    }
}
