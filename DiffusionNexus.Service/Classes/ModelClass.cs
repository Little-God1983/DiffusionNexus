/*
 * Licensed under the terms found in the LICENSE file in the root directory.
 * For non-commercial use only. See LICENSE for details.
*/

using System.Collections;
using System.Linq;

namespace DiffusionNexus.Service.Classes
{
    public class ModelClass
    {
        private const string Unknown = "UNKNOWN";

        private string diffusionBaseModel = Unknown;
        private MetadataCompleteness? _cachedCompleteness;

        [MetadataField]
        public string DiffusionBaseModel
        {
            get => diffusionBaseModel;
            set
            {
                if (diffusionBaseModel != value)
                {
                    diffusionBaseModel = value == "SDXL 1.0" ? "SDXL" : value;
                    InvalidateCompletenessCache();
                }
            }
        }

        private string? _safeTensorFileName;
        [MetadataField]
        public string SafeTensorFileName
        {
            get => _safeTensorFileName ?? string.Empty;
            set
            {
                if (_safeTensorFileName != value)
                {
                    _safeTensorFileName = value;
                    InvalidateCompletenessCache();
                }
            }
        }

        private string? _modelVersionName;
        [MetadataField]
        public string ModelVersionName
        {
            get => _modelVersionName ?? string.Empty;
            set
            {
                if (_modelVersionName != value)
                {
                    _modelVersionName = value;
                    InvalidateCompletenessCache();
                }
            }
        }

        private string? _modelId;
        [MetadataField]
        public string? ModelId
        {
            get => _modelId;
            set
            {
                if (_modelId != value)
                {
                    _modelId = value;
                    InvalidateCompletenessCache();
                }
            }
        }

        public string? SHA256Hash { get; set; }

        private DiffusionTypes _modelType = DiffusionTypes.UNASSIGNED;
        [MetadataField]
        public DiffusionTypes ModelType
        {
            get => _modelType;
            set
            {
                if (_modelType != value)
                {
                    _modelType = value;
                    InvalidateCompletenessCache();
                }
            }
        }

        public List<FileInfo> AssociatedFilesInfo { get; set; }
        public List<string> Tags { get; set; } = new();

        private CivitaiBaseCategories _civitaiCategory = CivitaiBaseCategories.UNASSIGNED;
        [MetadataField]
        public CivitaiBaseCategories CivitaiCategory
        {
            get => _civitaiCategory;
            set
            {
                if (_civitaiCategory != value)
                {
                    _civitaiCategory = value;
                    InvalidateCompletenessCache();
                }
            }
        }

        public List<string> TrainedWords { get; set; } = new();
        public bool? Nsfw { get; set; }

        // status flags
        public bool NoMetaData { get; set; } = true;

        private void InvalidateCompletenessCache()
        {
            _cachedCompleteness = null;
        }

        //------------------------------------------------------------------
        // Helper: evaluate completeness whenever you need it
        //------------------------------------------------------------------
        public MetadataCompleteness GetCompleteness()
        {
            if (_cachedCompleteness.HasValue)
                return _cachedCompleteness.Value;

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

            _cachedCompleteness = filled == 0 ? MetadataCompleteness.None
                 : filled == total ? MetadataCompleteness.Full
                                    : MetadataCompleteness.Partial;

            return _cachedCompleteness.Value;
        }

        //------------------------------------------------------------------
        // Convenience flags
        //------------------------------------------------------------------
        public bool HasAnyMetadata => GetCompleteness() != MetadataCompleteness.None;
        public bool HasFullMetadata => GetCompleteness() == MetadataCompleteness.Full;
    }
}
