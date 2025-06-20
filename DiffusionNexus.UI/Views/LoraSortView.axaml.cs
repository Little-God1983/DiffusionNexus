using Avalonia.Controls;
using DiffusionNexus.LoraSort.Service.Classes;
using System;
using System.Collections.Generic;
using System.Linq;

namespace DiffusionNexus.UI;

public partial class LoraSortView : UserControl
{
    public LoraSortView()
    {
        InitializeComponent();
        // Prepopulate the fields with the mapping's data.
        txtTags.Text = string.Join(", ", Mapping.LookForTag);
        txtFolder.Text = Mapping.MapToFolder;
    }
    public CustomTagMap Mapping { get; set; } = new CustomTagMap
    {
        LookForTag = new List<string>(),
        MapToFolder = string.Empty
    };

    private void Save_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        // Get the comma–separated tags.
        string tagsInput = txtTags.Text.Trim();
        List<string> tagsList = new List<string>();
        if (!string.IsNullOrWhiteSpace(tagsInput))
        {
            tagsList = tagsInput.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                                .Select(s => s.Trim())
                                .Where(s => !string.IsNullOrEmpty(s))
                                .ToList();
        }

        // Get the target folder.
        string folder = txtFolder.Text.Trim();
        if (tagsList.Count == 0 || string.IsNullOrEmpty(folder))
        {
            //MessageBox.Show("Please enter at least one tag and a target folder.", "Incomplete Data", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // Update the mapping.
        Mapping.LookForTag = tagsList;
        Mapping.MapToFolder = folder;

        //this.DialogResult = true;
        //this.Close();
    }

    private void Cancel_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        //this.DialogResult = false;
        //this.Close();
    }
}