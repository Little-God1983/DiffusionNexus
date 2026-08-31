using DiffusionNexus.Domain.Enums;

namespace DiffusionNexus.Service.Services.Sync.Identity;

/// <summary>
/// Names what a safetensors container actually is from its TENSOR KEYS — a reading of the weights,
/// not a guess about the file name.
/// </summary>
/// <remarks>
/// This is the rung that makes #527 safe to act on. The sorter physically relocates files off this
/// verdict, and a LoRA called <c>vae_finetune_lora</c> must not be filed as a VAE because of how
/// its author named it. The keys cannot lie about this: a LoRA carries <c>lora_up</c>/<c>lora_A</c>
/// pairs, an autoencoder carries <c>post_quant_conv</c>, a text encoder carries its encoder stack
/// at the ROOT of its key paths (<c>text_model.…</c>, <c>model.layers.…</c>) where a checkpoint
/// that merely bundles one nests it a level down.
/// <para>
/// There is deliberately no upscaler rung: ESRGAN-family upscalers ship as <c>.pth</c> pickles with
/// no readable header at all, so a rule for them here would never fire. They are named by
/// <see cref="AssetKindClassifier"/> instead.
/// </para>
/// <para>
/// Order is load-bearing. A LoRA trained on the text encoder carries both LoRA markers and
/// encoder-shaped key names, so the LoRA evidence is checked first: what the file IS outranks what
/// it was trained on, exactly as <see cref="BaseModelHeaderMap"/> checks its name hint before the
/// architecture that every SDXL refinement shares.
/// </para>
/// <para>
/// Every rung BELOW the LoRA rung assumes ONE purpose per container, so a composite-container guard
/// sits near the top and excuses this map from the files where that assumption does not hold
/// — a full checkpoint, which bundles a UNet, a VAE and a text encoder together. It answers null
/// rather than a kind; see the comment on it for why. It sits below the LoRA rung and not above it
/// because the risk is one-sided: a LoRA whose keys happen to carry a checkpoint-shaped prefix would
/// lose its weights verdict and be handed to the name rung, while the reverse cannot happen — a
/// genuine composite checkpoint carries no <c>lora_up</c>, <c>lora_te</c> or <c>.alpha</c> keys, so
/// the LoRA rung has nothing to mis-fire on.
/// </para>
/// <para>
/// One rung sits between those two, and it is the only UNIVERSAL test here — every other rung asks
/// whether ANY sampled key carries a needle, this one asks whether ALL of them do. It earns that
/// position on the same argument the LoRA rung does: if every key belongs to one named component,
/// the container IS that component and cannot be composite, whatever prefix those keys happen to
/// carry. Without it a standalone LTX embeddings connector — 59 tensors, all of them connector,
/// written under <c>model.diffusion_model.</c> — is read as a bundled checkpoint by the guard.
/// </para>
/// </remarks>
public static class AssetKindHeaderMap
{
    // Rung 1 — LoRA. Checked first; see class remarks. ".alpha" is matched as a SUFFIX because it
    // is the per-module scale a LoRA writes beside each up/down pair, and as a substring it would
    // hit any tensor whose path merely contains the letters.
    //
    // Both SPELLINGS of the up/down pair are needed. kohya and PEFT write "lora_up"/"lora_down";
    // diffusers before v0.21 wrote "lora.down.weight"/"lora.up.weight" with dots, and its attention
    // processors spelled them "…to_q_lora.down.weight". Missing the dotted form was not a missed
    // detection but a WRONG one: an old-format LoRA that also trained the text encoder carries
    // "text_encoder.text_model.encoder.layers.…lora.down.weight", so it fell past this rung into the
    // TextEncoder rung, whose needle those keys match — a real LoRA stamped TextEncoder from its
    // weights, which every guard on this feature trusts, leaving it invisible in the Viewer and
    // unselectable by any bulk sync.
    //
    // There is no dotted A/B pair to add: the A/B naming arrives with PEFT's lora_A/lora_B module
    // dict, which serializes with underscores, while the dot-spelled legacy format only ever named
    // its pair up/down. A needle for a spelling no tool writes is a needle that can only misfire.
    //
    // "lora_linear_layer" is a THIRD spelling and is NOT redundant with either pair — do not remove
    // it as such. Legacy diffusers patched the text encoder through PatchedLoraProjection, which
    // holds the adapter as an attribute of that name, so its keys read
    // "…q_proj.lora_linear_layer.down.weight": the segment sits BETWEEN "lora" and "down", so the
    // key contains neither "lora_down" nor "lora.down" and both pairs above miss it. It then matches
    // the TextEncoder rung, stamping a real LoRA TextEncoder from its weights.
    //
    // The file's own UNet keys ("unet.…processor.to_q_lora.down.weight") would otherwise rescue it,
    // but they cannot be relied on: SafetensorsHeaderReader samples only the first
    // MaxSampledTensorKeys root properties in file order, and "text_encoder…" sorts before "unet…",
    // so an alphabetically written header can present 64 text-encoder keys and no UNet key at all.
    // Same sampling hazard the composite guard below exists for, reached from the other side.
    //
    // Normalizing "." to "_" before matching would collapse the two up/down spellings into one
    // needle (it would not help this one) and is deliberately NOT done: the VAE rung's
    // "encoder.down."/"decoder.up.", the TextEncoder rung's "text_model.encoder.layers" and the
    // ".alpha" suffix rule all depend on the dots being real.
    private static readonly string[] LoraNeedles =
    {
        "lora_up", "lora_down", "lora.up", "lora.down", "lora_linear_layer",
        "lora_a.", "lora_b.", "lora_unet", "lora_te",
    };

