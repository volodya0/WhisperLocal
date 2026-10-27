using System.Text;
using System.Text.Json;

namespace WhisperLocal.Config;

/// <summary>
/// Changes single values in config.json in place, keeping comments, order and formatting
/// (re-serializing the whole file would drop the comments). The hot reload then applies the change.
/// </summary>
public static class ConfigEditor
{
    /// <summary>Sets "<paramref name="section"/>": { "<paramref name="property"/>": value } to <paramref name="value"/>.</summary>
    public static void SetValue<T>(string path, string section, string property, T value)
    {
        var bytes = File.ReadAllBytes(path);
        var bom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
        var json = bytes.AsSpan(bom);

        var reader = new Utf8JsonReader(json, new JsonReaderOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        });

        var inSection = false;
        while (reader.Read())
        {
            if (reader.TokenType != JsonTokenType.PropertyName)
                continue;
            var name = reader.GetString();
            if (reader.CurrentDepth == 1)
            {
                inSection = string.Equals(name, section, StringComparison.OrdinalIgnoreCase);
                continue;
            }
            if (!inSection || reader.CurrentDepth != 2 || !string.Equals(name, property, StringComparison.OrdinalIgnoreCase))
                continue;

            reader.Read();
            if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray)
                throw new InvalidOperationException($"{section}.{property} is not a simple value");
            var start = bom + (int)reader.TokenStartIndex;
            var end = bom + (int)reader.BytesConsumed;
            var replacement = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value, ConfigManager.JsonOptions));

            var result = new byte[bytes.Length - (end - start) + replacement.Length];
            bytes.AsSpan(0, start).CopyTo(result);
            replacement.CopyTo(result.AsSpan(start));
            bytes.AsSpan(end).CopyTo(result.AsSpan(start + replacement.Length));
            File.WriteAllBytes(path, result);
            Log.Info($"Config: {section}.{property} = {Encoding.UTF8.GetString(replacement)}");
            return;
        }
        throw new InvalidOperationException($"{section}.{property} not found in {path}; add it to the config by hand");
    }
}
