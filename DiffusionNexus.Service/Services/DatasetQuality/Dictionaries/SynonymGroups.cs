namespace DiffusionNexus.Service.Services.DatasetQuality.Dictionaries;

/// <summary>
/// Groups of interchangeable terms commonly found in image captions.
/// Used by consistency checks to detect when the same concept is described
/// with different words across captions (e.g. "woman" in one, "lady" in another).
/// </summary>
public static class SynonymGroups
{
    /// <summary>
    /// Each entry is a set of terms that are considered interchangeable
    /// for the purpose of dataset consistency analysis. All terms are lowercase.
    /// </summary>
    public static readonly IReadOnlyList<HashSet<string>> Groups =
    [
        // People — female
        new(StringComparer.OrdinalIgnoreCase) { "woman", "girl", "lady", "female", "gal" },
        // People — male
        new(StringComparer.OrdinalIgnoreCase) { "man", "boy", "guy", "male", "gentleman" },
        // People — child
        new(StringComparer.OrdinalIgnoreCase) { "child", "kid", "youngster", "toddler" },
        // People — general
        new(StringComparer.OrdinalIgnoreCase) { "person", "individual", "human", "figure" },

        // Vehicles
        new(StringComparer.OrdinalIgnoreCase) { "car", "automobile", "vehicle", "auto" },
        new(StringComparer.OrdinalIgnoreCase) { "truck", "lorry", "pickup" },
        new(StringComparer.OrdinalIgnoreCase) { "bicycle", "bike", "cycle" },
        new(StringComparer.OrdinalIgnoreCase) { "motorcycle", "motorbike" },

        // Hair color
        new(StringComparer.OrdinalIgnoreCase) { "blonde", "blond" },
        new(StringComparer.OrdinalIgnoreCase) { "brunette", "brown-haired", "brown hair" },
        new(StringComparer.OrdinalIgnoreCase) { "redhead", "red-haired", "red hair", "ginger" },

        // Hair style
        new(StringComparer.OrdinalIgnoreCase) { "ponytail", "pony tail" },
        new(StringComparer.OrdinalIgnoreCase) { "pigtails", "twintails", "twin tails" },
        new(StringComparer.OrdinalIgnoreCase) { "bun", "hair bun", "updo" },

        // Eye color
        new(StringComparer.OrdinalIgnoreCase) { "blue eyes", "blue-eyed" },
        new(StringComparer.OrdinalIgnoreCase) { "green eyes", "green-eyed" },
        new(StringComparer.OrdinalIgnoreCase) { "brown eyes", "brown-eyed" },

        // Facial expressions
        new(StringComparer.OrdinalIgnoreCase) { "smile", "smiling", "grin", "grinning" },
        new(StringComparer.OrdinalIgnoreCase) { "frown", "frowning", "scowl", "scowling" },
        new(StringComparer.OrdinalIgnoreCase) { "laugh", "laughing", "chuckle", "chuckling" },

        // Clothing — upper body
        new(StringComparer.OrdinalIgnoreCase) { "shirt", "blouse", "top" },
        new(StringComparer.OrdinalIgnoreCase) { "jacket", "coat", "blazer" },
        new(StringComparer.OrdinalIgnoreCase) { "sweater", "pullover", "jumper" },
        new(StringComparer.OrdinalIgnoreCase) { "hoodie", "hooded sweatshirt" },
        new(StringComparer.OrdinalIgnoreCase) { "t-shirt", "tee", "tshirt" },

        // Clothing — lower body
        new(StringComparer.OrdinalIgnoreCase) { "pants", "trousers", "slacks" },
        new(StringComparer.OrdinalIgnoreCase) { "shorts", "short pants" },
        new(StringComparer.OrdinalIgnoreCase) { "skirt", "miniskirt", "mini skirt" },
        new(StringComparer.OrdinalIgnoreCase) { "jeans", "denim pants", "denim" },

        // Clothing — full body
        new(StringComparer.OrdinalIgnoreCase) { "dress", "gown", "frock" },
        new(StringComparer.OrdinalIgnoreCase) { "suit", "business suit", "formal suit" },
        new(StringComparer.OrdinalIgnoreCase) { "uniform", "outfit", "attire" },

        // Accessories
        new(StringComparer.OrdinalIgnoreCase) { "glasses", "eyeglasses", "spectacles" },
        new(StringComparer.OrdinalIgnoreCase) { "sunglasses", "shades" },
        new(StringComparer.OrdinalIgnoreCase) { "hat", "cap", "headwear" },
        new(StringComparer.OrdinalIgnoreCase) { "necklace", "pendant", "chain" },
        new(StringComparer.OrdinalIgnoreCase) { "earrings", "ear rings" },
        new(StringComparer.OrdinalIgnoreCase) { "bracelet", "wristband", "bangle" },

        // Settings — indoor
        new(StringComparer.OrdinalIgnoreCase) { "room", "chamber", "interior" },
        new(StringComparer.OrdinalIgnoreCase) { "kitchen", "cookroom" },
        new(StringComparer.OrdinalIgnoreCase) { "bedroom", "sleeping room" },
        new(StringComparer.OrdinalIgnoreCase) { "bathroom", "restroom", "washroom" },

        // Settings — outdoor
        new(StringComparer.OrdinalIgnoreCase) { "park", "garden", "green space" },
        new(StringComparer.OrdinalIgnoreCase) { "street", "road", "avenue" },
        new(StringComparer.OrdinalIgnoreCase) { "beach", "shore", "seaside", "coast" },
        new(StringComparer.OrdinalIgnoreCase) { "forest", "woods", "woodland" },
        new(StringComparer.OrdinalIgnoreCase) { "mountain", "hill", "peak" },
        new(StringComparer.OrdinalIgnoreCase) { "lake", "pond" },
        new(StringComparer.OrdinalIgnoreCase) { "ocean", "sea" },
        new(StringComparer.OrdinalIgnoreCase) { "city", "town", "urban" },

        // Poses / actions
        new(StringComparer.OrdinalIgnoreCase) { "standing", "upright" },
        new(StringComparer.OrdinalIgnoreCase) { "sitting", "seated" },
        new(StringComparer.OrdinalIgnoreCase) { "lying", "lying down", "reclined", "reclining" },
        new(StringComparer.OrdinalIgnoreCase) { "walking", "strolling" },
        new(StringComparer.OrdinalIgnoreCase) { "running", "jogging", "sprinting" },
        new(StringComparer.OrdinalIgnoreCase) { "looking at viewer", "facing camera", "facing viewer" },

        // Body descriptors
        new(StringComparer.OrdinalIgnoreCase) { "slim", "slender", "thin" },
        new(StringComparer.OrdinalIgnoreCase) { "muscular", "buff", "athletic" },
        new(StringComparer.OrdinalIgnoreCase) { "tall", "long-legged" },

        // Photography
        new(StringComparer.OrdinalIgnoreCase) { "photo", "photograph", "photography" },
        new(StringComparer.OrdinalIgnoreCase) { "closeup", "close-up", "close up" },
        new(StringComparer.OrdinalIgnoreCase) { "portrait", "headshot", "head shot" },
        new(StringComparer.OrdinalIgnoreCase) { "full body", "full-body", "fullbody", "full body shot" },
        new(StringComparer.OrdinalIgnoreCase) { "upper body", "upper-body", "upperbody" },

        // Lighting
        new(StringComparer.OrdinalIgnoreCase) { "sunlight", "sunshine", "natural light" },
        new(StringComparer.OrdinalIgnoreCase) { "shadow", "shadows", "shading" },
        new(StringComparer.OrdinalIgnoreCase) { "backlight", "backlighting", "backlit" },

        // Quality / booru meta
        new(StringComparer.OrdinalIgnoreCase) { "high quality", "highres", "high resolution", "hq" },
        new(StringComparer.OrdinalIgnoreCase) { "masterpiece", "best quality" },
        new(StringComparer.OrdinalIgnoreCase) { "absurdres", "incredibly absurdres" }
    ];

    /// <summary>
    /// Reverse lookup: term → index of the synonym group it belongs to.
    /// Built once on first access.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, int> TermToGroupIndex = BuildTermIndex();

    private static Dictionary<string, int> BuildTermIndex()
    {
        var index = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < Groups.Count; i++)
        {
            foreach (var term in Groups[i])
            {
                index.TryAdd(term, i);
            }
        }
        return index;
    }
}