    private const string LoraAlphaSuffix = ".alpha";

    // Rung 2 — whole-container component, and the ONLY rung in this class with a UNIVERSAL
    // quantifier. Every other rung is existential ("does ANY sampled key contain this needle"),
    // which is right for a marker that identifies a container by a part unique to it. These markers
    // are not that: a full LTX checkpoint contains an embeddings connector, so "any key mentions a
    // connector" would file somebody's checkpoint as a text encoder off one embedded part. What
    // distinguishes the standalone component is that there is nothing ELSE in the container — so the
    // test is that EVERY sampled key belongs to one of these components. A checkpoint that merely
    // holds a connector also holds transformer, patchify and VAE keys, and fails that test on the
    // first of them.
    //
    // It sits above the composite guard for the same reason the LoRA rung does, and the reason is
    // the same sentence: if every key belongs to one named component, the container IS that
    // component and cannot be composite, whatever prefix those keys happen to carry. That prefix is
    // the whole bug. ltx-2-19b-embeddings_connector_dev_bf16.safetensors is 59 tensors, every one of
    // them a connector — but the audio and video halves are written under "model.diffusion_model.",
    // so the guard read a 59-tensor component as a bundled checkpoint, answered null, and dropped it
    // on the name rung and the LORA default.
    //
    // The quantifier is over the marker SET, not one marker: that same file is 29 keys of
    // "audio_embeddings_connector", 29 of "video_embeddings_connector" and one trailing
    // "text_embedding_projection.aggregate_embed.weight". No single needle is in all 59, so a
    // per-needle universal test would name none of it. "embeddings_connector" is deliberately
    // unprefixed so it spans the audio and video halves.
    //
    // ltx-2.3_text_projection_bf16.safetensors and ltx-2.3-22b-dev_embeddings_connectors.safetensors
    // are the other shape: 4 tensors, all "text_embedding_projection.", no checkpoint prefix at all
    // and no needle anywhere in the map that reached them.
    //
    // TextEncoder rather than a kind of its own because that is what these components do — they
    // project text (and the pooled audio/video embeddings that ride alongside it) into the
    // transformer's conditioning space — and because a kind this map returns must be one the rest of
    // the app can name; see AllKinds.
    private static readonly string[] WholeContainerComponentNeedles =
    {
        "embeddings_connector", "text_embedding_projection",
    };

