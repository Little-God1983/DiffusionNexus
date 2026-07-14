# Batch Metadata Distiller — Part 2: Gallery & Run Screen — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking. **Complete Part 1 (core engine) first** — this part consumes its services.

**Goal:** Surface the Batch Metadata Distiller as a Workflows-gallery tile (with a labelled "Utilities" divider) and build its bespoke three-column run screen: input & prompt-detection, per-image metadata editor, and automation + output.

**Architecture:** The distiller is NOT a diffusion run, so it does not extend the generation base `PipelineRunViewModel`. Both satisfy a thin new interface `IPipelineRun` so the gallery's `ActiveRun` slot can host either. The run VM is thin orchestration over the Part 1 services plus a `MetadataDistillerService`. Manifest gains `Category` (for the divider) and `RequiresModels` (the distiller needs no ComfyUI models, so it bypasses the readiness gate).

**Tech Stack:** Avalonia 11 + CommunityToolkit.Mvvm, C# / .NET 10, xUnit + FluentAssertions + Moq.

## Global Constraints

- Target framework: `net10.0`; `Nullable` + `ImplicitUsings` enabled.
- The gallery/pipelines feature is internally named "Pipelines"; the user-facing word is "Workflows". New code follows the `Pipeline`/`pipelines` naming.
- Run VMs are constructed via `ActivatorUtilities.CreateInstance<T>(sp, tile.Manifest)` — the manifest is the sole extra ctor arg; every other ctor parameter must be DI-resolvable (`ILoraCatalog`, `IPipelineAssetInstaller`, `IDialogService` are already registered).
- Existing tile DataTemplate divider idiom is a 1px `Border` at `#40FFFFFF`.
- Reuse `ImageListInputControl` (`ImagePaths: ObservableCollection<string>?` TwoWay, mutated in place; `SelectedImagePath: string?` TwoWay) for input — do not build a new picker.
- Work on branch `feature/batch-metadata-distiller`. Commit after every task. Build `DiffusionNexus.UI/DiffusionNexus.UI.csproj`; test `DiffusionNexus.Tests/DiffusionNexus.Tests.csproj` from `e:\Repos\DiffusionNexus`.

---

### Task 1: Manifest gains `Category` + `RequiresModels`; register the tile

**Files:**
- Modify: `DiffusionNexus.UI/Models/Pipelines/PipelineManifest.cs`
- Modify: `DiffusionNexus.UI/Services/Pipelines/PipelineManifestProvider.cs:22`
- Modify: `DiffusionNexus.UI/ViewModels/PipelineTileViewModel.cs`
- Create: `DiffusionNexus.UI/Assets/Pipelines/batch-metadata-distiller.json`
- Test: `DiffusionNexus.Tests/Distiller/PipelineManifestFieldsTests.cs`

**Interfaces:**
- Consumes: nothing (data-only).
- Produces: `PipelineManifest.Category` (`string`, default `"Generation"`), `PipelineManifest.RequiresModels` (`bool`, default `true`); `PipelineTileViewModel.Category`. A loadable `batch-metadata-distiller` manifest with `Category="Utilities"`, `RequiresModels=false`.

- [ ] **Step 1: Write the failing test**

Create `DiffusionNexus.Tests/Distiller/PipelineManifestFieldsTests.cs`:

```csharp
using DiffusionNexus.UI.Models.Pipelines;
using DiffusionNexus.UI.Services.Pipelines;
using FluentAssertions;
using System.Linq;

namespace DiffusionNexus.Tests.Distiller;

public class PipelineManifestFieldsTests
{
    [Fact]
    public void Manifest_defaults_are_generation_and_requires_models()
    {
        var m = new PipelineManifest();
        m.Category.Should().Be("Generation");
        m.RequiresModels.Should().BeTrue();
    }

    [Fact]
    public void Distiller_manifest_loads_as_utility_without_models()
    {
        var provider = new PipelineManifestProvider();
        var m = provider.All().FirstOrDefault(x => x.Id == "batch-metadata-distiller");

        m.Should().NotBeNull();
        m!.Category.Should().Be("Utilities");
        m.RequiresModels.Should().BeFalse();
        m.ShowInGallery.Should().BeTrue();
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test DiffusionNexus.Tests/DiffusionNexus.Tests.csproj --filter "FullyQualifiedName~PipelineManifestFieldsTests"`
Expected: FAIL — `Category`/`RequiresModels` don't exist and the manifest isn't registered.

- [ ] **Step 3: Add the manifest fields**

In `DiffusionNexus.UI/Models/Pipelines/PipelineManifest.cs`, add after the `ShowInGallery` property (before the class closing brace):

```csharp
    /// <summary>
    /// Gallery grouping. "Generation" (default) for guided render workflows; "Utilities" for
    /// tools like the Batch Metadata Distiller. Drives the labelled divider in the gallery.
    /// </summary>
    public string Category { get; init; } = "Generation";

    /// <summary>
    /// Whether opening this workflow requires an installed ComfyUI models tree. <c>true</c> (default)
    /// runs the readiness gate; <c>false</c> (e.g. the metadata distiller, which does no inference)
    /// opens the run screen directly.
    /// </summary>
    public bool RequiresModels { get; init; } = true;
```

- [ ] **Step 4: Register the manifest id**

In `DiffusionNexus.UI/Services/Pipelines/PipelineManifestProvider.cs`, change line 22 from:

```csharp
    private static readonly string[] ManifestIds = ["anime-to-real", "qwen-image-2512", "image-to-image"];
```

to:

```csharp
    private static readonly string[] ManifestIds = ["anime-to-real", "qwen-image-2512", "image-to-image", "batch-metadata-distiller"];
```

- [ ] **Step 5: Surface Category on the tile VM**

In `DiffusionNexus.UI/ViewModels/PipelineTileViewModel.cs`, add after the `Description` property (around line 41):

```csharp
    public string Category => Manifest.Category;
```

- [ ] **Step 6: Create the manifest asset**

Create `DiffusionNexus.UI/Assets/Pipelines/batch-metadata-distiller.json`:

```json
{
  "id": "batch-metadata-distiller",
  "title": "Batch Metadata Distiller",
  "description": "Recover ComfyUI generation data (prompt, sampler, CFG, LoRAs incl. Power Lora / Lora Stack) and re-save clean, CivitAI-readable copies.",
  "showInGallery": true,
  "category": "Utilities",
  "requiresModels": false,
  "assets": []
}
```

- [ ] **Step 7: Verify the asset is embedded**

Open `DiffusionNexus.UI/DiffusionNexus.UI.csproj` and confirm an `<AvaloniaResource Include="Assets\**" />` (or `Assets\Pipelines\**`) glob covers the new file. If manifests are listed individually instead, add:

```xml
    <AvaloniaResource Include="Assets\Pipelines\batch-metadata-distiller.json" />
```

- [ ] **Step 8: Run test to verify it passes**

Run: `dotnet test DiffusionNexus.Tests/DiffusionNexus.Tests.csproj --filter "FullyQualifiedName~PipelineManifestFieldsTests"`
Expected: PASS.

- [ ] **Step 9: Commit**

```bash
git add DiffusionNexus.UI/Models/Pipelines/PipelineManifest.cs DiffusionNexus.UI/Services/Pipelines/PipelineManifestProvider.cs DiffusionNexus.UI/ViewModels/PipelineTileViewModel.cs DiffusionNexus.UI/Assets/Pipelines/batch-metadata-distiller.json DiffusionNexus.Tests/Distiller/PipelineManifestFieldsTests.cs
git commit -m "feat(distiller): register utility pipeline manifest + Category/RequiresModels"
```

---

### Task 2: `IPipelineRun` interface + gallery hosting (bypass readiness, group tiles)

**Files:**
- Create: `DiffusionNexus.UI/ViewModels/Pipelines/IPipelineRun.cs`
- Modify: `DiffusionNexus.UI/ViewModels/Pipelines/PipelineRunViewModel.cs:30`
- Modify: `DiffusionNexus.UI/ViewModels/PipelinesViewModel.cs`

**Interfaces:**
- Consumes: `PipelineManifest.RequiresModels`/`Category` (Task 1).
- Produces:
  - `interface IPipelineRun : IDisposable { string Title { get; } event EventHandler? CloseRequested; ResourceMonitorViewModel? ResourceMonitor { get; set; } void LoadInputImages(IReadOnlyList<string> paths); }`
  - `PipelinesViewModel.ActiveRun` retyped to `IPipelineRun?`; run factory delegate retyped to `Func<PipelineTileViewModel, IPipelineRun>`; new computed collections `GenerationPipelines`, `UtilityPipelines`, `HasUtilityPipelines`.

