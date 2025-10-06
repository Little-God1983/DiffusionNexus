using DiffusionNexus.Service.Classes;
using DiffusionNexus.UI.Classes;
using System;

namespace DiffusionNexus.UI.ViewModels;

public class ModelVariantViewModel
{
    public ModelVariantViewModel(ModelClass model, string variantLabel)
    {
        Model = model ?? throw new ArgumentNullException(nameof(model));
        VariantLabel = string.IsNullOrWhiteSpace(variantLabel)
            ? LoraVariantClassifier.DefaultVariantLabel
            : variantLabel;
    }

    public ModelClass Model { get; }

    public string VariantLabel { get; }

    public bool IsDefaultVariant => string.Equals(
        VariantLabel,
        LoraVariantClassifier.DefaultVariantLabel,
        StringComparison.OrdinalIgnoreCase);

    public string DisplayLabel => VariantLabel;

    public string SearchText
    {
        get
        {
            var name = Model.SafeTensorFileName ?? string.Empty;
            var version = Model.ModelVersionName ?? string.Empty;
            return $"{VariantLabel} {name} {version}".Trim();
        }
    }

    public bool MatchesSearch(string search)
    {
        if (string.IsNullOrWhiteSpace(search))
        {
            return true;
        }

        return (Model.SafeTensorFileName?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false)
            || (Model.ModelVersionName?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false)
            || (!string.IsNullOrWhiteSpace(VariantLabel) && VariantLabel.Contains(search, StringComparison.OrdinalIgnoreCase));
    }
}
