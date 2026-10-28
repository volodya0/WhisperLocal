using System.Text;
using System.Text.Json;

namespace WhisperLocal.Config;

/// <summary>
/// Changes single values in config.json in place, keeping comments, order and formatting
/// (re-serializing the whole file would drop the comments). The hot reload then applies the change.
/// </summary>
public static class ConfigEditor
{
    /// <summary>
    /// Sets "<paramref name="section"/>": { "<paramref name="property"/>": value } to <paramref name="value"/>,
    /// adding the property (and the section) if a config from an older version lacks them.
    /// </summary>
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
        int? rootStart = null, sectionStart = null;
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.StartObject)
            {
                // Just after "{": where a missing property or section goes
                if (reader.CurrentDepth == 0)
                    rootStart ??= bom + (int)reader.BytesConsumed;
                else if (reader.CurrentDepth == 1 && inSection)
                    sectionStart ??= bom + (int)reader.BytesConsumed;
                continue;
            }
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
            var replacement = Serialize(value);
            Write(path, bytes, start, end, replacement);
            Log.Info($"Config: {section}.{property} = {replacement}");
            return;
        }

        // A trailing comma before "}" is fine: the config is read with AllowTrailingCommas
        var serialized = Serialize(value);
        if (sectionStart is { } at)
            Write(path, bytes, at, at, $"\n    \"{property}\": {serialized},");
        else if (rootStart is { } root)
            Write(path, bytes, root, root, $"\n  \"{section}\": {{\n    \"{property}\": {serialized}\n  }},");
        else
            throw new InvalidOperationException($"{path} is not a JSON object");
        Log.Info($"Config: {section}.{property} = {serialized} (added)");
    }

    private static string Serialize<T>(T value) => JsonSerializer.Serialize(value, ConfigManager.JsonOptions);

    /// <summary>Replaces bytes [start, end) of the file with <paramref name="text"/>.</summary>
    private static void Write(string path, byte[] bytes, int start, int end, string text)
    {
        var replacement = Encoding.UTF8.GetBytes(text);
        var result = new byte[bytes.Length - (end - start) + replacement.Length];
        bytes.AsSpan(0, start).CopyTo(result);
        replacement.CopyTo(result.AsSpan(start));
        bytes.AsSpan(end).CopyTo(result.AsSpan(start + replacement.Length));
        File.WriteAllBytes(path, result);
    }
}
