using System.Globalization;
using System.Text;

namespace TuneLab.NeutrinoV3;

internal static class NeutrinoPhonemes
{
    public const int Pau = 0;
    public const int Br = 3;
    public const int Ap = 41;

    static readonly Dictionary<string, int> sPhonemeIds = new(StringComparer.Ordinal)
    {
        ["pau"] = 0,
        ["a"] = 1,
        ["b"] = 2,
        ["br"] = 3,
        ["by"] = 4,
        ["ch"] = 5,
        ["cl"] = 6,
        ["d"] = 7,
        ["dy"] = 8,
        ["e"] = 9,
        ["f"] = 10,
        ["g"] = 11,
        ["gy"] = 12,
        ["h"] = 13,
        ["hy"] = 14,
        ["i"] = 15,
        ["j"] = 16,
        ["k"] = 17,
        ["ky"] = 18,
        ["m"] = 19,
        ["my"] = 20,
        ["n"] = 21,
        ["N"] = 22,
        ["ny"] = 23,
        ["o"] = 24,
        ["p"] = 25,
        ["py"] = 27,
        ["r"] = 28,
        ["ry"] = 29,
        ["s"] = 30,
        ["sh"] = 31,
        ["t"] = 33,
        ["ts"] = 34,
        ["ty"] = 35,
        ["u"] = 36,
        ["v"] = 37,
        ["w"] = 38,
        ["y"] = 39,
        ["z"] = 40,
        ["AP"] = 41,
    };

    static readonly Dictionary<string, string[]> sKana = new(StringComparer.Ordinal);
    static string[] sKanaKeys = [];

