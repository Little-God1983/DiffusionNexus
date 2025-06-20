/*
 * Licensed under the terms found in the LICENSE file in the root directory.
 * For non-commercial use only. See LICENSE for details.
 */

namespace DiffusionNexus.LoraSort.Service.Classes
{
    public class CustomTagMap
    {
        //this is a list because in theory it should be possible to map multiple tags to a single folder
        public List<string> LookForTag { get; set; } = new List<string>();
        public string MapToFolder { get; set; }
        public int Priority { get; set; }
    }

}
