using DiffusionNexus.Inference.Models;
using DiffusionNexus.Inference.StableDiffusionCpp;
using FluentAssertions;

namespace DiffusionNexus.Tests.Inference;

public class StableDiffusionCppLoaderTests
{
    private static readonly Type LoaderType =
        typeof(ModelDescriptor).Assembly.GetType(
            "DiffusionNexus.Inference.StableDiffusionCpp.StableDiffusionCppLoader",
            throwOnError: true)!;

    private static readonly System.Reflection.MethodInfo BuildMethod =
        LoaderType.GetMethod(
            "Build",
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static |
            System.Reflection.BindingFlags.NonPublic)!;

    private static object Build(ModelDescriptor descriptor) =>
        BuildMethod.Invoke(null, new object[] { descriptor })!;

    private static InvocationResult InvokeBuild(ModelDescriptor descriptor)
    {
        try
        {
            return new InvocationResult(Build(descriptor), null);
        }
        catch (System.Reflection.TargetInvocationException tie)
        {
            return new InvocationResult(null, tie.InnerException ?? tie);
        }
    }

    private record InvocationResult(object? Value, Exception? Error);

    [Fact]
    public void Build_NullDescriptor_Throws()
    {
        var result = InvokeBuild(null!);
        result.Error.Should().BeOfType<ArgumentNullException>();
    }

    [Fact]
    public void Build_UnsupportedKind_ThrowsNotSupported()
    {
        var descriptor = new ModelDescriptor
        {
            Key = "x",
            DisplayName = "x",
            Kind = (ModelKind)999,
        };

        var result = InvokeBuild(descriptor);

        result.Error.Should().BeOfType<NotSupportedException>();
        result.Error!.Message.Should().Contain("999");
    }

    [Fact]
    public void Build_ZImageTurbo_MissingDiffusionModelPath_Throws()
    {
        var descriptor = new ModelDescriptor
        {
            Key = ModelKeys.ZImageTurbo,
            DisplayName = "Z",
            Kind = ModelKind.ZImageTurbo,
            DiffusionModelPath = null,
            VaePath = "vae.safetensors",
            TextEncoders = new Dictionary<TextEncoderSlot, string>
            {
                [TextEncoderSlot.Llm] = "llm.safetensors"
            }
        };

        var result = InvokeBuild(descriptor);

        result.Error.Should().BeOfType<InvalidOperationException>();
        result.Error!.Message.Should().Contain("DiffusionModelPath");
    }

    [Fact]
    public void Build_ZImageTurbo_MissingLlmTextEncoder_Throws()
    {
        var descriptor = new ModelDescriptor
        {
            Key = ModelKeys.ZImageTurbo,
            DisplayName = "Z",
            Kind = ModelKind.ZImageTurbo,
            DiffusionModelPath = "unet.safetensors",
            VaePath = "vae.safetensors",
            TextEncoders = new Dictionary<TextEncoderSlot, string>
            {
                [TextEncoderSlot.ClipL] = "clip-l.safetensors"
            }
        };

        var result = InvokeBuild(descriptor);

        result.Error.Should().BeOfType<InvalidOperationException>();
        result.Error!.Message.Should().Contain("LLM");
    }

    [Fact]
    public void Build_ZImageTurbo_MissingVae_Throws()
    {
        var descriptor = new ModelDescriptor
        {
            Key = ModelKeys.ZImageTurbo,
            DisplayName = "Z",
            Kind = ModelKind.ZImageTurbo,
            DiffusionModelPath = "unet.safetensors",
            VaePath = "  ",
            TextEncoders = new Dictionary<TextEncoderSlot, string>
            {
                [TextEncoderSlot.Llm] = "llm.safetensors"
            }
        };

        var result = InvokeBuild(descriptor);

        result.Error.Should().BeOfType<InvalidOperationException>();
        result.Error!.Message.Should().Contain("VAE");
    }

    [Fact]
    public void Build_ZImageTurbo_AllSlotsPresent_ReturnsParameterObject()
    {
        var descriptor = new ModelDescriptor
        {
            Key = ModelKeys.ZImageTurbo,
            DisplayName = "Z",
            Kind = ModelKind.ZImageTurbo,
            DiffusionModelPath = "unet.safetensors",
            VaePath = "vae.safetensors",
            TextEncoders = new Dictionary<TextEncoderSlot, string>
            {
                [TextEncoderSlot.Llm] = "llm.safetensors"
            }
        };

        var result = InvokeBuild(descriptor);

        result.Error.Should().BeNull();
        result.Value.Should().NotBeNull();
    }
}