    static readonly Dictionary<string, string[]> sOnsets = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ky"] = ["ky"],
        ["gy"] = ["gy"],
        ["sh"] = ["sh"],
        ["sy"] = ["sh"],
        ["ch"] = ["ch"],
        ["ty"] = ["ty"],
        ["j"] = ["j"],
        ["jy"] = ["j"],
        ["zy"] = ["j"],
        ["dy"] = ["dy"],
        ["ny"] = ["ny"],
        ["hy"] = ["hy"],
        ["by"] = ["by"],
        ["py"] = ["py"],
        ["my"] = ["my"],
        ["ry"] = ["ry"],
        ["ts"] = ["ts"],
        ["kw"] = ["k", "w"],
        ["gw"] = ["g", "w"],
        ["k"] = ["k"],
        ["g"] = ["g"],
        ["s"] = ["s"],
        ["z"] = ["z"],
        ["t"] = ["t"],
        ["d"] = ["d"],
        ["n"] = ["n"],
        ["h"] = ["h"],
        ["f"] = ["f"],
        ["b"] = ["b"],
        ["p"] = ["p"],
        ["m"] = ["m"],
        ["y"] = ["y"],
        ["r"] = ["r"],
        ["w"] = ["w"],
        ["v"] = ["v"],
    };

    static readonly string[] sOnsetKeys = sOnsets.Keys.OrderByDescending(key => key.Length).ToArray();

    public static void LoadDictionary(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException("NEUTRINO Japanese dictionary was not found.", path);

        var parsed = new Dictionary<string, string[]>(StringComparer.Ordinal);
        foreach (string raw in File.ReadLines(path, Encoding.UTF8))
        {
            string line = raw;
            int comment = line.IndexOf('#');
            if (comment >= 0)
                line = line[..comment];
            string[] parts = line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2)
                continue;
            string key = parts[0].Normalize(NormalizationForm.FormC);
            string[] phones = parts.Skip(1).Select(NormalizeSymbol).ToArray();
            if (phones.All(IsKnown))
                parsed[key] = phones;
        }

        lock (sKana)
        {
            sKana.Clear();
            foreach (var pair in parsed)
                sKana.Add(pair.Key, pair.Value);
            sKanaKeys = sKana.Keys.OrderByDescending(key => key.Length).ToArray();
        }
    }

    public static int GetId(string symbol)
    {
        string normalized = NormalizeSymbol(symbol);
        if (!sPhonemeIds.TryGetValue(normalized, out int id))
            throw new InvalidDataException($"Unknown NEUTRINO phoneme: {symbol}");
        return id;
    }

    public static bool IsKnown(string symbol) => sPhonemeIds.ContainsKey(NormalizeSymbol(symbol));

    public static bool IsCoreVowel(string symbol)
    {
        string value = NormalizeSymbol(symbol);
        return value is "a" or "i" or "u" or "e" or "o" or "N" or "pau" or "AP";
    }

    public static bool IsContinuationLyric(string? lyric)
    {
        string value = lyric?.Trim() ?? string.Empty;
        return value == "-" || value == "ー" || value.StartsWith('+');
    }

    public static string[] LyricToPhonemes(string? lyric)
    {
        string value = (lyric ?? string.Empty).Trim().Normalize(NormalizationForm.FormC);
        if (value.Length == 0 || IsRest(value))
            return ["pau"];

        string[] fields = value.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length > 1 && fields.All(IsKnown))
            return fields.Select(NormalizeSymbol).ToArray();
        if (IsKnown(value))
            return [NormalizeSymbol(value)];

        lock (sKana)
        {
            if (sKana.TryGetValue(value, out string[]? exact))
                return exact.ToArray();
            if (value.Any(IsKanaCharacter))
                return ParseKana(value);
        }

        if (value.All(ch => ch <= 0x7f))
            return ParseRomaji(value);

        throw new InvalidDataException($"Cannot convert lyric '{value}' to NEUTRINO phonemes.");
    }

    static string[] ParseKana(string value)
    {
        var result = new List<string>();
        int index = 0;
        while (index < value.Length)
        {
            char current = value[index];
            if (current == 'ー')
            {
                string? vowel = result.LastOrDefault(IsCoreVowel);
                if (vowel is null || vowel is "pau" or "AP" or "N")
                    throw new InvalidDataException($"Long-vowel mark in '{value}' has no preceding vowel.");
                result.Add(vowel);
                index++;
                continue;
            }
            if (char.IsWhiteSpace(current) || char.IsPunctuation(current))
            {
                index++;
                continue;
            }

            string? match = null;
            foreach (string key in sKanaKeys)
            {
                if (key.Length <= value.Length - index &&
                    value.AsSpan(index, key.Length).SequenceEqual(key.AsSpan()))
                {
                    match = key;
                    break;
                }
            }
            if (match is null)
                throw new InvalidDataException($"Kana '{value[index..]}' is not in the NEUTRINO dictionary.");
            result.AddRange(sKana[match]);
            index += match.Length;
        }
        return result.Count == 0 ? ["pau"] : result.ToArray();
    }

    static string[] ParseRomaji(string value)
    {
        string text = value.ToLowerInvariant()
            .Replace("-", string.Empty, StringComparison.Ordinal)
            .Replace("'", string.Empty, StringComparison.Ordinal);
        var result = new List<string>();
        int index = 0;
        while (index < text.Length)
        {
            char ch = text[index];
            if (char.IsWhiteSpace(ch) || char.IsPunctuation(ch))
            {
                index++;
                continue;
            }
            if (IsVowel(ch))
            {
                result.Add(ch.ToString(CultureInfo.InvariantCulture));
                index++;
                continue;
            }
            if (index + 1 < text.Length && ch == text[index + 1] && ch != 'n')
            {
                result.Add("cl");
                index++;
                continue;
            }
            if (ch == 'n')
            {
                bool final = index + 1 >= text.Length;
                bool beforeConsonant = !final && !IsVowel(text[index + 1]) && text[index + 1] != 'y';
                if (final || beforeConsonant || (index + 1 < text.Length && text[index + 1] == 'n'))
                {
                    result.Add("N");
                    index += index + 1 < text.Length && text[index + 1] == 'n' ? 2 : 1;
                    continue;
                }
            }

            string? onsetKey = sOnsetKeys.FirstOrDefault(key =>
                key.Length <= text.Length - index &&
                text.AsSpan(index, key.Length).Equals(key.AsSpan(), StringComparison.OrdinalIgnoreCase));
            if (onsetKey is null)
                throw new InvalidDataException($"Romaji '{value}' is not supported near '{text[index..]}'.");
            int vowelIndex = index + onsetKey.Length;
            if (vowelIndex >= text.Length || !IsVowel(text[vowelIndex]))
                throw new InvalidDataException($"Romaji '{value}' has an incomplete syllable near '{text[index..]}'.");
            result.AddRange(sOnsets[onsetKey]);
            result.Add(text[vowelIndex].ToString(CultureInfo.InvariantCulture));
            index = vowelIndex + 1;
        }
        return result.Count == 0 ? ["pau"] : result.ToArray();
    }

    static bool IsRest(string value) =>
        value.Equals("R", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("SP", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("rest", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("sil", StringComparison.OrdinalIgnoreCase) ||
        value is "、" or "。" or "," or ".";

    static bool IsKanaCharacter(char ch) =>
        ch is >= '\u3040' and <= '\u30ff' || ch == 'ー';

    static bool IsVowel(char ch) => ch is 'a' or 'i' or 'u' or 'e' or 'o';

    public static string NormalizeSymbol(string? symbol)
    {
        string value = symbol?.Trim() ?? string.Empty;
        if (value.Equals("sil", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("sp", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("rest", StringComparison.OrdinalIgnoreCase))
            return "pau";
        if (value.Equals("ap", StringComparison.OrdinalIgnoreCase))
            return "AP";
        if (value == "N")
            return "N";
        if (sPhonemeIds.ContainsKey(value))
            return value;
        string lower = value.ToLowerInvariant();
        return sPhonemeIds.ContainsKey(lower) ? lower : value;
    }
}
