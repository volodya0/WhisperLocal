namespace WhisperLocal.Text;

/// <summary>Word-level comparison, to highlight what the grammar check changed.</summary>
public static class TextDiff
{
    /// <summary>
    /// The words of <paramref name="revised"/> (with the spaces between them) and whether each one is new or changed
    /// compared with <paramref name="original"/>: the words outside the longest common subsequence.
    /// </summary>
    public static List<(string Text, bool Changed)> Changes(string original, string revised)
    {
        var a = original.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var b = revised.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        // Punctuation around a word does not make it a different word ("WireGuard" → "WireGuard.")
        var ka = a.Select(Key).ToArray();
        var kb = b.Select(Key).ToArray();

        // lcs[i, j]: length of the longest common subsequence of a[i..] and b[j..]
        var lcs = new int[a.Length + 1, b.Length + 1];
        for (var i = a.Length - 1; i >= 0; i--)
        for (var j = b.Length - 1; j >= 0; j--)
            lcs[i, j] = ka[i] == kb[j] ? lcs[i + 1, j + 1] + 1 : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);

        var result = new List<(string, bool)>();
        int x = 0, y = 0;
        while (y < b.Length)
        {
            if (x < a.Length && ka[x] == kb[y])
            {
                result.Add((b[y], false));
                x++;
                y++;
            }
            else if (x < a.Length && lcs[x + 1, y] >= lcs[x, y + 1])
            {
                x++; // a word of the original that was removed or replaced
            }
            else
            {
                result.Add((b[y], true));
                y++;
            }
        }

        // Spaces between the words, so the parts join back into the revised text
        for (var i = 0; i < result.Count - 1; i++)
            result[i] = (result[i].Item1 + " ", result[i].Item2);
        return result;
    }

    private static string Key(string word) => word.Trim('.', ',', ';', ':', '!', '?', '"', '\'', '«', '»', '(', ')');
}
