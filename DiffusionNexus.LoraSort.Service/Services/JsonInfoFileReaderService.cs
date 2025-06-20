/*
 * Licensed under the terms found in the LICENSE file in the root directory.
 * For non-commercial use only. See LICENSE for details.
 */

using DiffusionNexus.LoraSort.Service.Classes;
using DiffusionNexus.LoraSort.Service.Helper;
using Serilog;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Diagnostics;

namespace DiffusionNexus.LoraSort.Service.Services
{
    public class JsonInfoFileReaderService
    {
        private readonly string _loraInfoBasePath;
        private readonly string _apiKey;

        public JsonInfoFileReaderService(string jsonFilePath, string apiKey)
        {
            _loraInfoBasePath = jsonFilePath;
            _apiKey = apiKey;
        }
        /// <summary>
        /// Parses the safetensor file at the given path and returns the __metadata__ JSON element.
        /// </summary>
        /// <param name="filePath">The path to the safetensor file.</param>
        /// <returns>A JsonElement representing the metadata.</returns>
        public static string ParseSafetensorsMetadata(FileInfo file)
        {
            Log.Information("Reading metadata from file '{FileName}'", file.FullName);

            try
            {
                // Open the file for reading in binary mode.
                Log.Debug("Opening file {FileName} for reading...", file.FullName);
                using (FileStream fs = File.OpenRead(file.FullName))
                {
                    if (file.Length < 8)
                    {
                        Log.Error("File {FileName} is too short (length={FileLength}) to contain the header length.",
                                  file.FullName, file.Length);
                        throw new Exception("The file is too short to contain the header length.");
                    }

                    // Read the first 8 bytes which encode the header length as a little-endian UInt64.
                    Log.Debug("Reading first 8 bytes for header length from {FileName}", file.FullName);
                    byte[] headerLengthBytes = new byte[8];
                    int readCount = fs.Read(headerLengthBytes, 0, 8);
                    if (readCount != 8)
                    {
                        Log.Error("Failed to read the header length (expected 8 bytes, got {BytesRead}).", readCount);
                        throw new Exception("Failed to read the header length from the file.");
                    }

                    ulong headerLength = BitConverter.ToUInt64(headerLengthBytes, 0);
                    Log.Debug("Header length is {HeaderLength} bytes", headerLength);

                    // Read the header bytes.
                    byte[] headerBytes = new byte[headerLength];
                    readCount = fs.Read(headerBytes, 0, (int)headerLength);
                    if (readCount != (int)headerLength)
                    {
                        Log.Error("Failed to read the full header from the file. Expected {ExpectedBytes}, got {BytesRead}.",
                                  headerLength, readCount);
                        throw new Exception("Failed to read the full header from the file.");
                    }

                    // Decode the header as a UTF-8 string.
                    string headerJson = Encoding.UTF8.GetString(headerBytes);
                    Log.Debug("Header JSON: {HeaderJson}", headerJson);

                    // Parse the JSON.
                    return GetMetaDataFromJson(file, headerJson);
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error occurred while reading metadata from file '{FileName}'", file.FullName);
                throw; // Rethrow so the caller can handle/observe it as needed.
            }
            finally
            {
                Log.Debug("Finished attempting to read metadata from file '{FileName}'", file.FullName);
            }
        }

        private static string GetMetaDataFromJson(FileInfo file, string headerJson)
        {
            using (JsonDocument document = JsonDocument.Parse(headerJson))
            {
                JsonElement root = document.RootElement;
                if (root.TryGetProperty("__metadata__", out JsonElement metadata))
                {
                    Log.Debug("Metadata found under '__metadata__' property. Returning metadata.");
                    string modelHash = string.Empty;
                    if (metadata.TryGetProperty("ss_new_sd_model_hash", out JsonElement modelHashElement))
                    {
                        modelHash = modelHashElement.GetString();
                        Log.Debug("Model Hash (ss_new_sd_model_hash): " + modelHash);
                    }
                    else
                    {
                        Log.Error("The model has could not be found under a '__metadata__' property in file {FileName}", file.FullName);
                        throw new Exception("The model has could not be found under a '__metadata__' property in file");
                    }
                    return modelHash;
                }
                else
                {
                    Log.Error("The header does not contain a '__metadata__' property in file {FileName}", file.FullName);
                    throw new Exception("The header does not contain a '__metadata__' property.");
                }
            }
        }

        private static string ComputeSHA256(string filePath)
        {
            // Validate input
            if (!File.Exists(filePath))
            {
                Log.Error("File Not found {FileName}", filePath);
                throw new FileNotFoundException("File not found.", filePath);
            }

            // Open the file as a stream and compute the hash
            using (FileStream stream = File.OpenRead(filePath))
            using (SHA256 sha256 = SHA256.Create())
            {
                byte[] hashBytes = sha256.ComputeHash(stream);

                // Convert the byte array to a hexadecimal string
                StringBuilder sb = new StringBuilder();
                foreach (byte b in hashBytes)
                {
                    sb.Append(b.ToString("x2")); // Lowercase hex
                }

                return sb.ToString();
            }
        }

        public async Task<List<ModelClass>> GetModelData(IProgress<ProgressReport>? progress, string jsonFilePath, CancellationToken cancellationToken)
        {
            
            List<ModelClass> modelDataList = new List<ModelClass>();
            modelDataList = GroupFilesByPrefix(jsonFilePath);
            progress?.Report(new ProgressReport
            {
                Percentage = 0,
                StatusMessage = $"Number of LoRa's found: {modelDataList.Count}"
            });
            int count = 1;
            foreach (ModelClass model in modelDataList)
            {
              
                Log.Debug($"Processing Metadata: {count} of {modelDataList.Count} - {model.ModelName}");

                // Throw if cancellation is requested
                cancellationToken.ThrowIfCancellationRequested();
                //if(model.AssociatedFilesInfo.Any(x => x.Extension == ))

                if (model.NoMetaData == true)
                {
                    await UpdateModelDataFromCivitaiAPI(progress, model);
                }
                else
                {
                    // Try to read from local file info first 
                    model.CivitaiCategory = GetMatchingCategory(model);
                    model.DiffusionBaseModel = GetBaseModelName(model);
                    model.ModelType = GetModelType(model);

                    // If local file info is insufficient try via online API
                    if (model.DiffusionBaseModel == "UNKNOWN" || model.CivitaiCategory == CivitaiBaseCategories.UNKNOWN)
                    {
                        await UpdateModelDataFromCivitaiAPI(progress, model);
                    }
                }
                count++;
            }

            Log.Debug($"Processing Metadata finished.");
            return modelDataList;
        }

        private DiffusionTypes GetModelType(ModelClass model)
        {
            var fileCivitai = model.AssociatedFilesInfo.FirstOrDefault(x => x.FullName.Contains(".civitai.info"));
            if (fileCivitai != null)
            {
                CivitaiMetaDataService civitaiMetaDataService = new CivitaiMetaDataService(_apiKey);
                // Read file content as a JSON string
                string jsonContent = File.ReadAllText(fileCivitai.FullName);

                // Pass JSON string to GetModelType
                return civitaiMetaDataService.GetModelType(jsonContent);
            }
            return DiffusionTypes.OTHER;
        }

        private async Task UpdateModelDataFromCivitaiAPI(IProgress<ProgressReport>? progress, ModelClass model)
        {
            try
            {
                FileInfo SafetensorsFileInfo = model.AssociatedFilesInfo.FirstOrDefault(x => x.Extension == ".safetensors");
                if (SafetensorsFileInfo == null) 
                {
                    progress?.Report(new ProgressReport
                    {
                        IsSuccessful = false,
                        Percentage = 0,
                        StatusMessage = $"API Call skipped for: {model.ModelName} - No safetensorsfile found"
                    });
                    return;
                }
                progress?.Report(new ProgressReport
                {
                    Percentage = 0,
                    StatusMessage = $"Calling API for metadata of {model.ModelName}"
                });


                //string SafetensorModelHash = ParseSafetensorsMetadata(SafetensorsFileInfo);
                string SHA256FromSafetensorsFile = ComputeSHA256(SafetensorsFileInfo.FullName);
                CivitaiMetaDataService service = new CivitaiMetaDataService(_apiKey);
                //First API call to get info from the specific model version, some loras have a Pony, Flux, SDXL version
                string modelVersionInfoApiResponse = await service.GetModelVersionInformationFromCivitaiAsync(SHA256FromSafetensorsFile);
                string modelId = service.GetModelId(modelVersionInfoApiResponse);
                model.DiffusionBaseModel = service.GetBaseModelName(modelVersionInfoApiResponse);

                //Second API call to get basic info about the model like tags etc. These are stored on the model page not on the version page
                string modelInfoApiResponse = await service.GetModelInformationFromCivitaiAsync(modelId);
                model.Tags = service.GetTagsFromModelInfo(modelInfoApiResponse);
                model.ModelType = service.GetModelType(modelInfoApiResponse);
                model.NoMetaData = false;
                model.CivitaiCategory = GetCategoryFromTags(model.Tags);

            }
            catch (Exception ex)
            {
                progress?.Report(new ProgressReport
                {
                    IsSuccessful = false,
                    Percentage = 0,
                    StatusMessage = $"Error on Retrieving Meta Data from API for {model.ModelName} - {ex.Message}"
                });

                model.ErrorOnRetrievingMetaData = true;
            }
        }

        private CivitaiBaseCategories GetMatchingCategory(ModelClass model)
        {
            // Try loading tags from the ".json" file.
            var uniqueTags = new HashSet<string>(LoadTagsFromFile(model, ".civitai.info", "tags"));

            // If the category is still UNKNOWN, attempt to load additional tags from ".cm-info.json".
            uniqueTags.UnionWith(LoadTagsFromFile(model, ".cm-info.json", "Tags"));

            model.Tags = uniqueTags.ToList();

            // Determine and return the category based on tags.
            return GetCategoryFromTags(model.Tags);
        }

        // Helper method to load tags from a specified file ending.
        private HashSet<string> LoadTagsFromFile(ModelClass model, string ending, string searchTerm)
        {
            var file = model.AssociatedFilesInfo.FirstOrDefault(x => x.FullName.Contains(ending));
            if (file == null)
            {
                return new HashSet<string>();
            }

            JsonDocument jdoc = LoadJsonDocument(file.FullName);
            return GetTagsFromJson(jdoc.RootElement, searchTerm);
        }

        private CivitaiBaseCategories GetCategoryFromTags(List<string> tags)
        {
            //Implement CustomTag
            foreach (string tag in tags)
            {
                if (Enum.TryParse(tag.Replace(" ", "_").ToUpper(), out CivitaiBaseCategories category))
                {
                    return category; // Return the first match
                }
            }
            return CivitaiBaseCategories.UNKNOWN;
        }

        private List<FileInfo> GetAllFiles()
        {
            List<FileInfo> files = new List<FileInfo>();
            try
            {
                // Ensure the path is a directory and not a file path
                if (Directory.Exists(_loraInfoBasePath))
                {
                    DirectoryInfo dirInfo = new DirectoryInfo(_loraInfoBasePath);
                    //foreach (var file in dirInfo.GetFiles("*.info", SearchOption.AllDirectories))
                    //{
                    //    files.Add(file);
                    //}
                    foreach (var file in dirInfo.GetFiles("*", SearchOption.AllDirectories))
                    {
                        if (StaticFileTypes.ModelExtensions.Contains(Path.GetExtension(file.FullName)))
                        {
                            files.Add(file);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                throw new Exception(ex.Message);
            }
            return files;
        }

        public static List<ModelClass> GroupFilesByPrefix(string rootDirectory)
        {
            var fileGroups = new Dictionary<string, List<FileInfo>>();

            // Get all files from the directory and subdirectories
            string[] files = Directory.GetFiles(rootDirectory, "*", SearchOption.AllDirectories);

            foreach (var filePath in files)
            {
                var fileInfo = new FileInfo(filePath);
                var prefix = ExtractBaseName(fileInfo.Name).ToLower();

                if (!fileGroups.ContainsKey(prefix))
                {
                    fileGroups[prefix] = new List<FileInfo>();
                }
                fileGroups[prefix].Add(fileInfo);
            }

            // Create ModelClass instances from grouped files
            var modelClasses = new List<ModelClass>();

            foreach (var group in fileGroups)
            {
                ModelClass model = new ModelClass
                {
                    ModelName = group.Key,
                    AssociatedFilesInfo = group.Value,
                    CivitaiCategory = CivitaiBaseCategories.UNKNOWN // Set your desired category here
                };

                //If <= 1 That means there is no MetaData so the file will be skipped
                if (model.AssociatedFilesInfo.Count <= 1) { model.NoMetaData = true; }
                modelClasses.Add(model);
            }

            return modelClasses;
        }

        private static string GetPrefix(string fileName)
        {
            // Extract the prefix from the file name (everything before the last underscore)
            //var lastUnderscoreIndex = fileName.LastIndexOf('_');
            //return lastUnderscoreIndex > 0 ? fileName.Substring(0, lastUnderscoreIndex) : fileName;
            return Path.GetFileNameWithoutExtension(fileName);
            //return fileName.Split('.').First();
        }
        static string ExtractBaseName(string fileName)
        {
            // Order the known extensions descending by length.
            var extension = StaticFileTypes.GeneralExtensions
                .OrderByDescending(e => e.Length)
                .FirstOrDefault(e => fileName.EndsWith(e, StringComparison.OrdinalIgnoreCase));

            if (extension != null)
            {
                return fileName.Substring(0, fileName.Length - extension.Length);
            }

            // If no extension is matched, return the original filename.
            return fileName;
        }

        private JsonDocument LoadJsonDocument(string filePath)
        {
            string jsonData = File.ReadAllText(filePath);
            // The using statement is removed to allow the document to be used outside of this method
            return JsonDocument.Parse(jsonData);
        }

        private string GetBaseModelName(ModelClass model)
        {
            try
            {
                var fileCivitai = model.AssociatedFilesInfo.FirstOrDefault(x => x.FullName.Contains(".civitai.info"));
                var fileJson = model.AssociatedFilesInfo.FirstOrDefault(x => x.Extension == ".json");

                if (fileCivitai != null)
                {
                    using (JsonDocument jdoc = LoadJsonDocument(fileCivitai.FullName))
                    {
                        JsonElement root = jdoc.RootElement;
                        return root.GetProperty("baseModel").GetString();
                    }
                }
                else if (fileJson != null)
                {
                    using (JsonDocument jdoc = LoadJsonDocument(fileJson.FullName))
                    {
                        JsonElement root = jdoc.RootElement;
                        if (root.TryGetProperty("sd version", out JsonElement tagElement) && tagElement.ValueKind == JsonValueKind.String)
                        {
                            return tagElement.GetString();
                        }
                    }
                }

                return "UNKNOWN";
            }
            catch (Exception ex)
            {
                // Optionally log the exception, for example:
                //Logger.LogError(ex, "Error retrieving base model");
                return "UNKNOWN";
            }

        }

        private HashSet<string> GetTagsFromJson(JsonElement root, string searchTerm)
        {
            HashSet<string> tags = new HashSet<string>();

            if (root.TryGetProperty("model", out JsonElement model) && model.TryGetProperty(searchTerm, out JsonElement tagsElement))
            {
                GetTagsFromJsonElement(tags, tagsElement);
            }
            else if (root.TryGetProperty(searchTerm, out JsonElement tagElement))
            {
                GetTagsFromJsonElement(tags, tagElement);
            }

            return tags;
        }

        private static void GetTagsFromJsonElement(HashSet<string> tags, JsonElement tagsElement)
        {
            foreach (JsonElement tagElement in tagsElement.EnumerateArray())
            {
                string tag = tagElement.GetString();
                if (!string.IsNullOrEmpty(tag))
                {
                    tags.Add(tag);
                }
            }
        }
    }
}