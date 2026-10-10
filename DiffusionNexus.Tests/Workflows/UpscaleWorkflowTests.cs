using System.Text.Json.Nodes;
using FluentAssertions;

namespace DiffusionNexus.Tests.Workflows;

public class UpscaleWorkflowTests
{
    private static JsonObject Load(string name) =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Assets", "Workflows", name)))!.AsObject();

    private static IEnumerable<string> Links(JsonObject workflow) =>
        workflow.SelectMany(n => n.Value!["inputs"]!.AsObject())
            .Where(i => i.Value is JsonArray { Count: 2 })
            .Select(i => i.Value![0]!.GetValue<string>());

    // #608: the rgthree loader only carried a disabled leftover LoRA, and the Engine never installs rgthree.
    [Fact]
    public void Upscale_HasNoRgthreeNode_AndEveryLinkPointsAtANodeThatExists()
    {
        var workflow = Load("Z-Image-Turbo-Upscale.json");

        workflow.Select(n => n.Value!["class_type"]!.GetValue<string>()).Should().NotContain(t => t.Contains("rgthree"));
        Links(workflow).Should().OnlyContain(id => workflow.ContainsKey(id));
        workflow["39"]!["inputs"]!["model"]!.ToJsonString().Should().Be("""["28",0]""");
        workflow["17"]!["inputs"]!["clip"]!.ToJsonString().Should().Be("""["27",0]""");
        workflow["35"]!["inputs"]!["clip"]!.ToJsonString().Should().Be("""["27",0]""");
    }

    [Fact]
    public void Describe_RunsTheGgufNodeOnA1280pxCopy_AndShowsTheText()
    {
        var workflow = Load("Qwen3-VL-Describe.json");

        workflow["1"]!["class_type"]!.GetValue<string>().Should().Be("LoadImage");
        workflow["2"]!["inputs"]!["largest_size"]!.GetValue<int>().Should().Be(1280);
        workflow["3"]!["class_type"]!.GetValue<string>().Should().Be("SimpleQwenVLggufV2");
        workflow["3"]!["inputs"]!["bypass"]!.GetValue<bool>().Should().BeFalse("the node rejects a prompt without it");
        workflow["3"]!["inputs"]!["image"]!.ToJsonString().Should().Be("""["2",0]""");
        workflow["4"]!["class_type"]!.GetValue<string>().Should().Be("ShowText|pysssss");
        Links(workflow).Should().OnlyContain(id => workflow.ContainsKey(id));
    }

    // Step 1 decides from the failing node's type whether Qwen3-VL may still be loaded; the names must match the workflow.
    [Fact]
    public void Describe_TheNodeTypesStepOneKnows_AreTheWorkflowsOwn()
    {
        var workflow = Load("Qwen3-VL-Describe.json");
        string TypeOf(string id) => workflow[id]!["class_type"]!.GetValue<string>();

        TypeOf(DiffusionNexus.UI.Services.Vision.ImageDescriber.DescribeNodeId)
            .Should().Be(DiffusionNexus.UI.Services.Vision.ImageDescriber.DescribeNodeType);
        DiffusionNexus.UI.Services.Vision.ImageDescriber.NodesBeforeTheDescriber
            .Should().BeEquivalentTo([TypeOf("1"), TypeOf("2")]);
        DiffusionNexus.UI.Services.Vision.ImageDescriber.NodesAfterTheDescriber
            .Should().BeEquivalentTo([TypeOf("4")]);
    }

    // Checked in the source tree: an incremental build leaves a deleted workflow's old copy in bin.
    [Fact]
    public void TheVisionUpscaleWorkflow_IsGone()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "DiffusionNexus.sln")))
            dir = dir.Parent;
        dir.Should().NotBeNull("the tests run inside the repository");

        File.Exists(Path.Combine(dir!.FullName, "DiffusionNexus.Service", "Assets", "Workflows", "Vision-Z-Image-Turbo-Upscale.json"))
            .Should().BeFalse("Vision runs the describe workflow, then the normal upscale workflow");
    }
}
