namespace DiffusionNexus.Service.Services.DatasetQuality.Dictionaries;

/// <summary>
/// Maps semantic feature categories to known terms commonly found in captions.
/// Used by checks that verify feature coverage (e.g. "does every character caption
/// describe hair color?") and by consistency checks.
/// </summary>
public static class FeatureCategories
{
    /// <summary>
    /// Category name → list of known terms that belong to that category.
    /// All terms are lowercase for case-insensitive matching.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> Categories =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["hair_color"] =
            [
                "black hair", "brown hair", "blonde hair", "blond hair",
                "red hair", "ginger hair", "white hair", "gray hair", "grey hair",
                "silver hair", "blue hair", "green hair", "pink hair", "purple hair",
                "orange hair", "platinum hair", "auburn hair", "strawberry blonde",
                "multicolored hair", "two-tone hair", "gradient hair"
            ],

            ["hair_style"] =
            [
                "long hair", "short hair", "medium hair", "shoulder-length hair",
                "ponytail", "twintails", "pigtails", "braid", "braids",
                "bun", "hair bun", "updo", "bob cut", "pixie cut",
                "bangs", "side bangs", "blunt bangs", "swept bangs",
                "curly hair", "straight hair", "wavy hair",
                "messy hair", "slicked back", "afro", "dreadlocks",
                "hair over one eye", "hair between eyes",
                "ahoge", "sidelocks", "drill hair", "hime cut"
            ],

            ["eye_color"] =
            [
                "blue eyes", "green eyes", "brown eyes", "hazel eyes",
                "black eyes", "gray eyes", "grey eyes", "red eyes",
                "purple eyes", "violet eyes", "amber eyes", "golden eyes",
                "heterochromia", "multicolored eyes"
            ],

            ["facial_expression"] =
            [
                "smile", "smiling", "grin", "grinning", "laugh", "laughing",
                "frown", "frowning", "serious", "neutral expression",
                "angry", "surprised", "sad", "crying", "tears",
                "blushing", "blush", "pout", "pouting",
                "open mouth", "closed mouth", "tongue out",
                "wink", "winking", "closed eyes", "half-closed eyes"
            ],

            ["pose"] =
            [
                "standing", "sitting", "kneeling", "lying down", "crouching",
                "walking", "running", "jumping", "leaning",
                "arms crossed", "arms up", "hands on hips",
                "hand on head", "hand on chin", "hand on cheek",
                "looking at viewer", "looking away", "looking back",
                "looking up", "looking down", "looking to the side",
                "from above", "from below", "from behind", "from side",
                "profile", "three-quarter view",
                "full body", "upper body", "cowboy shot", "portrait"
            ],

            ["clothing_upper"] =
            [
                "shirt", "blouse", "t-shirt", "tank top", "crop top",
                "sweater", "hoodie", "jacket", "coat", "blazer",
                "vest", "cardigan", "turtleneck", "halter top",
                "strapless", "off-shoulder", "sleeveless",
                "long sleeves", "short sleeves", "rolled sleeves"
            ],

            ["clothing_lower"] =
            [
                "pants", "trousers", "jeans", "shorts", "skirt",
                "miniskirt", "long skirt", "pleated skirt",
                "leggings", "tights", "stockings", "thigh highs",
                "sweatpants", "cargo pants"
            ],

            ["clothing_full"] =
            [
                "dress", "gown", "sundress", "school uniform", "uniform",
                "suit", "jumpsuit", "romper", "kimono", "bikini",
                "swimsuit", "bodysuit", "armor", "casual clothes",
                "formal wear", "sportswear", "pajamas"
            ],

            ["clothing_footwear"] =
            [
                "shoes", "boots", "sneakers", "sandals", "heels",
                "high heels", "flats", "slippers", "barefoot",
                "knee-high boots", "thigh-high boots", "loafers"
            ],

            ["accessories"] =
            [
                "glasses", "sunglasses", "hat", "cap", "beanie",
                "crown", "tiara", "headband", "hair ribbon", "bow",
                "necklace", "choker", "pendant", "earrings", "bracelet",
                "ring", "watch", "scarf", "tie", "bowtie",
                "belt", "bag", "backpack", "purse", "umbrella",
                "mask", "gloves", "fingerless gloves"
            ],

            ["setting_indoor"] =
            [
                "indoors", "room", "bedroom", "kitchen", "bathroom",
                "living room", "office", "classroom", "library",
                "restaurant", "cafe", "bar", "gym", "studio",
                "hallway", "corridor", "stairs", "elevator"
            ],

            ["setting_outdoor"] =
            [
                "outdoors", "outside", "park", "garden", "street",
                "road", "sidewalk", "beach", "ocean", "sea",
                "forest", "woods", "mountain", "hill", "field",
                "meadow", "river", "lake", "pond", "waterfall",
                "city", "cityscape", "skyline", "rooftop", "balcony",
                "bridge", "train station", "airport"
            ],

            ["time_of_day"] =
            [
                "daytime", "day", "morning", "afternoon", "evening",
                "night", "nighttime", "sunset", "sunrise", "dusk",
                "dawn", "twilight", "golden hour", "blue hour"
            ],

            ["weather"] =
            [
                "sunny", "cloudy", "overcast", "rainy", "rain",
                "snowy", "snow", "foggy", "fog", "misty",
                "windy", "stormy", "clear sky"
            ],

            ["lighting"] =
            [
                "natural light", "sunlight", "moonlight", "candlelight",
                "neon light", "backlight", "backlighting", "backlit",
                "soft light", "hard light", "dramatic lighting",
                "rim light", "studio lighting", "ambient light",
                "shadow", "shadows", "silhouette", "high contrast",
                "low key", "high key"
            ],

            ["camera_angle"] =
            [
                "close-up", "closeup", "extreme close-up",
                "medium shot", "wide shot", "panoramic",
                "dutch angle", "bird's eye view", "worm's eye view",
                "overhead", "eye level", "low angle", "high angle",
                "fisheye", "macro", "depth of field", "bokeh",
                "shallow depth of field"
            ],

            ["body_type"] =
            [
                "slim", "slender", "thin", "petite",
                "muscular", "athletic", "buff", "fit",
                "curvy", "plus size", "tall", "short",
                "average build", "stocky", "lean"
            ],

            ["skin_tone"] =
            [
                "pale skin", "fair skin", "light skin",
                "medium skin", "olive skin", "tan", "tanned",
                "dark skin", "brown skin", "ebony"
            ],

            ["age_group"] =
            [
                "young", "elderly", "old", "middle-aged",
                "teen", "teenager", "adult", "mature"
            ]
        };

    /// <summary>
    /// Reverse lookup: term → category name. Built once on first access.
    /// If a term appears in multiple categories, the first match wins.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> TermToCategory = BuildTermIndex();

    /// <summary>
    /// All category names in definition order.
    /// </summary>
    public static readonly IReadOnlyList<string> CategoryNames = [.. Categories.Keys];

    private static Dictionary<string, string> BuildTermIndex()
    {
        var index = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (category, terms) in Categories)
        {
            foreach (var term in terms)
            {
                index.TryAdd(term, category);
            }
        }
        return index;
    }
}