    // Rung 3 — composite container. Every rung below assumes ONE purpose per file, and a full
    // checkpoint breaks that assumption outright: it is a UNet, a VAE and a text encoder in one
    // container. SafetensorsHeaderReader samples only the first MaxSampledTensorKeys root
    // properties in file order, so for an alphabetically-keyed checkpoint that sample can be
    // entirely "cond_stage_model.transformer.text_model.…" — which hits the TextEncoder rung — while
    // another ordering lands the "first_stage_model.…encoder.down." block and hits VAE. Both are
    // confident, both are wrong, and either would move the user's checkpoint into a support-asset
    // folder.
    //
    // These three prefixes are the CompVis/A1111 state-dict layout that only a bundled checkpoint
    // has; nothing that is only a VAE, only an encoder, or only a LoRA carries them (ComfyUI-format
    // LoRAs use a bare "diffusion_model." with no "model." ahead of it, which is why the needle
    // keeps its prefix).
    private static readonly string[] CompositeCheckpointNeedles =
    {
        "model.diffusion_model.", "first_stage_model.", "cond_stage_model.",
    };

    // Rung 4 — autoencoder. "post_quant_conv"/"quant_conv" are unique to a VAE's latent bottleneck;
    // the down/up block paths are the encoder and decoder stacks either side of it.
    private static readonly string[] VaeNeedles =
    {
        "post_quant_conv", "quant_conv", "encoder.down.", "decoder.up.",
    };

    // Rung 5 — ControlNet. "control_model." is the prefix a bundled ControlNet carries;
    // "controlnet_cond_embedding" and "input_hint_block" are the hint-conditioning stem that only
    // a ControlNet has.
    private static readonly string[] ControlNetNeedles =
    {
        "control_model.", "controlnet_cond_embedding", "input_hint_block",
    };

    // Rung 6 — text encoder, matched as a key ROOT PREFIX and never as a substring. Each entry
    // names the stack a STANDALONE encoder writes at the top of its key path: HuggingFace
    // CLIPTextModel ("text_model.…"), and the HuggingFace CAUSAL-LM layout used by a decoder-only
    // LLM shipped as a prompt encoder (Gemma, Llama, Qwen 3 / Qwen-VL, Mistral, ERNIE), which
    // writes "model.layers.N.…" — or, in the multimodal spelling, "language_model.layers.N.…" —
    // and carries neither logit_scale nor shared.weight, so the exact-key table below cannot
    // reach it.
    //
    // ANCHORING IS THE WHOLE DESIGN, and both halves of this table were paid for by real
    // checkpoints that substring needles mis-filed. The property that makes a container a
    // standalone encoder is not that an encoder key appears SOMEWHERE in it — every image
    // checkpoint bundles a text encoder — it is that the encoder stack sits at the ROOT of the key
    // path, with no component prefix ahead of it:
    //
    //   standalone HF CLIP  "text_model.encoder.layers.0.layer_norm1.bias"
    //   SDXL checkpoint     "conditioner.embedders.0.transformer.text_model.encoder.layers.0.…"
    //   standalone LLM      "model.layers.0.mlp.gate_proj.weight"
    //   HiDream checkpoint  "model.language_model.layers.0.mlp.gate_proj.weight"
    //   Chatterbox (TTS)    "tfmr.layers.0.mlp.gate_proj.weight"
    //
    // Why the composite guard above cannot cover for a substring needle here: that guard also sees
    // only the sampled window. A 2515-tensor SDXL checkpoint presents its conditioner block FIRST
    // — 64 keys of "conditioner.embedders.0.transformer.text_model.…" and not one
    // "first_stage_model."/"model.diffusion_model." key among them, though the file carries both
    // further down — so the guard finds nothing and a free "text_model.encoder.layers" fires.
    // Two real 13.8 GB Pony checkpoints were named TextEncoder exactly that way, i.e. support
    // assets: gone from the Viewer, unselectable by bulk sync, and handed to the sorter as files to
    // physically move. The same substring route had already cost two HiDream checkpoints, through
    // "model.embed_tokens" matching inside "model.language_model.embed_tokens".
    //
    // Adding "conditioner.embedders." to CompositeCheckpointNeedles instead is the alternative and
    // is a losing game: every architecture invents its own layout, so that route needs a fresh
    // exception per checkpoint family, forever. Anchoring costs nothing and needs no list of the
    // things it excludes — and it is not a heuristic but a restatement of the format: a safetensors
    // key set is the flattened module path of the SAVED TOP-LEVEL OBJECT, so anything the container
    // merely bundles is reached through the attribute name holding it and therefore sits at depth 2
    // or deeper. "The encoder stack is at depth 1" is thus not evidence ABOUT the container, it is
    // the same statement as "the saved object IS the encoder", and it holds however the 64-key
    // window happens to fall.
    //
    // Measured over the 1553 readable containers of one real library, anchoring changes exactly two
    // verdicts against the substring version — the two Pony checkpoints — and leaves all 28 real
    // text encoders, all 1435 LoRAs and all 9 VAEs where they were. Every prefix is load-bearing
    // over that sweep, measured by dropping each one: without "text_model." the four CLIP encoders
    // go unnamed; without "model.layers." eighteen files do (fifteen LLM encoders plus the three
    // LLaVA decoder shards); without "language_model.layers." qwen3vl_8b_fp8-nf4 does. A rooted
    // "token_embedding." and a rooted "model.embed_tokens" would both be safe here too, but each is
    // redundant on every one of those 1553 files, so neither is carried: a needle that earns
    // nothing can only misfire.
    //
    // This is an existential test, unlike rung 2's, and deliberately: rung 2's universal quantifier
    // cannot be reused here. It is guarded on an UNTRUNCATED sample, and every one of the LLM files
    // is 237–2417 tensors and fills the 64-key cap, so a universal rung would name none of them.
    // Nor is dropping that cap an escape: a universal test excludes HiDream only on the two
    // "model.final_layer2." keys that happen to sort into the window, where anchoring excludes it
    // on every key it has.
    private static readonly string[] TextEncoderRootPrefixes =
    {
        "text_model.", "model.layers.", "language_model.layers.",
    };