- [ ] **Step 1: Create the interface**

Create `DiffusionNexus.UI/ViewModels/Pipelines/IPipelineRun.cs`:

```csharp
using System;
using System.Collections.Generic;

namespace DiffusionNexus.UI.ViewModels.Pipelines;

/// <summary>
/// The minimal contract the Workflows gallery needs to host a run screen in its <c>ActiveRun</c>
/// slot. Satisfied by the generation base <see cref="PipelineRunViewModel"/> (which already has
/// every member) and by the bespoke <see cref="BatchMetadataDistillerViewModel"/>.
/// </summary>
public interface IPipelineRun : IDisposable
{
    /// <summary>Screen title shown in the run UI.</summary>
    string Title { get; }

    /// <summary>Raised when the user clicks Back; the gallery clears its active run.</summary>
    event EventHandler? CloseRequested;

    /// <summary>Set by the gallery to share its single GPU/RAM monitor. May be ignored.</summary>
    ResourceMonitorViewModel? ResourceMonitor { get; set; }

    /// <summary>Pre-loads a set of input images (the "Send to → Workflows" flow).</summary>
    void LoadInputImages(IReadOnlyList<string> paths);
}
```

- [ ] **Step 2: Make `PipelineRunViewModel` implement it (declaration only)**

In `DiffusionNexus.UI/ViewModels/Pipelines/PipelineRunViewModel.cs`, change line 30 from:

```csharp
public abstract partial class PipelineRunViewModel : ViewModelBase, IDisposable
```

to:

```csharp
public abstract partial class PipelineRunViewModel : ViewModelBase, IDisposable, IPipelineRun
```

(No member changes — it already declares `Title`, `CloseRequested`, `ResourceMonitor`, `LoadInputImages`, `Dispose`.)

- [ ] **Step 3: Retype the gallery to `IPipelineRun` and add grouping + bypass**

In `DiffusionNexus.UI/ViewModels/PipelinesViewModel.cs`:

(a) Change the factory field (line ~31):

```csharp
    private readonly Func<PipelineTileViewModel, PipelineRunViewModel>? _runFactory;
```
to:
```csharp
    private readonly Func<PipelineTileViewModel, ViewModels.Pipelines.IPipelineRun>? _runFactory;
```

(b) Change the `ActiveRun` backing field (line ~42-43):

```csharp
    [ObservableProperty]
    private PipelineRunViewModel? _activeRun;
```
to:
```csharp
    [ObservableProperty]
    private ViewModels.Pipelines.IPipelineRun? _activeRun;
```

(c) Change the ctor parameter (line ~55):

```csharp
        Func<PipelineTileViewModel, PipelineRunViewModel>? runFactory = null,
```
to:
```csharp
        Func<PipelineTileViewModel, ViewModels.Pipelines.IPipelineRun>? runFactory = null,
```

(d) Add the grouped collections just after the `Pipelines` property (line ~34):

```csharp
    /// <summary>Generation-category tiles (shown above the Utilities divider).</summary>
    public IEnumerable<PipelineTileViewModel> GenerationPipelines => Pipelines.Where(t => !IsUtility(t));

    /// <summary>Utility-category tiles (shown below the Utilities divider).</summary>
    public IEnumerable<PipelineTileViewModel> UtilityPipelines => Pipelines.Where(IsUtility);

    /// <summary>True when at least one utility tile exists (drives the divider's visibility).</summary>
    public bool HasUtilityPipelines => Pipelines.Any(IsUtility);

    private static bool IsUtility(PipelineTileViewModel t) =>
        string.Equals(t.Manifest.Category, "Utilities", StringComparison.OrdinalIgnoreCase);
```

(e) Add the readiness bypass at the top of the `foreach (var tile in Pipelines)` loop in `RefreshAllStatusesAsync` (right after the `foreach` opening brace, before the `if (root is null)` check):

```csharp
            if (!tile.Manifest.RequiresModels)
            {
                SetStatus(tile, PipelineStatus.Ready, "Ready");
                continue;
            }
```

(f) Add the open bypass in `OpenPipelineInternalAsync`, immediately after the `if (tile.IsBusy) return;` line:

```csharp
        // Utility workflows (e.g. the metadata distiller) need no ComfyUI models — open directly.
        if (!tile.Manifest.RequiresModels)
        {
            OpenRun(tile, inputImages);
            return;
        }
```

- [ ] **Step 4: Build to verify the retype compiles**

Run: `dotnet build DiffusionNexus.UI/DiffusionNexus.UI.csproj`
Expected: FAIL — `App.axaml.cs` still registers `Func<..., PipelineRunViewModel>`. That is fixed in Task 6; for now confirm the ONLY errors are in `App.axaml.cs` about the factory delegate type. (The `PipelinesViewModel` and `IPipelineRun` files themselves must compile.)

- [ ] **Step 5: Commit**

```bash
git add DiffusionNexus.UI/ViewModels/Pipelines/IPipelineRun.cs DiffusionNexus.UI/ViewModels/Pipelines/PipelineRunViewModel.cs DiffusionNexus.UI/ViewModels/PipelinesViewModel.cs
git commit -m "feat(distiller): IPipelineRun abstraction + gallery grouping and readiness bypass"
```

---

### Task 3: Gallery view — labelled divider + run-view resolution

**Files:**
- Modify: `DiffusionNexus.UI/Views/PipelinesView.axaml`

