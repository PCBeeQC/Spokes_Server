using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Spokes_Server.Components.Pages.Chat;

public static class EmojiData
{
    public static readonly Dictionary<string, List<string>> Categories;
    public static readonly Dictionary<string, string> Keywords;

    static EmojiData()
    {
        Categories = [];
        Keywords = [];

        try
        {
            var assembly = Assembly.GetExecutingAssembly();
            var resourceName = assembly.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith("emoji.json"));

            if (resourceName != null)
            {
                using var stream = assembly.GetManifestResourceStream(resourceName);
                using var reader = new StreamReader(stream!);
                var json = reader.ReadToEnd();

                var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                var emojis = JsonSerializer.Deserialize<List<GemojiEntry>>(json, options);

                if (emojis != null)
                {
                    foreach (var e in emojis)
                    {
                        if (string.IsNullOrEmpty(e.Emoji)) continue;

                        var catName = string.IsNullOrEmpty(e.Category) ? "Symbols" : e.Category;
                        if (!Categories.ContainsKey(catName)) Categories[catName] = [];
                        Categories[catName].Add(e.Emoji);

                        List<string> kwList = [];
                        if (!string.IsNullOrEmpty(e.Description)) kwList.Add(e.Description);
                        if (e.Aliases != null) kwList.AddRange(e.Aliases);
                        if (e.Tags != null) kwList.AddRange(e.Tags);

                        Keywords[e.Emoji] = string.Join(" ", kwList).ToLowerInvariant() + " " + e.Emoji;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error loading emoji data: {ex.Message}");
        }
    }

    private class GemojiEntry
    {
        [JsonPropertyName("emoji")]
        public string Emoji { get; set; } = "";

        [JsonPropertyName("description")]
        public string Description { get; set; } = "";

        [JsonPropertyName("category")]
        public string Category { get; set; } = "";

        [JsonPropertyName("aliases")]
        public List<string> Aliases { get; set; } = [];

        [JsonPropertyName("tags")]
        public List<string> Tags { get; set; } = [];
    }
}
