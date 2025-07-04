/*
 * Licensed under the terms found in the LICENSE file in the root directory.
 * For non-commercial use only. See LICENSE for details.
 */

using System.Collections;

namespace DiffusionNexus.Service.Classes
{
    [AttributeUsage(AttributeTargets.Property)]
    public sealed class MetadataFieldAttribute : Attribute { }

    public class ModelClass
    {
        private const string Unknown = "UNKNOWN";
        private string diffusionBaseModel = Unknown;

        [MetadataField]
        public string DiffusionBaseModel
        {
            get => diffusionBaseModel;
            set => diffusionBaseModel = value == "SDXL 1.0" ? "SDXL" : value;
        }

        [MetadataField] public string SafeTensorFileName { get; set; }
        [MetadataField] public string ModelVersionName { get; set; }
        [MetadataField] public string?  ModelId { get; set; }
        [MetadataField] public string?  SHA256Hash { get; set; }
        [MetadataField] public DiffusionTypes ModelType { get; set; } = DiffusionTypes.OTHER;
        [MetadataField] public List<FileInfo> AssociatedFilesInfo { get; set; }
        [MetadataField] public List<string>  Tags { get; set; } = new();
        [MetadataField] public CivitaiBaseCategories CivitaiCategory { get; set; } = CivitaiBaseCategories.UNASSIGNED;

        // status flags - not metadata
        public bool NoMetaData { get; set; } = true;
        public bool ErrorOnRetrievingMetaData { get; internal set; }

        //------------------------------------------------------------------
        // Helper: evaluate completeness whenever you need it
        //------------------------------------------------------------------
        public MetadataCompleteness GetCompleteness()
        {
            var metaProps = typeof(ModelClass)
                            .GetProperties()
                            .Where(p => Attribute.IsDefined(p, typeof(MetadataFieldAttribute)));

            int total = 0;
            int filled = 0;

            foreach (var prop in metaProps)
            {
                total++;
                var value = prop.GetValue(this);

                if (value switch
                {
                    null                      => false,
                    string s                  => !string.IsNullOrWhiteSpace(s) && s != Unknown,
                    IList list                => list.Count > 0,
                    DiffusionTypes t          => t != DiffusionTypes.OTHER,
                    CivitaiBaseCategories cat => cat != CivitaiBaseCategories.UNASSIGNED,
                    _                         => true
                })
                {
                    filled++;
                }
            }

            return filled == 0     ? MetadataCompleteness.None
                 : filled == total ? MetadataCompleteness.Full
                                   : MetadataCompleteness.Partial;
        }

        //------------------------------------------------------------------
        // Convenience flags you can use anywhere in the codebase
        //------------------------------------------------------------------
        public bool HasAnyMetadata  => GetCompleteness() != MetadataCompleteness.None;
        public bool HasFullMetadata => GetCompleteness() == MetadataCompleteness.Full;
    }
}