    // Two WHOLE keys rather than path fragments, so they are matched exactly: "shared.weight" is
    // T5's tied embedding table and "logit_scale" is CLIP's learned temperature. Either as a
    // substring would hit a bundled copy of the same component one path segment down — the same
    // hazard the prefix table above exists for, which is why neither is relaxed into one.
    private static readonly string[] TextEncoderExactKeys =
    {
        "logit_scale", "shared.weight",
    };

    /// <summary>
    /// Every kind this map can ever return. Test-only seam (DiffusionNexus.Tests,
    /// InternalsVisibleTo) — mirrors <see cref="BaseModelHeaderMap.AllLabels"/>.
    /// </summary>
    internal static IReadOnlyCollection<ModelType> AllKinds { get; } =
        [ModelType.LORA, ModelType.VAE, ModelType.Controlnet, ModelType.TextEncoder];

    /// <summary>What the tensor keys say this file is, or null when they say nothing usable.</summary>
    public static ModelType? Map(SafetensorsHeaderInfo? info)
    {
        if (info is null) return null;

        var keys = info.Keys;
        if (keys.Count == 0) return null;

        var lowered = new string[keys.Count];
        for (var i = 0; i < keys.Count; i++)
            lowered[i] = keys[i].ToLowerInvariant();

        // Rung 1 — LoRA, and it runs before both rungs below it, which the class remarks call
        // load-bearing in BOTH directions. What a file IS outranks what it was trained on, so a
        // LoRA trained on the text encoder is a LoRA and never a text encoder. And the ordering
        // against the composite guard is safe only this way round, because the risk there is
        // one-sided: a LoRA whose keys happen to carry a checkpoint-shaped prefix would lose its
        // weights verdict to a guard meant for bundled checkpoints and fall to the name rung — the
        // guess this whole class exists to pre-empt — whereas a genuine composite checkpoint carries
        // no lora_up, lora_te or ".alpha" keys, so this rung cannot mis-fire on one. The same
        // asymmetry holds against rung 2: a LoRA trained on nothing but an embeddings connector has
        // every key inside that connector and would pass its universal test, while a genuine
        // connector carries no up/down pair for this rung to mis-fire on.
        foreach (var key in lowered)
        {
            if (key.EndsWith(LoraAlphaSuffix, StringComparison.Ordinal)) return ModelType.LORA;
            if (ContainsAny(key, LoraNeedles)) return ModelType.LORA;
        }

        // Rung 2 — whole-container component, and the only UNIVERSAL test in this class: if EVERY
        // sampled key belongs to one named component, the container IS that component and cannot be
        // composite, whatever prefix those keys carry. That is the same argument that puts the LoRA
        // rung first, which is why this one sits above the composite guard rather than below it: the
        // guard would otherwise read a 59-tensor LTX connector written under "model.diffusion_model."
        // as a bundled checkpoint and drop it on the name rung. Existential markers cannot make this
        // claim — a checkpoint that merely CONTAINS a connector matches those — so this rung must
        // stay universal; see the needle table for the full argument.
        if (IsWholeContainerComponent(lowered)) return ModelType.TextEncoder;

        // Rung 3 answers NULL, never a ModelType — deliberately. Returning ModelType.Checkpoint
        // here would be a truer statement about the file and a worse thing to do: it would create a
        // second class of row that silently vanishes from the Viewer (ModelFileSyncService's
        // IsLoraFamily) and from every bulk sync (SyncStateRepository's LoraFamily filter), which is
        // exactly the disappearance #527's §5 exists to stop causing. Null falls through to the name
        // rung, which is where a checkpoint has always been decided, so this rung only ever REMOVES
        // a wrong confident answer.
        foreach (var key in lowered)
        {
            if (ContainsAny(key, CompositeCheckpointNeedles)) return null;
        }

        foreach (var key in lowered)
        {
            if (ContainsAny(key, VaeNeedles)) return ModelType.VAE;
        }

        foreach (var key in lowered)
        {
            if (ContainsAny(key, ControlNetNeedles)) return ModelType.Controlnet;
        }

        foreach (var key in lowered)
        {
            // Matched as a PREFIX, never as a substring: a standalone encoder writes its stack at
            // the root of the key path, while a checkpoint that bundles one nests it under a
            // component name ("conditioner.embedders.0.transformer.text_model.…",
            // "model.language_model.layers.…"). The nesting IS the evidence, and a substring match
            // throws exactly that away — see the table for the checkpoints it cost.
            if (StartsWithAny(key, TextEncoderRootPrefixes)) return ModelType.TextEncoder;

            foreach (var exact in TextEncoderExactKeys)
            {
                if (string.Equals(key, exact, StringComparison.Ordinal)) return ModelType.TextEncoder;
            }
        }

        return null;
    }

