/*
 * Licensed under the terms found in the LICENSE file in the root directory.
 * For non-commercial use only. See LICENSE for details.
*/

using System.Collections;
using System.Collections.Generic;
using System.Linq;

namespace DiffusionNexus.Service.Classes
{
    public class ModelClass : CivitaiModelMetadataBase
    {
        private const string Unknown = "UNKNOWN";

        private string diffusionBaseModel = Unknown;

        [MetadataField]
        public string DiffusionBaseModel
        {
            get => diffusionBaseModel;
            set
            {
                var normalised = value switch
                {
                    null or "" => Unknown,
                    "SDXL 1.0" => "SDXL",
                    _ => value
                };
                diffusionBaseModel = normalised;
                base.BaseModel = normalised;
            }
        }

        public override string? BaseModel
        {
            get => DiffusionBaseModel;
            set => DiffusionBaseModel = value ?? Unknown;
        }

        [MetadataField] public string SafeTensorFileName { get; set; } = string.Empty;

        [MetadataField]
        public override string ModelVersionName
        {
            get => base.ModelVersionName;
            set => base.ModelVersionName = value;
        }

        [MetadataField] public string? ModelId { get; set; }
        public string? SHA256Hash { get; set; }
        [MetadataField] public DiffusionTypes ModelType { get; set; } = DiffusionTypes.UNASSIGNED;
        public List<FileInfo> AssociatedFilesInfo { get; set; } = new();
        [MetadataField] public List<string> Tags { get; set; } = new();
        [MetadataField] public CivitaiBaseCategories CivitaiCategory { get; set; } = CivitaiBaseCategories.UNASSIGNED;
        [MetadataField]
        public new List<string> TrainedWords
        {
            get => MutableTrainedWords;
            set => MutableTrainedWords = value ?? new List<string>();
        }
        [MetadataField] public bool? Nsfw { get; set; }
        public override string? Description { get; set; }

        // status flags
        public bool NoMetaData { get; set; } = true;

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
                    null => false,
                    string s => !string.IsNullOrWhiteSpace(s) && s != Unknown,
                    IList list => list.Count > 0,
                    DiffusionTypes t => t != DiffusionTypes.UNASSIGNED,
                    CivitaiBaseCategories cat => cat != CivitaiBaseCategories.UNASSIGNED,
                    _ => true
                })
                {
                    filled++;
                }
            }



            return filled == 0 ? MetadataCompleteness.None
                 : filled == total ? MetadataCompleteness.Full
                                    : MetadataCompleteness.Partial;
        }

        //------------------------------------------------------------------
        // Convenience flags
        //------------------------------------------------------------------
        public bool HasAnyMetadata => GetCompleteness() != MetadataCompleteness.None;
        public bool HasFullMetadata => GetCompleteness() == MetadataCompleteness.Full;
    }
}