**Interfaces:**
- Consumes: `GenerationPipelines`, `UtilityPipelines`, `HasUtilityPipelines`, `ActiveRun` (Task 2); `BatchMetadataDistillerView` (Task 8 — reference compiles once that view exists; do Task 3's build check after Task 8, or add a temporary placeholder as noted).
- Produces: a gallery that renders two tile groups split by a labelled divider, and a `ContentControl` that resolves either run VM to its view.

- [ ] **Step 1: Add the ViewModels.Pipelines namespace + shared tile template**

In `DiffusionNexus.UI/Views/PipelinesView.axaml`, add to the root `UserControl` opening tag the namespace:

```xml
             xmlns:vmp="using:DiffusionNexus.UI.ViewModels.Pipelines"
```

Then add a `UserControl.Resources` block (immediately after `</Design.DataContext>`), moving the existing tile `DataTemplate` into a keyed resource so both groups share it:

```xml
  <UserControl.Resources>
    <DataTemplate x:Key="PipelineTileTemplate" x:DataType="vm:PipelineTileViewModel">
      <Button Width="240" Height="260" Margin="0,0,16,16"
              Padding="0" CornerRadius="8" BorderThickness="0"
              HorizontalContentAlignment="Stretch" VerticalContentAlignment="Stretch"
              Cursor="Hand"
              Command="{Binding $parent[ItemsControl].DataContext.OpenPipelineCommand}"
              CommandParameter="{Binding}"
              ToolTip.Tip="{Binding Description}">
        <Button.Styles>
          <Style Selector="Button"><Setter Property="Background" Value="Transparent"/></Style>
          <Style Selector="Button:pointerover /template/ ContentPresenter"><Setter Property="Background" Value="Transparent"/></Style>
          <Style Selector="Button:pressed /template/ ContentPresenter"><Setter Property="Background" Value="Transparent"/></Style>
          <Style Selector="Button:pointerover Border#Card"><Setter Property="BorderBrush" Value="#0078D4"/></Style>
        </Button.Styles>
        <Border x:Name="Card" Background="#2D2D2D" CornerRadius="8" BorderBrush="#3D3D3D" BorderThickness="1" ClipToBounds="True">
          <Grid RowDefinitions="*,Auto">
            <Border Grid.Row="0" Background="#1A1A1A">
              <Grid>
                <Image Source="{Binding IconBitmap}" Stretch="UniformToFill"
                       HorizontalAlignment="Stretch" VerticalAlignment="Stretch" IsVisible="{Binding HasCustomIcon}"/>
                <Image Source="{Binding IconBitmap}" Width="96" Height="96"
                       HorizontalAlignment="Center" VerticalAlignment="Center" IsVisible="{Binding !HasCustomIcon}"/>
                <Border HorizontalAlignment="Right" VerticalAlignment="Top" Margin="6"
                        Background="{Binding StatusBrush}" CornerRadius="4" Padding="6,2">
                  <TextBlock Text="{Binding StatusText}" FontSize="10" FontWeight="SemiBold" Foreground="White"/>
                </Border>
                <Border IsVisible="{Binding IsBusy}" Background="#80000000">
                  <ProgressBar IsIndeterminate="True" Width="120" HorizontalAlignment="Center" VerticalAlignment="Center"/>
                </Border>
              </Grid>
            </Border>
            <StackPanel Grid.Row="1" Background="#252525" Spacing="2">
              <TextBlock Text="{Binding Title}" FontSize="15" FontWeight="SemiBold" Foreground="White"
                         Margin="12,8,12,0" TextTrimming="CharacterEllipsis"/>
              <TextBlock Text="{Binding Description}" FontSize="11" Foreground="#A0A0A0"
                         Margin="12,0,12,10" TextWrapping="Wrap" MaxLines="2" TextTrimming="CharacterEllipsis"/>
            </StackPanel>
          </Grid>
        </Border>
      </Button>
    </DataTemplate>
  </UserControl.Resources>
```

- [ ] **Step 2: Replace the run `ContentControl` with typed resolution**

Replace the existing run `ContentControl` block (the one wrapping `<pipelines:PipelineRunView .../>`) with:

```xml
  <!-- Active run (generation OR distiller) — replaces the gallery while open -->
  <ContentControl Content="{Binding ActiveRun}"
                  IsVisible="{Binding ActiveRun, Converter={x:Static ObjectConverters.IsNotNull}}">
    <ContentControl.DataTemplates>
      <DataTemplate DataType="vmp:PipelineRunViewModel">
        <pipelines:PipelineRunView/>
      </DataTemplate>
      <DataTemplate DataType="vmp:BatchMetadataDistillerViewModel">
        <pipelines:BatchMetadataDistillerView/>
      </DataTemplate>
    </ContentControl.DataTemplates>
  </ContentControl>
```

(The `PipelineRunViewModel` template matches the abstract base, so both concrete generation VMs still render via `PipelineRunView`.)

- [ ] **Step 3: Replace the single tile ItemsControl with two grouped ones + divider**

Replace the entire `<ScrollViewer Grid.Row="2" ...> ... </ScrollViewer>` block with:

```xml
    <ScrollViewer Grid.Row="2" VerticalScrollBarVisibility="Auto">
      <StackPanel Spacing="0">

        <ItemsControl ItemsSource="{Binding GenerationPipelines}"
                      ItemTemplate="{StaticResource PipelineTileTemplate}">
          <ItemsControl.ItemsPanel>
            <ItemsPanelTemplate><WrapPanel Orientation="Horizontal"/></ItemsPanelTemplate>
          </ItemsControl.ItemsPanel>
        </ItemsControl>

        <!-- Labelled Utilities divider (requested horizontal line) -->
        <Grid ColumnDefinitions="Auto,*" Margin="0,6,0,14" IsVisible="{Binding HasUtilityPipelines}">
          <TextBlock Text="UTILITIES" FontSize="11" FontWeight="SemiBold" Foreground="#808080"
                     VerticalAlignment="Center" Margin="2,0,0,0"/>
          <Border Grid.Column="1" Height="1" Background="#40FFFFFF" Margin="12,0,0,0" VerticalAlignment="Center"/>
        </Grid>

        <ItemsControl ItemsSource="{Binding UtilityPipelines}"
                      IsVisible="{Binding HasUtilityPipelines}"
                      ItemTemplate="{StaticResource PipelineTileTemplate}">
          <ItemsControl.ItemsPanel>
            <ItemsPanelTemplate><WrapPanel Orientation="Horizontal"/></ItemsPanelTemplate>
          </ItemsControl.ItemsPanel>
        </ItemsControl>

      </StackPanel>
    </ScrollViewer>
```

- [ ] **Step 4: Defer the build check**

This view references `BatchMetadataDistillerView` (Task 8) and `BatchMetadataDistillerViewModel` (Task 7). It will not fully compile until those exist. Proceed to Task 4; the gallery build is verified at the end of Task 8.

- [ ] **Step 5: Commit**

```bash
git add DiffusionNexus.UI/Views/PipelinesView.axaml
git commit -m "feat(distiller): gallery Utilities divider + typed run-view resolution"
```

---

### Task 4: Run-screen sub-view-models + `DistillOptions`

**Files:**
- Create: `DiffusionNexus.UI/Models/Distiller/DistillOptions.cs`
- Create: `DiffusionNexus.UI/ViewModels/Pipelines/PromptRuleSetViewModel.cs`
- Create: `DiffusionNexus.UI/ViewModels/Pipelines/DistillerLoraViewModel.cs`
- Create: `DiffusionNexus.UI/ViewModels/Pipelines/DistillerItemViewModel.cs`
- Test: `DiffusionNexus.Tests/Distiller/DistillerViewModelsTests.cs`

**Interfaces:**
- Consumes: `PromptRuleSet`, `RuleKind`, `ReplacePair`, `ImageGenerationData`, `LoraInfo` (Part 1 / models).
- Produces (used by Task 5's service and Task 6's VM):
  - `PromptRuleSetViewModel` with `ToModel() → PromptRuleSet`.
  - `DistillerLoraViewModel` with `ToLoraInfo() → LoraInfo`.
  - `DistillerItemViewModel` with `BuildDistillItem() → DistillItem` (defined in Task 5) — but to avoid a forward dependency, `DistillerItemViewModel` exposes `BuildEditedData() → ImageGenerationData`, `IncludedLoras() → IReadOnlyList<LoraInfo>`, and the editable `Positive`/`Negative` — the service assembles the `DistillItem`.

- [ ] **Step 1: Write the failing tests**

Create `DiffusionNexus.Tests/Distiller/DistillerViewModelsTests.cs`:

```csharp
using System.Linq;
using DiffusionNexus.UI.Models;
using DiffusionNexus.UI.Models.Distiller;
using DiffusionNexus.UI.ViewModels.Pipelines;
using FluentAssertions;

namespace DiffusionNexus.Tests.Distiller;

public class DistillerViewModelsTests
{
    [Fact]
    public void RuleSet_delete_parses_words_from_commas_and_newlines()
    {
        var vm = new PromptRuleSetViewModel { Name = "Q", IsReplace = false, WordsText = "masterpiece, best quality\n4k" };

        var model = vm.ToModel();

        model.Kind.Should().Be(RuleKind.Delete);
        model.DeleteWords.Should().Equal("masterpiece", "best quality", "4k");
    }

    [Fact]
    public void RuleSet_replace_parses_arrow_pairs()
    {
        var vm = new PromptRuleSetViewModel { Name = "R", IsReplace = true, WordsText = "1girl => woman\n1boy -> man" };

        var model = vm.ToModel();

        model.Kind.Should().Be(RuleKind.Replace);
        model.ReplacePairs.Select(p => (p.From, p.To)).Should().Equal(("1girl", "woman"), ("1boy", "man"));
    }

    [Fact]
    public void Item_builds_edited_data_and_included_loras()
    {
        var data = new ImageGenerationData
        {
            PositivePrompt = "p", NegativePrompt = "n", Steps = 20, Cfg = 7, Seed = 5,
            SamplerName = "euler", Scheduler = "normal", Checkpoint = "base",
            Loras = [new LoraInfo { Name = "keep", StrengthModel = 0.8 }, new LoraInfo { Name = "drop", StrengthModel = 0.5 }],
            Width = 512, Height = 512, HasData = true
        };
        var item = new DistillerItemViewModel("c:/x.png", data);
        item.StepsText = "28";                 // user edit
        item.Loras.First(l => l.Name == "drop").Include = false;

        var edited = item.BuildEditedData();
        var loras = item.IncludedLoras();

        edited.Steps.Should().Be(28);
        edited.Checkpoint.Should().Be("base");
        loras.Select(l => l.Name).Should().Equal("keep");
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test DiffusionNexus.Tests/DiffusionNexus.Tests.csproj --filter "FullyQualifiedName~DistillerViewModelsTests"`
Expected: FAIL to compile — the VMs don't exist.

- [ ] **Step 3: Create `DistillOptions`**

Create `DiffusionNexus.UI/Models/Distiller/DistillOptions.cs`:

```csharp
namespace DiffusionNexus.UI.Models.Distiller;

/// <summary>Run-time options for a distill run.</summary>
public sealed class DistillOptions
{
    /// <summary>Remove the embedded ComfyUI workflow/prompt chunks from output (default on).</summary>
    public bool StripWorkflow { get; set; } = true;

    /// <summary>Compute AutoV2 resource hashes for found LoRAs/checkpoints (slower).</summary>
    public bool ComputeHashes { get; set; }

    /// <summary>Destination folder for cleaned copies. Must be set before a run.</summary>
    public string? OutputFolder { get; set; }
}
```

- [ ] **Step 4: Create `PromptRuleSetViewModel`**

Create `DiffusionNexus.UI/ViewModels/Pipelines/PromptRuleSetViewModel.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using DiffusionNexus.UI.Models.Distiller;

namespace DiffusionNexus.UI.ViewModels.Pipelines;

/// <summary>Editable view of one named delete/replace rule set.</summary>
public partial class PromptRuleSetViewModel : ViewModelBase
{
    [ObservableProperty] private string _name = "New rule set";
    [ObservableProperty] private bool _isReplace;
    [ObservableProperty] private bool _enabled = true;

    /// <summary>
    /// Free-text editor content. Delete sets: words separated by commas/newlines. Replace sets:
    /// one "from =&gt; to" (or "-&gt;" / "→") per line.
    /// </summary>
    [ObservableProperty] private string _wordsText = "";

    public PromptRuleSet ToModel()
    {
        if (IsReplace)
        {
            var pairs = new List<ReplacePair>();
            foreach (var line in WordsText.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var idx = IndexOfArrow(line, out var arrowLen);
                if (idx < 0) continue;
                var from = line[..idx].Trim();
                var to = line[(idx + arrowLen)..].Trim();
                if (from.Length > 0) pairs.Add(new ReplacePair(from, to));
            }
            return new PromptRuleSet { Name = Name, Kind = RuleKind.Replace, Enabled = Enabled, ReplacePairs = pairs };
        }

        var words = WordsText
            .Split(['\n', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
        return new PromptRuleSet { Name = Name, Kind = RuleKind.Delete, Enabled = Enabled, DeleteWords = words };
    }

    private static int IndexOfArrow(string line, out int len)
    {
        foreach (var (arrow, l) in new[] { ("=>", 2), ("->", 2), ("→", 1) })
        {
            var i = line.IndexOf(arrow, StringComparison.Ordinal);
            if (i >= 0) { len = l; return i; }
        }
        len = 0; return -1;
    }
}
```

- [ ] **Step 5: Create `DistillerLoraViewModel`**

Create `DiffusionNexus.UI/ViewModels/Pipelines/DistillerLoraViewModel.cs`:

```csharp
using CommunityToolkit.Mvvm.ComponentModel;
using DiffusionNexus.UI.Models;

namespace DiffusionNexus.UI.ViewModels.Pipelines;

/// <summary>One detected LoRA row in the metadata editor.</summary>
public partial class DistillerLoraViewModel : ViewModelBase
{
    public string Name { get; }
    public string? SourceLabel { get; }

    [ObservableProperty] private double _strength;
    [ObservableProperty] private bool _include = true;
    [ObservableProperty] private bool _foundLocally;

    public DistillerLoraViewModel(LoraInfo info)
    {
        Name = info.Name;
        SourceLabel = info.Source;
        _strength = info.StrengthModel;
    }

    public LoraInfo ToLoraInfo() => new() { Name = Name, StrengthModel = Strength, StrengthClip = Strength, Source = SourceLabel };
}
```

- [ ] **Step 6: Create `DistillerItemViewModel`**

Create `DiffusionNexus.UI/ViewModels/Pipelines/DistillerItemViewModel.cs`:

```csharp
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using DiffusionNexus.UI.Models;

namespace DiffusionNexus.UI.ViewModels.Pipelines;

/// <summary>One input image: its parsed metadata, editable working copies, and detected LoRAs.</summary>
public partial class DistillerItemViewModel : ViewModelBase
{
    private readonly ImageGenerationData _data;

    public string Path { get; }
    public string FileName { get; }
    public bool HasMetadata { get; }
    public bool HasLoras { get; }
    public bool IsPng { get; }

    [ObservableProperty] private Bitmap? _thumbnail;
    [ObservableProperty] private bool _includeInRun;

    [ObservableProperty] private string? _positive;
    [ObservableProperty] private string? _negative;
    [ObservableProperty] private string _stepsText = "";
    [ObservableProperty] private string _cfgText = "";
    [ObservableProperty] private string _seedText = "";
    [ObservableProperty] private string? _samplerName;
    [ObservableProperty] private string? _scheduler;
    [ObservableProperty] private string? _model;

    public ObservableCollection<DistillerLoraViewModel> Loras { get; } = [];

    public DistillerItemViewModel(string path, ImageGenerationData data)
    {
        _data = data;
        Path = path;
        FileName = System.IO.Path.GetFileName(path);
        IsPng = string.Equals(System.IO.Path.GetExtension(path), ".png", System.StringComparison.OrdinalIgnoreCase);
        HasMetadata = data.HasData;
        HasLoras = data.Loras.Count > 0;
        IncludeInRun = data.HasData && IsPng; // v1 writes PNG output only

        Positive = data.PositivePrompt;
        Negative = data.NegativePrompt;
        StepsText = data.Steps?.ToString(CultureInfo.InvariantCulture) ?? "";
        CfgText = data.Cfg?.ToString("0.###", CultureInfo.InvariantCulture) ?? "";
        SeedText = data.Seed?.ToString(CultureInfo.InvariantCulture) ?? "";
        SamplerName = data.SamplerName;
        Scheduler = data.Scheduler;
        Model = data.Checkpoint;

        foreach (var lora in data.Loras)
            Loras.Add(new DistillerLoraViewModel(lora));
    }

    /// <summary>Loads a small preview off the UI thread. Safe to skip in tests.</summary>
    public async Task LoadThumbnailAsync()
    {
        try
        {
            var bmp = await Task.Run(() =>
            {
                using var fs = File.OpenRead(Path);
                return Bitmap.DecodeToWidth(fs, 200);
            });
            Thumbnail = bmp;
        }
        catch { /* undecodable — leave placeholder */ }
    }

    /// <summary>Applies the user's numeric/text edits back onto a copy of the parsed data.</summary>
    public ImageGenerationData BuildEditedData() => _data with
    {
        PositivePrompt = Positive,
        NegativePrompt = Negative,
        Steps = int.TryParse(StepsText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var s) ? s : null,
        Cfg = double.TryParse(CfgText, NumberStyles.Float, CultureInfo.InvariantCulture, out var c) ? c : null,
        Seed = long.TryParse(SeedText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var sd) ? sd : null,
        SamplerName = SamplerName,
        Scheduler = Scheduler,
        Checkpoint = Model,
    };

    public IReadOnlyList<LoraInfo> IncludedLoras() =>
        Loras.Where(l => l.Include).Select(l => l.ToLoraInfo()).ToList();
}
```

- [ ] **Step 7: Run tests to verify they pass**

Run: `dotnet test DiffusionNexus.Tests/DiffusionNexus.Tests.csproj --filter "FullyQualifiedName~DistillerViewModelsTests"`
Expected: PASS.

- [ ] **Step 8: Commit**

```bash
git add DiffusionNexus.UI/Models/Distiller/DistillOptions.cs DiffusionNexus.UI/ViewModels/Pipelines/PromptRuleSetViewModel.cs DiffusionNexus.UI/ViewModels/Pipelines/DistillerLoraViewModel.cs DiffusionNexus.UI/ViewModels/Pipelines/DistillerItemViewModel.cs DiffusionNexus.Tests/Distiller/DistillerViewModelsTests.cs
git commit -m "feat(distiller): run-screen sub view-models + DistillOptions"
```

---

### Task 5: `MetadataDistillerService` — batch orchestration

**Files:**
- Create: `DiffusionNexus.UI/Services/Distiller/MetadataDistillerService.cs`
- Test: `DiffusionNexus.Tests/Distiller/MetadataDistillerServiceTests.cs`

**Interfaces:**
- Consumes: `PromptRuleEngine`, `A1111MetadataFormatter`, `ImageResourceHasher`, `PngMetadataWriter` (Part 1); `PromptRuleSet`, `DistillOptions`, `ImageGenerationData`, `LoraInfo`.
- Produces:
  - `sealed record DistillItem(string SourcePath, ImageGenerationData Data, string Positive, string? Negative, IReadOnlyList<LoraInfo> Loras)`
  - `sealed record DistillResult(int Written, IReadOnlyList<DistillFailure> Failures)`
  - `sealed record DistillFailure(string SourcePath, string Error)`
  - `sealed class MetadataDistillerService(ImageResourceHasher hasher)` with `Task<DistillResult> DistillAsync(IReadOnlyList<DistillItem> items, IReadOnlyList<PromptRuleSet> ruleSets, DistillOptions options, IProgress<int>? progress, CancellationToken ct)`.

- [ ] **Step 1: Write the failing test**

Create `DiffusionNexus.Tests/Distiller/MetadataDistillerServiceTests.cs`:

```csharp
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using DiffusionNexus.UI.Models;
using DiffusionNexus.UI.Models.Distiller;
using DiffusionNexus.UI.Services;
using DiffusionNexus.UI.Services.Distiller;
using DiffusionNexus.UI.Services.Lora;
using FluentAssertions;
using Moq;

namespace DiffusionNexus.Tests.Distiller;

public class MetadataDistillerServiceTests
{
    private static string MakePng()
    {
        // Seed a PNG that carries a ComfyUI "prompt"/"workflow" chunk so we can prove stripping.
        var basePath = Path.Combine(Path.GetTempPath(), $"seed_{System.Guid.NewGuid():N}.png");
        File.WriteAllBytes(basePath, MinimalPng());
        var src = Path.Combine(Path.GetTempPath(), $"in_{System.Guid.NewGuid():N}.png");
        PngMetadataWriter.CopyWithMetadata(basePath, src, new() { ["prompt"] = "{...}", ["workflow"] = "{...}" });
        File.Delete(basePath);
        return src;
    }

    private static byte[] MinimalPng()
    {
        using var ms = new MemoryStream();
        ms.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        WriteChunk(ms, "IHDR", new byte[13]);
        WriteChunk(ms, "IEND", []);
        return ms.ToArray();
    }

    private static void WriteChunk(Stream s, string type, byte[] data)
    {
        var len = System.BitConverter.GetBytes(data.Length);
        if (System.BitConverter.IsLittleEndian) System.Array.Reverse(len);
        s.Write(len); s.Write(System.Text.Encoding.ASCII.GetBytes(type)); s.Write(data); s.Write(new byte[4]);
    }

    private static MetadataDistillerService NewService()
    {
        var catalog = new Mock<ILoraCatalog>();
        catalog.Setup(c => c.GetInstalledLorasAsync(It.IsAny<IReadOnlyCollection<string>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<AvailableLora>());
        return new MetadataDistillerService(new ImageResourceHasher(catalog.Object, _ => Task.FromResult<string?>(null)));
    }

    [Fact]
    public async Task DistillAsync_writes_cleaned_copy_with_parameters_and_strips_workflow()
    {
        var src = MakePng();
        var outDir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"out_{System.Guid.NewGuid():N}")).FullName;
        try
        {
            var data = new ImageGenerationData { Steps = 20, Cfg = 7, Seed = 1, SamplerName = "euler", Scheduler = "normal", Width = 512, Height = 512, HasData = true };
            var item = new DistillItem(src, data, "a cat, masterpiece", "blurry", []);
            var del = new PromptRuleSet { Kind = RuleKind.Delete, DeleteWords = ["masterpiece"] };
            var options = new DistillOptions { OutputFolder = outDir, StripWorkflow = true };

            var result = await NewService().DistillAsync([item], [del], options, progress: null, CancellationToken.None);

            result.Written.Should().Be(1);
            result.Failures.Should().BeEmpty();

            var outFile = Path.Combine(outDir, Path.GetFileName(src));
            File.Exists(outFile).Should().BeTrue();
            var chunks = PngChunkReader.ReadTextChunks(outFile);
            chunks.Should().NotContainKey("prompt");
            chunks.Should().NotContainKey("workflow");
            chunks["parameters"].Should().Contain("a cat");
            chunks["parameters"].Should().NotContain("masterpiece"); // rule applied
        }
        finally { Directory.Delete(outDir, true); File.Delete(src); }
    }

    [Fact]
    public async Task DistillAsync_deduplicates_output_names()
    {
        var a = MakePng();
        var dir = Path.GetDirectoryName(a)!;
        var sameName = Path.Combine(dir, Path.GetFileName(a)); // same file name, different source dir scenario simulated below
        var outDir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"out_{System.Guid.NewGuid():N}")).FullName;
        try
        {
            var data = new ImageGenerationData { HasData = true, Width = 8, Height = 8 };
            var item = new DistillItem(a, data, "p", null, []);
            var options = new DistillOptions { OutputFolder = outDir, StripWorkflow = false };

            // Run twice targeting the same output folder → second must not overwrite the first.
            await NewService().DistillAsync([item], [], options, null, CancellationToken.None);
            await NewService().DistillAsync([item], [], options, null, CancellationToken.None);

            Directory.GetFiles(outDir).Length.Should().Be(2);
        }
        finally { Directory.Delete(outDir, true); File.Delete(a); }
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test DiffusionNexus.Tests/DiffusionNexus.Tests.csproj --filter "FullyQualifiedName~MetadataDistillerServiceTests"`
Expected: FAIL to compile — service/records don't exist.

- [ ] **Step 3: Implement the service**

Create `DiffusionNexus.UI/Services/Distiller/MetadataDistillerService.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using DiffusionNexus.UI.Models;
using DiffusionNexus.UI.Models.Distiller;

namespace DiffusionNexus.UI.Services.Distiller;

/// <summary>One image to distill: source path, parsed+edited data, curated prompts, included LoRAs.</summary>
public sealed record DistillItem(string SourcePath, ImageGenerationData Data, string Positive, string? Negative, IReadOnlyList<LoraInfo> Loras);

/// <summary>A per-item failure captured during a run (the batch continues past it).</summary>
public sealed record DistillFailure(string SourcePath, string Error);

/// <summary>Outcome of a distill run.</summary>
public sealed record DistillResult(int Written, IReadOnlyList<DistillFailure> Failures);

/// <summary>
/// Runs the distill pipeline over a batch: apply rule sets → format A1111 parameters (optionally with
/// resource hashes) → write a clean copy (optionally workflow-stripped) into the output folder. v1
/// writes PNG output only; non-PNG sources are reported as failures.
/// </summary>
public sealed class MetadataDistillerService
{
    private readonly ImageResourceHasher _hasher;

    public MetadataDistillerService(ImageResourceHasher hasher) => _hasher = hasher;

    public async Task<DistillResult> DistillAsync(
        IReadOnlyList<DistillItem> items,
        IReadOnlyList<PromptRuleSet> ruleSets,
        DistillOptions options,
        IProgress<int>? progress,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(options.OutputFolder))
            throw new InvalidOperationException("Output folder is not set.");
        Directory.CreateDirectory(options.OutputFolder);

        var failures = new List<DistillFailure>();
        int written = 0, done = 0;

        foreach (var item in items)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (!string.Equals(Path.GetExtension(item.SourcePath), ".png", StringComparison.OrdinalIgnoreCase))
                    throw new NotSupportedException("Only PNG output is supported in this version.");

                var positive = PromptRuleEngine.Apply(item.Positive ?? "", ruleSets);
                var negativeRaw = item.Negative;
                var negative = string.IsNullOrWhiteSpace(negativeRaw) ? null : PromptRuleEngine.Apply(negativeRaw, ruleSets);

                ResourceHashes? hashes = null;
                if (options.ComputeHashes)
                    hashes = await _hasher.ComputeAsync(item.Data.Checkpoint, item.Loras, ct).ConfigureAwait(false);

                var parameters = A1111MetadataFormatter.Build(item.Data, positive, negative, item.Loras, hashes);

                var dest = UniquePath(options.OutputFolder!, Path.GetFileName(item.SourcePath));
                PngMetadataWriter.CopyWithMetadata(
                    item.SourcePath, dest,
                    new Dictionary<string, string> { ["parameters"] = parameters },
                    stripExisting: options.StripWorkflow);

                written++;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                failures.Add(new DistillFailure(item.SourcePath, ex.Message));
            }
            finally
            {
                progress?.Report(++done);
            }
        }

        return new DistillResult(written, failures);
    }

    private static string UniquePath(string folder, string fileName)
    {
        var dest = Path.Combine(folder, fileName);
        if (!File.Exists(dest)) return dest;

        var stem = Path.GetFileNameWithoutExtension(fileName);
        var ext = Path.GetExtension(fileName);
        for (int i = 2; ; i++)
        {
            var candidate = Path.Combine(folder, $"{stem} ({i}){ext}");
            if (!File.Exists(candidate)) return candidate;
        }
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test DiffusionNexus.Tests/DiffusionNexus.Tests.csproj --filter "FullyQualifiedName~MetadataDistillerServiceTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add DiffusionNexus.UI/Services/Distiller/MetadataDistillerService.cs DiffusionNexus.Tests/Distiller/MetadataDistillerServiceTests.cs
git commit -m "feat(distiller): batch orchestration service (rules + format + strip + write)"
```

---

### Task 6: `BatchMetadataDistillerViewModel` — the run VM

**Files:**
- Create: `DiffusionNexus.UI/ViewModels/Pipelines/BatchMetadataDistillerViewModel.cs`

**Interfaces:**
- Consumes: `IPipelineRun` (Task 2); `PipelineManifest`; `ILoraCatalog`, `IPipelineAssetInstaller`, `IDialogService`; Part 1 services; Task 4/5 types; `ImageMetadataParser`.
- Produces: `BatchMetadataDistillerViewModel : ViewModelBase, IPipelineRun` bound by `BatchMetadataDistillerView` (Task 7).

- [ ] **Step 1: Create the VM**

Create `DiffusionNexus.UI/ViewModels/Pipelines/BatchMetadataDistillerViewModel.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiffusionNexus.UI.Models.Distiller;
using DiffusionNexus.UI.Models.Pipelines;
using DiffusionNexus.UI.Services;
using DiffusionNexus.UI.Services.Distiller;
using DiffusionNexus.UI.Services.Lora;
using DiffusionNexus.UI.Services.Pipelines;
using Serilog;

namespace DiffusionNexus.UI.ViewModels.Pipelines;

/// <summary>
/// Run screen for the Batch Metadata Distiller: load loose images, auto-detect embedded prompts,
/// hand-curate per image, define batch-wide delete/replace rule sets, and write clean, CivitAI-readable
/// copies. Implements <see cref="IPipelineRun"/> so it can live in the Workflows gallery, but shares
/// none of the generation machinery in <see cref="PipelineRunViewModel"/>.
/// </summary>
public partial class BatchMetadataDistillerViewModel : ViewModelBase, IPipelineRun
{
    private static readonly ILogger Logger = Log.ForContext<BatchMetadataDistillerViewModel>();

    private readonly ILoraCatalog _loraCatalog;
    private readonly IDialogService? _dialogs;
    private readonly MetadataDistillerService _distiller;
    private readonly ImageMetadataParser _parser = new();
    private CancellationTokenSource? _cts;

    public string Title => "Batch Metadata Distiller";
    public event EventHandler? CloseRequested;
    public ResourceMonitorViewModel? ResourceMonitor { get; set; } // no GPU use; ignored

    public ObservableCollection<string> ImagePaths { get; } = [];
    [ObservableProperty] private string? _selectedImagePath;

    public ObservableCollection<DistillerItemViewModel> Items { get; } = [];
    [ObservableProperty] private DistillerItemViewModel? _selectedItem;

    public ObservableCollection<PromptRuleSetViewModel> RuleSets { get; } = [];

    [ObservableProperty] private bool _stripWorkflow = true;
    [ObservableProperty] private bool _computeHashes;
    [ObservableProperty] private string? _outputFolder;

    [ObservableProperty] private int _totalCount;
    [ObservableProperty] private int _withMetadataCount;
    public string DetectionSummary => $"{WithMetadataCount} / {TotalCount} images have embedded metadata";

    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private double _progress;
    [ObservableProperty] private string _statusText = "";

    public BatchMetadataDistillerViewModel(
        PipelineManifest manifest,
        ILoraCatalog loraCatalog,
        IPipelineAssetInstaller installer,
        IDialogService? dialogs = null)
    {
        _loraCatalog = loraCatalog;
        _dialogs = dialogs;
        var hasher = new ImageResourceHasher(loraCatalog, async _ => await installer.ResolveModelsRootAsync());
        _distiller = new MetadataDistillerService(hasher);

        ImagePaths.CollectionChanged += OnImagePathsChanged;
    }

    partial void OnWithMetadataCountChanged(int value) => OnPropertyChanged(nameof(DetectionSummary));
    partial void OnTotalCountChanged(int value) => OnPropertyChanged(nameof(DetectionSummary));

    partial void OnSelectedImagePathChanged(string? value) =>
        SelectedItem = Items.FirstOrDefault(i => string.Equals(i.Path, value, StringComparison.OrdinalIgnoreCase));

    private void OnImagePathsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            Items.Clear();
            RecomputeCounts();
            return;
        }

        if (e.NewItems is not null)
            foreach (string path in e.NewItems.OfType<string>())
                _ = AddItemAsync(path);

        if (e.OldItems is not null)
            foreach (string path in e.OldItems.OfType<string>())
            {
                var existing = Items.FirstOrDefault(i => string.Equals(i.Path, path, StringComparison.OrdinalIgnoreCase));
                if (existing is not null) Items.Remove(existing);
            }

        RecomputeCounts();
    }

    private async Task AddItemAsync(string path)
    {
        if (Items.Any(i => string.Equals(i.Path, path, StringComparison.OrdinalIgnoreCase))) return;

        ImageGenerationData data;
        try { data = await Task.Run(() => _parser.Parse(path)); }
        catch (Exception ex) { Logger.Warning(ex, "Distiller: parse failed for {Path}", path); return; }

        var item = new DistillerItemViewModel(path, data);
        Items.Add(item);
        if (SelectedItem is null && item.HasMetadata) { SelectedItem = item; SelectedImagePath = item.Path; }
        RecomputeCounts();
        await item.LoadThumbnailAsync();
    }

    private void RecomputeCounts()
    {
        TotalCount = Items.Count;
        WithMetadataCount = Items.Count(i => i.HasMetadata);
    }

    [RelayCommand]
    private void Back() => CloseRequested?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private void AddDeleteSet() => RuleSets.Add(new PromptRuleSetViewModel { Name = $"Delete set {RuleSets.Count + 1}", IsReplace = false });

    [RelayCommand]
    private void AddReplaceSet() => RuleSets.Add(new PromptRuleSetViewModel { Name = $"Replace set {RuleSets.Count + 1}", IsReplace = true });

    [RelayCommand]
    private void RemoveRuleSet(PromptRuleSetViewModel? set) { if (set is not null) RuleSets.Remove(set); }

    public bool CanDistill => !IsRunning && !string.IsNullOrWhiteSpace(OutputFolder) && Items.Any(i => i.IncludeInRun);

    [RelayCommand(CanExecute = nameof(CanDistill))]
    private async Task DistillAsync()
    {
        if (!CanDistill) return;

        _cts = new CancellationTokenSource();
        IsRunning = true;
        Progress = 0;
        try
        {
            var items = Items.Where(i => i.IncludeInRun)
                .Select(i => new DistillItem(i.Path, i.BuildEditedData(), i.Positive ?? "", i.Negative, i.IncludedLoras()))
                .ToList();
            var rules = RuleSets.Select(r => r.ToModel()).ToList();
            var options = new DistillOptions { StripWorkflow = StripWorkflow, ComputeHashes = ComputeHashes, OutputFolder = OutputFolder };

            var progress = new Progress<int>(done =>
                Dispatcher.UIThread.Post(() =>
                {
                    Progress = items.Count == 0 ? 0 : (double)done / items.Count * 100;
                    StatusText = $"{done} / {items.Count}";
                }));

            var result = await _distiller.DistillAsync(items, rules, options, progress, _cts.Token);

            StatusText = result.Failures.Count == 0
                ? $"Done — {result.Written} image(s) written to {OutputFolder}"
                : $"Done — {result.Written} written, {result.Failures.Count} failed";

            if (result.Failures.Count > 0 && _dialogs is not null)
                await _dialogs.ShowMessageAsync("Some images failed",
                    string.Join("\n", result.Failures.Take(20).Select(f => $"{Path.GetFileName(f.SourcePath)}: {f.Error}")));
        }
        catch (OperationCanceledException) { StatusText = "Cancelled"; }
        catch (Exception ex)
        {
            Logger.Error(ex, "Distill run failed");
            if (_dialogs is not null) await _dialogs.ShowMessageAsync("Distill failed", ex.Message);
        }
        finally
        {
            IsRunning = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    [RelayCommand]
    private void Cancel() => _cts?.Cancel();

    partial void OnIsRunningChanged(bool value) => DistillCommand.NotifyCanExecuteChanged();
    partial void OnOutputFolderChanged(string? value) => DistillCommand.NotifyCanExecuteChanged();

    public void LoadInputImages(IReadOnlyList<string> paths)
    {
        foreach (var p in paths.Where(File.Exists))
            if (!ImagePaths.Contains(p)) ImagePaths.Add(p);
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        ImagePaths.CollectionChanged -= OnImagePathsChanged;
    }
}
```

- [ ] **Step 2: Build to verify the VM compiles**

Run: `dotnet build DiffusionNexus.UI/DiffusionNexus.UI.csproj`
Expected: FAIL only where `App.axaml.cs` factory type and `BatchMetadataDistillerView` are still missing (Tasks 7–8). The VM file itself must compile; the `[RelayCommand(CanExecute = nameof(CanDistill))]` on `DistillAsync` generates `DistillCommand`, which the `OnIsRunningChanged`/`OnOutputFolderChanged` partials refresh.

- [ ] **Step 3: Commit**

```bash
git add DiffusionNexus.UI/ViewModels/Pipelines/BatchMetadataDistillerViewModel.cs
git commit -m "feat(distiller): Batch Metadata Distiller run view-model"
```

---

### Task 7: `BatchMetadataDistillerView` — three-column run screen

**Files:**
- Create: `DiffusionNexus.UI/Views/Pipelines/BatchMetadataDistillerView.axaml`
- Create: `DiffusionNexus.UI/Views/Pipelines/BatchMetadataDistillerView.axaml.cs`

**Interfaces:**
- Consumes: `BatchMetadataDistillerViewModel` (Task 6); `ImageListInputControl`.
- Produces: the run screen view; a code-behind folder-picker handler that sets `OutputFolder`.

- [ ] **Step 1: Create the view**

Create `DiffusionNexus.UI/Views/Pipelines/BatchMetadataDistillerView.axaml`:

```xml
<UserControl xmlns="https://github.com/avaloniaui"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:controls="using:DiffusionNexus.UI.Views.Controls"
             xmlns:vmp="using:DiffusionNexus.UI.ViewModels.Pipelines"
             x:Class="DiffusionNexus.UI.Views.Pipelines.BatchMetadataDistillerView"
             x:DataType="vmp:BatchMetadataDistillerViewModel">

  <Grid RowDefinitions="Auto,*" Margin="16">

    <!-- Top bar -->
    <Grid Grid.Row="0" ColumnDefinitions="Auto,*,Auto" Margin="0,0,0,12">
      <Button Grid.Column="0" Content="← Back" Command="{Binding BackCommand}" Padding="12,6"/>
      <StackPanel Grid.Column="1" Margin="14,0,0,0" VerticalAlignment="Center">
        <TextBlock Text="Batch Metadata Distiller" FontSize="18" FontWeight="Bold" Foreground="White"/>
        <TextBlock Text="Recover ComfyUI generation data → clean, CivitAI-readable images"
                   FontSize="12" Foreground="#909090"/>
      </StackPanel>
      <Border Grid.Column="2" Background="#2C2F34" CornerRadius="999" Padding="12,6" VerticalAlignment="Center"
              BorderBrush="#3A3D43" BorderThickness="1">
        <TextBlock Text="{Binding DetectionSummary}" FontSize="12.5" Foreground="#B6BAC1"/>
      </Border>
    </Grid>

    <Grid Grid.Row="1" ColumnDefinitions="300,*,340">

      <!-- LEFT: input + detection -->
      <Grid Grid.Column="0" RowDefinitions="Auto,Auto,*">
        <TextBlock Grid.Row="0" Text="INPUT IMAGES" FontSize="11" FontWeight="Bold" Foreground="#868B93" Margin="0,0,0,8"/>
        <controls:ImageListInputControl Grid.Row="1"
            ImagePaths="{Binding ImagePaths}"
            SelectedImagePath="{Binding SelectedImagePath, Mode=TwoWay}"/>
        <ScrollViewer Grid.Row="2" Margin="0,10,0,0">
          <ItemsControl ItemsSource="{Binding Items}">
            <ItemsControl.ItemTemplate>
              <DataTemplate x:DataType="vmp:DistillerItemViewModel">
                <Border Background="#26282D" CornerRadius="6" Padding="8" Margin="0,0,0,6" BorderBrush="#3A3D43" BorderThickness="1">
                  <Grid ColumnDefinitions="Auto,*,Auto">
                    <Panel Grid.Column="0" Width="9" Height="9" VerticalAlignment="Center">
                      <Ellipse Fill="#57C06A" IsVisible="{Binding HasMetadata}"/>
                      <Ellipse Fill="#5A5F66" IsVisible="{Binding !HasMetadata}"/>
                    </Panel>
                    <TextBlock Grid.Column="1" Text="{Binding FileName}" Margin="8,0" VerticalAlignment="Center"
                               FontSize="12" Foreground="#D8DADF" TextTrimming="CharacterEllipsis"/>
                    <CheckBox Grid.Column="2" IsChecked="{Binding IncludeInRun}" ToolTip.Tip="Include in run"/>
                  </Grid>
                </Border>
              </DataTemplate>
            </ItemsControl.ItemTemplate>
          </ItemsControl>
        </ScrollViewer>
      </Grid>

      <!-- MIDDLE: per-image editor -->
      <ScrollViewer Grid.Column="1" Margin="16,0">
        <StackPanel IsVisible="{Binding SelectedItem, Converter={x:Static ObjectConverters.IsNotNull}}"
                    DataContext="{Binding SelectedItem}">
          <TextBlock Text="POSITIVE PROMPT" FontSize="11" FontWeight="Bold" Foreground="#868B93" Margin="0,0,0,4"/>
          <TextBox Text="{Binding Positive}" AcceptsReturn="True" TextWrapping="Wrap" MinHeight="70"/>
          <TextBlock Text="NEGATIVE PROMPT" FontSize="11" FontWeight="Bold" Foreground="#868B93" Margin="0,10,0,4"/>
          <TextBox Text="{Binding Negative}" AcceptsReturn="True" TextWrapping="Wrap" MinHeight="48"/>

          <Grid ColumnDefinitions="*,*,*,*" Margin="0,12,0,0">
            <StackPanel Grid.Column="0" Margin="0,0,6,0">
              <TextBlock Text="STEPS" FontSize="10" Foreground="#868B93"/>
              <TextBox Text="{Binding StepsText}"/>
            </StackPanel>
            <StackPanel Grid.Column="1" Margin="0,0,6,0">
              <TextBlock Text="CFG" FontSize="10" Foreground="#868B93"/>
              <TextBox Text="{Binding CfgText}"/>
            </StackPanel>
            <StackPanel Grid.Column="2" Margin="0,0,6,0">
              <TextBlock Text="SAMPLER" FontSize="10" Foreground="#868B93"/>
              <TextBox Text="{Binding SamplerName}"/>
            </StackPanel>
            <StackPanel Grid.Column="3">
              <TextBlock Text="SCHEDULER" FontSize="10" Foreground="#868B93"/>
              <TextBox Text="{Binding Scheduler}"/>
            </StackPanel>
          </Grid>
          <Grid ColumnDefinitions="*,3*" Margin="0,8,0,0">
            <StackPanel Grid.Column="0" Margin="0,0,6,0">
              <TextBlock Text="SEED" FontSize="10" Foreground="#868B93"/>
              <TextBox Text="{Binding SeedText}"/>
            </StackPanel>
            <StackPanel Grid.Column="1">
              <TextBlock Text="MODEL" FontSize="10" Foreground="#868B93"/>
              <TextBox Text="{Binding Model}"/>
            </StackPanel>
          </Grid>

          <TextBlock Text="DETECTED LORAS · LOAD ORDER" FontSize="11" FontWeight="Bold" Foreground="#868B93" Margin="0,14,0,4"/>
          <ItemsControl ItemsSource="{Binding Loras}">
            <ItemsControl.ItemTemplate>
              <DataTemplate x:DataType="vmp:DistillerLoraViewModel">
                <Border Background="#2C2F34" CornerRadius="6" Padding="8" Margin="0,0,0,6" BorderBrush="#3A3D43" BorderThickness="1">
                  <Grid ColumnDefinitions="Auto,*,Auto,Auto">
                    <CheckBox Grid.Column="0" IsChecked="{Binding Include}"/>
                    <TextBlock Grid.Column="1" Text="{Binding Name}" Margin="8,0" VerticalAlignment="Center" Foreground="#E8E9EC"/>
                    <TextBox Grid.Column="2" Text="{Binding Strength}" Width="64" Margin="0,0,8,0"/>
                    <TextBlock Grid.Column="3" Text="{Binding SourceLabel}" VerticalAlignment="Center" FontSize="10" Foreground="#868B93"/>
                  </Grid>
                </Border>
              </DataTemplate>
            </ItemsControl.ItemTemplate>
          </ItemsControl>
        </StackPanel>
      </ScrollViewer>

      <!-- RIGHT: automation + output -->
      <ScrollViewer Grid.Column="2">
        <StackPanel Spacing="12">

          <Border Background="#26282D" CornerRadius="8" Padding="11" BorderBrush="#3A3D43" BorderThickness="1">
            <StackPanel Spacing="8">
              <Grid ColumnDefinitions="*,Auto,Auto">
                <TextBlock Grid.Column="0" Text="Rule sets" FontWeight="SemiBold" Foreground="White" VerticalAlignment="Center"/>
                <Button Grid.Column="1" Content="+ Delete" Command="{Binding AddDeleteSetCommand}" Margin="0,0,6,0" FontSize="11" Padding="8,3"/>
                <Button Grid.Column="2" Content="+ Replace" Command="{Binding AddReplaceSetCommand}" FontSize="11" Padding="8,3"/>
              </Grid>
              <ItemsControl ItemsSource="{Binding RuleSets}">
                <ItemsControl.ItemTemplate>
                  <DataTemplate x:DataType="vmp:PromptRuleSetViewModel">
                    <Border Background="#2C2F34" CornerRadius="6" Padding="8" Margin="0,0,0,6" BorderBrush="#3A3D43" BorderThickness="1">
                      <StackPanel Spacing="6">
                        <Grid ColumnDefinitions="Auto,*,Auto">
                          <CheckBox Grid.Column="0" IsChecked="{Binding Enabled}"/>
                          <TextBox Grid.Column="1" Text="{Binding Name}" Margin="6,0" Watermark="Set name"/>
                          <Button Grid.Column="2" Content="✕" FontSize="11" Padding="6,2"
                                  Command="{Binding $parent[ItemsControl].DataContext.RemoveRuleSetCommand}"
                                  CommandParameter="{Binding}"/>
                        </Grid>
                        <TextBox Text="{Binding WordsText}" AcceptsReturn="True" TextWrapping="Wrap" MinHeight="48"
                                 Watermark="Delete: word, word — Replace: from =&gt; to (one per line)"/>
                      </StackPanel>
                    </Border>
                  </DataTemplate>
                </ItemsControl.ItemTemplate>
              </ItemsControl>
            </StackPanel>
          </Border>

          <Border Background="#26282D" CornerRadius="8" Padding="11" BorderBrush="#3A3D43" BorderThickness="1">
            <StackPanel Spacing="8">
              <TextBlock Text="Output" FontWeight="SemiBold" Foreground="White"/>
              <CheckBox Content="Strip ComfyUI workflow" IsChecked="{Binding StripWorkflow}"/>
              <CheckBox Content="Compute resource hashes (slower)" IsChecked="{Binding ComputeHashes}"/>
              <TextBlock Text="OUTPUT FOLDER" FontSize="10" Foreground="#868B93" Margin="0,4,0,0"/>
              <Grid ColumnDefinitions="*,Auto">
                <TextBox Grid.Column="0" Text="{Binding OutputFolder}" IsReadOnly="True" Watermark="Choose a folder…"/>
                <Button Grid.Column="1" Content="Browse…" Margin="6,0,0,0" Click="OnBrowseOutputFolder"/>
              </Grid>
            </StackPanel>
          </Border>

          <Button Content="Distill" Command="{Binding DistillCommand}"
                  Background="#35C2B0" Foreground="#0C1413" FontWeight="Bold" Padding="0,11"
                  HorizontalAlignment="Stretch" HorizontalContentAlignment="Center"/>
          <ProgressBar Value="{Binding Progress}" Maximum="100" IsVisible="{Binding IsRunning}"/>
          <Button Content="Cancel" Command="{Binding CancelCommand}" IsVisible="{Binding IsRunning}" HorizontalAlignment="Center"/>
          <TextBlock Text="{Binding StatusText}" Foreground="#B6BAC1" FontSize="12" TextWrapping="Wrap"/>
        </StackPanel>
      </ScrollViewer>

    </Grid>
  </Grid>
</UserControl>
```

- [ ] **Step 2: Create the code-behind (folder picker)**

Create `DiffusionNexus.UI/Views/Pipelines/BatchMetadataDistillerView.axaml.cs`:

```csharp
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using DiffusionNexus.UI.ViewModels.Pipelines;

namespace DiffusionNexus.UI.Views.Pipelines;

public partial class BatchMetadataDistillerView : UserControl
{
    public BatchMetadataDistillerView() => InitializeComponent();

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private async void OnBrowseOutputFolder(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not BatchMetadataDistillerViewModel vm) return;
        var top = TopLevel.GetTopLevel(this);
        if (top is null) return;

        var folders = await top.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Choose output folder",
            AllowMultiple = false,
        });

        var path = folders.FirstOrDefault()?.TryGetLocalPath();
        if (!string.IsNullOrEmpty(path)) vm.OutputFolder = path;
    }
}
```

- [ ] **Step 3: Build to verify the view + VM compile together**

Run: `dotnet build DiffusionNexus.UI/DiffusionNexus.UI.csproj`
Expected: FAIL only in `App.axaml.cs` (factory delegate type / missing case — Task 8). Everything else compiles, including `PipelinesView.axaml` now that `BatchMetadataDistillerView` exists.

- [ ] **Step 4: Commit**

```bash
git add DiffusionNexus.UI/Views/Pipelines/BatchMetadataDistillerView.axaml DiffusionNexus.UI/Views/Pipelines/BatchMetadataDistillerView.axaml.cs
git commit -m "feat(distiller): three-column run screen view"
```

---

### Task 8: Wire the DI factory + final verification

**Files:**
- Modify: `DiffusionNexus.UI/App.axaml.cs:1052-1058` and `:1064`

**Interfaces:**
- Consumes: `BatchMetadataDistillerViewModel` (Task 6); `IPipelineRun` (Task 2).
- Produces: the gallery factory now builds the distiller run VM; the whole solution compiles and runs.

- [ ] **Step 1: Retype the factory delegate + add the case**

In `DiffusionNexus.UI/App.axaml.cs`, replace the factory registration (lines ~1052-1058) with:

```csharp
        services.AddTransient<Func<PipelineTileViewModel, ViewModels.Pipelines.IPipelineRun>>(sp => tile =>
            tile.Id switch
            {
                "anime-to-real" => ActivatorUtilities.CreateInstance<ViewModels.Pipelines.AnimeToRealPipelineRunViewModel>(sp, tile.Manifest),
                "image-to-image" => ActivatorUtilities.CreateInstance<ViewModels.Pipelines.ImageToImagePipelineRunViewModel>(sp, tile.Manifest),
                "batch-metadata-distiller" => ActivatorUtilities.CreateInstance<ViewModels.Pipelines.BatchMetadataDistillerViewModel>(sp, tile.Manifest),
                _ => throw new NotSupportedException($"No run UI is registered for pipeline '{tile.Id}'."),
            });
```

- [ ] **Step 2: Retype the factory resolution in the PipelinesViewModel registration**

In the same file (line ~1064), change:

```csharp
            sp.GetService<Func<PipelineTileViewModel, ViewModels.Pipelines.PipelineRunViewModel>>(),
```

to:

```csharp
            sp.GetService<Func<PipelineTileViewModel, ViewModels.Pipelines.IPipelineRun>>(),
```

- [ ] **Step 3: Build the whole UI project**

Run: `dotnet build DiffusionNexus.UI/DiffusionNexus.UI.csproj`
Expected: Build succeeded, 0 errors.

- [ ] **Step 4: Run the full test suite**

Run: `dotnet test DiffusionNexus.Tests/DiffusionNexus.Tests.csproj`
Expected: All tests PASS (Part 1 + Part 2 Distiller tests, plus the pre-existing suite).

- [ ] **Step 5: Manual verification (drive the real app)**

Launch the app (per the project's run method). Then confirm:
1. Open the **Workflows** module. The gallery shows the generation tiles, a **labelled "Utilities" divider (horizontal line)**, and the **Batch Metadata Distiller** tile below it — with a "Ready" badge (no ComfyUI install required to open it).
2. Click the tile → the three-column run screen opens (it must NOT show a "No ComfyUI installation" dialog).
3. Drag in a folder of ComfyUI PNGs. The top bar tally updates ("N / M images have embedded metadata"); list items badge green/grey.
4. Click an image with LoRAs → the middle panel fills prompt/CFG/steps/sampler and lists the detected LoRAs in load order (verify a Power Lora Loader / Lora Stack image resolves its LoRAs).
5. Add a Delete rule set (e.g. `masterpiece`) and a Replace set (e.g. `1girl => woman`); toggle "Strip ComfyUI workflow"; pick an output folder; click **Distill**.
6. Open an output PNG's metadata (in the app's metadata panel or CivitAI): confirm the `parameters` are present with `<lora:...>` tokens, the blacklist word is gone, `1girl` became `woman`, and — with strip on — the embedded ComfyUI `prompt`/`workflow` are gone.

- [ ] **Step 6: Commit**

```bash
git add DiffusionNexus.UI/App.axaml.cs
git commit -m "feat(distiller): register run factory + finish gallery wiring"
```

---

## Feature complete

Both parts merged on `feature/batch-metadata-distiller`: the core engine (Part 1) plus the gallery tile, labelled divider, and three-column run screen (Part 2). Next step per the team convention: open a PR against `develop` (never commit to `develop` directly).