    /// <summary>
    /// True when EVERY sampled key belongs to one of the named components — i.e. the container holds
    /// that component and nothing else. The universal quantifier is the whole point: see rung 2.
    /// </summary>
    private static bool IsWholeContainerComponent(string[] lowered)
    {
        // "All of them" over nothing is vacuously true, and a rung that fires on an empty sample
        // would name every unreadable header a text encoder. Map already returns early on an empty
        // key list, but a universal quantifier is not a thing to leave guarded from a distance.
        if (lowered.Length == 0) return false;

        // The claim is about the WHOLE container and the evidence is a SAMPLE — the first
        // MaxSampledTensorKeys root properties in file order. A sample that fills the cap says
        // nothing about the keys past it, so a container that reaches it is not eligible however
        // uniform its first 64 keys look: a checkpoint whose connector block happened to lead its
        // header would otherwise be named after that block, which is the truncation hazard the
        // composite guard exists for, reached through this rung and past it. Erring this way loses
        // nothing that was ever working — an ineligible container falls to the guard and the name
        // rung, exactly where it fell before this rung existed — while erring the other way moves a
        // user's checkpoint into a support-asset folder. Both real connectors are 59 and 4 tensors.
        if (lowered.Length >= SafetensorsHeaderReader.MaxSampledTensorKeys) return false;

        foreach (var key in lowered)
        {
            if (!ContainsAny(key, WholeContainerComponentNeedles)) return false;
        }

        return true;
    }

    private static bool ContainsAny(string key, string[] needles)
    {
        foreach (var needle in needles)
        {
            if (key.Contains(needle, StringComparison.Ordinal)) return true;
        }

        return false;
    }

    private static bool StartsWithAny(string key, string[] prefixes)
    {
        foreach (var prefix in prefixes)
        {
            if (key.StartsWith(prefix, StringComparison.Ordinal)) return true;
        }

        return false;
    }
}
