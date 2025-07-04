/*
 * Licensed under the terms found in the LICENSE file in the root directory.
 * For non-commercial use only. See LICENSE for details.
 */
using Serilog;
using DiffusionNexus.Service.Classes;

namespace DiffusionNexus.Service.Services
{
    public class FileCopyService
    {
        public FileCopyService()
        {

        }
        private void EnsureFolderExists(IProgress<ProgressReport>? progress, string directoryPath)
        {
            try
            {
                if (!Directory.Exists(directoryPath))
                {
                    Directory.CreateDirectory(directoryPath);
                    progress?.Report(new ProgressReport { IsSuccessful = true, StatusMessage = $"Directory '{directoryPath}' created successfully.", LogLevel = LogSeverity.Success });
                }
            }
            catch (Exception ex)
            {
                Log.Error($"Failed to create directory '{directoryPath}' {ex.Message}");
                progress?.Report(new ProgressReport { IsSuccessful = false, StatusMessage = $"Failed to create directory '{directoryPath}'", LogLevel = LogSeverity.Error });
            }
        }

        public bool ProcessModelClasses(IProgress<ProgressReport>? progress, CancellationToken cancellationToken, List<ModelClass> models, SelectedOptions options)
        {
            int totalModels = models.Count;
            int currentModel = 0;
            bool hasErrors = false;

            foreach (var model in models)
            {
                // Update progress based on model index.
                int percentage = (int)((double)currentModel / totalModels * 100);

                // Throw if cancellation is requested
                cancellationToken.ThrowIfCancellationRequested();

                if (model.NoMetaData)
                {
                    progress?.Report(new ProgressReport { IsSuccessful = false, Percentage = percentage, StatusMessage = $"File '{model.SafeTensorFileName}' has no metaData => File is skipped.", LogLevel = LogSeverity.Warning });
                    hasErrors = true;
                    continue;
                }
                else if (!model.HasFullMetadata)
                {
                    //No neeed to make a progress report since we allready display the failed API call
                    hasErrors = true;
                    continue;
                }

                if (model.ModelType != DiffusionTypes.LORA && model.ModelType != DiffusionTypes.LOCON)
                {
                    progress?.Report(new ProgressReport { IsSuccessful = false, Percentage = percentage, StatusMessage = $"File '{model.SafeTensorFileName}' is not a Lora. It is of Type: {model.ModelType.ToString()} => File is skipped.", LogLevel = LogSeverity.Warning });
                    hasErrors = true;
                    continue;
                }
                string modelDirectory = GetTargetDirectoryPath(options, model);

                EnsureFolderExists(progress, modelDirectory);
                hasErrors = ExecuteCopyMove(progress, options, hasErrors, model, percentage, modelDirectory);
                currentModel++;
            }

            return hasErrors;
        }

        private static bool ExecuteCopyMove(IProgress<ProgressReport>? progress, SelectedOptions options, bool hasErrors, ModelClass model, int percentage, string modelDirectory)
        {
            foreach (var modelFile in model.AssociatedFilesInfo)
            {
                string source = modelFile.FullName;
                string target = Path.Combine(modelDirectory, modelFile.Name);
                try
                {
                    if (options.IsMoveOperation)
                    {
                        File.Move(source, target, options.OverrideFiles);
                        progress?.Report(new ProgressReport { IsSuccessful = true, Percentage = percentage, StatusMessage = $"File '{modelFile.Name}' moved to '{modelDirectory}'.", LogLevel = LogSeverity.Success });
                    }
                    else
                    {
                        File.Copy(source, target, options.OverrideFiles);
                        progress?.Report(new ProgressReport { IsSuccessful = true, Percentage = percentage, StatusMessage = $"File '{modelFile.Name}' copied to '{modelDirectory}'.", LogLevel = LogSeverity.Success });
                    }
                }
                catch (Exception ex)
                {
                    progress?.Report(new ProgressReport { IsSuccessful = false, Percentage = percentage, StatusMessage = $"Error copying file '{modelFile.Name}' Reason: {ex.Message}", LogLevel = LogSeverity.Error });
                    hasErrors = true;
                }
            }

            return hasErrors;
        }

        private static string GetTargetDirectoryPath(SelectedOptions options, ModelClass model)
        {
            if (options.UseCustomMappings)
            {
                // Check if any of the CustomTagMaps Tags match the model tags.
                // This has to be done in order by custom mapping priority; the first match wins.
                // and returns the Path.Combine(options.TargetPath, model.DiffusionBaseModel, PathToFolder);
                CustomTagMapXmlService customTagMapXmlService = new CustomTagMapXmlService();
                var tagMaps = customTagMapXmlService.LoadMappings()
                                            .OrderBy(m => m.Priority)
                                            .ToList();
                if (tagMaps.Count != 0 && model.Tags.Count != 0)
                {
                    foreach (var map in tagMaps)
                    {
                        if (map.LookForTag.Count != 0 && map.LookForTag.Intersect(model.Tags, StringComparer.OrdinalIgnoreCase).Any())
                        {
                            // If CreateBaseFolders is true, include DiffusionBaseModel in the path
                            if (options.CreateBaseFolders)
                            {
                                return Path.Combine(options.TargetPath, model.DiffusionBaseModel, map.MapToFolder);
                            }
                            else
                            {
                                return Path.Combine(options.TargetPath, map.MapToFolder);
                            }
                        }
                    }
                }
            }
            // Default behavior if no custom mappings are used or no match is found
            if (options.CreateBaseFolders)
            {
                return Path.Combine(options.TargetPath, model.DiffusionBaseModel, model.CivitaiCategory.ToString());
            }
            else
            {
                return Path.Combine(options.TargetPath, model.CivitaiCategory.ToString());
            }
        }
    }
}