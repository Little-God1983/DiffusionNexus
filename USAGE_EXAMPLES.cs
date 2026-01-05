using DiffusionNexus.DataAccess;
using DiffusionNexus.Service.Services;
using System.Net.Http;

namespace DiffusionNexus.Examples;

/// <summary>
/// Example code demonstrating how to use the new SQLite database system
/// </summary>
public class DatabaseUsageExamples
{
    public static async Task Example1_InitializeDatabase()
    {
        // Initialize the database (creates if doesn't exist)
        await DbContextFactory.EnsureDatabaseCreatedAsync();
        
        Console.WriteLine("Database initialized at default location:");
        Console.WriteLine(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DiffusionNexus",
            "diffusion_nexus.db"));
    }

    public static async Task Example2_ImportLocalFiles()
    {
        // Create database context
        using var context = DbContextFactory.CreateDbContext();
        
        // Create API client
        var apiClient = new CivitaiApiClient(new HttpClient());
        
        // Create import service
        var importService = new LocalFileImportService(context, apiClient);
        
        // Progress reporting
        var progress = new Progress<ProgressReport>(report =>
        {
            Console.WriteLine($"[{report.LogLevel}] {report.Percentage}% - {report.StatusMessage}");
        });
        
        // Import all files from a directory
        string loraDirectory = @"C:\StableDiffusion\models\Lora";
        await importService.ImportDirectoryAsync(loraDirectory, progress);
    }

    public static async Task Example3_QueryModels()
    {
        using var context = DbContextFactory.CreateDbContext();
        
        // Get all models with their versions and files
        var models = await new ModelSyncService(context, new CivitaiApiClient(new HttpClient()))
            .GetAllModelsAsync();
        
        foreach (var model in models)
        {
            Console.WriteLine($"Model: {model.Name} (ID: {model.CivitaiModelId})");
            Console.WriteLine($"  Type: {model.Type}, NSFW: {model.Nsfw}");
            
            foreach (var version in model.Versions)
            {
                Console.WriteLine($"  Version: {version.Name} ({version.BaseModel})");
                
                foreach (var file in version.Files.Where(f => f.LocalFilePath != null))
                {
                    Console.WriteLine($"    File: {file.Name}");
                    Console.WriteLine($"    Path: {file.LocalFilePath}");
                    Console.WriteLine($"    Hash: {file.SHA256Hash}");
                }
            }
        }
    }

    public static async Task Example4_FindModelByHash()
    {
        using var context = DbContextFactory.CreateDbContext();
        
        string sha256 = "abc123..."; // Your file's SHA256 hash
        
        var fileRepo = new DiffusionNexus.DataAccess.Repositories.ModelFileRepository(context);
        var modelFile = await fileRepo.GetBySHA256HashAsync(sha256);
        
        if (modelFile != null)
        {
            Console.WriteLine($"Found: {modelFile.ModelVersion.Model.Name}");
            Console.WriteLine($"Version: {modelFile.ModelVersion.Name}");
            Console.WriteLine($"Local Path: {modelFile.LocalFilePath}");
            Console.WriteLine($"Download URL: {modelFile.DownloadUrl}");
        }
        else
        {
            Console.WriteLine("Model not found in database");
        }
    }

    public static async Task Example5_UseMetadataProvider()
    {
        using var context = DbContextFactory.CreateDbContext();
        
        // Create metadata provider chain
        var apiClient = new CivitaiApiClient(new HttpClient());
        var providers = new IModelMetadataProvider[]
        {
            new DatabaseMetadataProvider(context),           // Check database first
            new LocalFileMetadataProvider(),                 // Then local .info/.json files
            new CivitaiApiMetadataProvider(apiClient, "")    // Finally Civitai API
        };
        
        // Create file controller with database-aware providers
        var fileController = new FileControllerService(providers);
        
        // Get metadata for a file - will use fastest available source
        string filePath = @"C:\Models\my_lora.safetensors";
        var progress = new Progress<ProgressReport>(r => Console.WriteLine(r.StatusMessage));
        
        var metadata = await fileController.GetModelMetadataWithFallbackAsync(
            filePath, 
            progress, 
            CancellationToken.None);
        
        Console.WriteLine($"Model: {metadata.ModelVersionName}");
        Console.WriteLine($"Base Model: {metadata.DiffusionBaseModel}");
        Console.WriteLine($"Type: {metadata.ModelType}");
        Console.WriteLine($"Tags: {string.Join(", ", metadata.Tags)}");
    }

    public static async Task Example6_SyncSingleFile()
    {
        using var context = DbContextFactory.CreateDbContext();
        var apiClient = new CivitaiApiClient(new HttpClient());
        var syncService = new ModelSyncService(context, apiClient);
        
        string filePath = @"C:\Models\character_lora.safetensors";
        
        // Compute hash
        string hash = new HashingService().ComputeFileHash(filePath);
        
        // Sync with database (will fetch from API if needed)
        var progress = new Progress<ProgressReport>(r => 
            Console.WriteLine($"{r.LogLevel}: {r.StatusMessage}"));
        
        var model = await syncService.SyncLocalFileAsync(filePath, hash, progress);
        
        if (model != null)
        {
            Console.WriteLine($"Synced: {model.Name}");
        }
    }

    public static async Task Example7_GetLocalFiles()
    {
        using var context = DbContextFactory.CreateDbContext();
        var syncService = new ModelSyncService(context, new CivitaiApiClient(new HttpClient()));
        
        // Get all files that have local paths set
        var localFiles = await syncService.GetLocalFilesAsync();
        
        Console.WriteLine($"Found {localFiles.Count()} local files:");
        foreach (var file in localFiles)
        {
            Console.WriteLine($"  {file.Name} - {file.LocalFilePath}");
        }
    }

    public static async Task Example8_CustomQuery()
    {
        using var context = DbContextFactory.CreateDbContext();
        
        // Find all NSFW LoRA models
        var nsfwLoras = context.Models
            .Where(m => m.Type == "LORA" && m.Nsfw)
            .Include(m => m.Versions)
                .ThenInclude(v => v.Files)
            .ToList();
        
        Console.WriteLine($"Found {nsfwLoras.Count} NSFW LoRA models");
        
        // Find all models with a specific tag
        var characterModels = context.Models
            .Where(m => m.Tags.Any(t => t.Tag.ToLower() == "character"))
            .ToList();
        
        Console.WriteLine($"Found {characterModels.Count} character models");
        
        // Find models by base model version
        var sdxlModels = context.ModelVersions
            .Where(v => v.BaseModel.Contains("SDXL"))
            .Include(v => v.Model)
            .Select(v => v.Model)
            .Distinct()
            .ToList();
        
        Console.WriteLine($"Found {sdxlModels.Count} SDXL models");
    }
}
