namespace DiffusionNexus.Service.Services.DatasetQuality.Dictionaries;

/// <summary>
/// Common English stop words excluded from token extraction and frequency analysis.
/// These words carry little semantic value for dataset quality checks.
/// </summary>
public static class StopWords
{
    /// <summary>
    /// Case-insensitive set of stop words for fast lookups.
    /// </summary>
    public static readonly HashSet<string> Set = new(StringComparer.OrdinalIgnoreCase)
    {
        // Articles
        "a", "an", "the",

        // Pronouns
        "i", "me", "my", "myself", "we", "our", "ours", "ourselves",
        "you", "your", "yours", "yourself", "yourselves",
        "he", "him", "his", "himself",
        "she", "her", "hers", "herself",
        "it", "its", "itself",
        "they", "them", "their", "theirs", "themselves",
        "what", "which", "who", "whom", "this", "that", "these", "those",

        // Verbs (common auxiliary / linking)
        "am", "is", "are", "was", "were", "be", "been", "being",
        "have", "has", "had", "having",
        "do", "does", "did", "doing",
        "will", "would", "shall", "should",
        "can", "could", "may", "might", "must",

        // Prepositions
        "at", "by", "for", "from", "in", "into", "of", "on", "onto",
        "to", "with", "about", "above", "after", "against", "along",
        "among", "around", "before", "behind", "below", "beneath",
        "beside", "between", "beyond", "during", "except", "inside",
        "near", "off", "out", "outside", "over", "past", "through",
        "toward", "towards", "under", "until", "up", "upon", "within", "without",

        // Conjunctions
        "and", "but", "or", "nor", "for", "yet", "so",
        "both", "either", "neither", "not", "only", "own", "same",

        // Adverbs / filler
        "also", "as", "back", "even", "here", "how", "just",
        "much", "no", "now", "once", "other", "some", "still",
        "such", "than", "then", "there", "too", "very", "when",
        "where", "while", "why", "all", "any", "each", "every",
        "few", "more", "most", "several",

        // Determiners / quantifiers
        "another", "enough", "many", "one", "two", "three"
    };
}
