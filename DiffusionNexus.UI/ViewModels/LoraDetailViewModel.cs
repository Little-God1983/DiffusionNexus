using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia.Media.Imaging;
using DiffusionNexus.Service.Classes;

namespace DiffusionNexus.UI.ViewModels;

public class LoraDetailViewModel : ViewModelBase
{
    private readonly LoraCardViewModel _card;
    private readonly Uri? _previewMediaUri;
    private readonly string? _previewMediaPath;

    public LoraDetailViewModel(LoraCardViewModel card)
    {
        _card = card;
        var mediaPath = card.GetPreviewMediaPath();
        if (!string.IsNullOrWhiteSpace(mediaPath) && File.Exists(mediaPath))
        {
            _previewMediaPath = mediaPath;
            _previewMediaUri = new Uri(mediaPath);
        }
    }

    public LoraCardViewModel Card => _card;

    public string ModelName => _card.Model?.ModelVersionName
        ?? _card.Model?.SafeTensorFileName
        ?? "Unknown Model";

    public IReadOnlyList<string> Tags => (IReadOnlyList<string>?)_card.Model?.Tags ?? Array.Empty<string>();

    public bool HasTags => _card.Model?.Tags?.Count > 0;

    public Bitmap? PreviewImage => _card.PreviewImage;

    public bool HasPreviewVideo => _previewMediaUri != null;

    public Uri? PreviewMediaSource => _previewMediaUri;

    public string? PreviewMediaPath => _previewMediaPath;

    public string ModelIdDisplay => string.IsNullOrWhiteSpace(_card.Model?.ModelId)
        ? "Not available"
        : _card.Model!.ModelId!;

    public string ModelVersionIdDisplay => string.IsNullOrWhiteSpace(_card.Model?.ModelVersionId)
        ? "Not available"
        : _card.Model!.ModelVersionId!;

    public string BaseModelDisplay => string.IsNullOrWhiteSpace(_card.Model?.DiffusionBaseModel)
        ? "Unknown"
        : _card.Model!.DiffusionBaseModel;

    public string FileNameWithExtension
    {
        get
        {
            var model = _card.Model;
            if (model == null)
            {
                return "Not available";
            }

            var file = model.AssociatedFilesInfo
                .FirstOrDefault(f => SupportedTypes.ModelTypesByPriority.Any(ext =>
                    f.Extension.Equals(ext, StringComparison.OrdinalIgnoreCase)));

            if (file != null)
            {
                return file.Name;
            }

            if (!string.IsNullOrWhiteSpace(model.SafeTensorFileName))
            {
                return model.SafeTensorFileName.EndsWith(".safetensors", StringComparison.OrdinalIgnoreCase)
                    ? model.SafeTensorFileName
                    : model.SafeTensorFileName + ".safetensors";
            }

            return "Not available";
        }
    }

    public string ModelTypeDisplay => _card.Model?.ModelType.ToString() ?? "Unknown";

    public string Description => string.IsNullOrWhiteSpace(_card.Model?.Description)
        ? "No description available."
        : _card.Model!.Description!;

    public bool HasDescription => !string.IsNullOrWhiteSpace(_card.Model?.Description);
}
