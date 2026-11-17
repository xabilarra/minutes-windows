using System.Text;

namespace Minutes.Core;

/// <summary>
/// The user's confirmed spellings (names, brands, jargon), applied to the transcript after Whisper.
/// Sending them as Whisper's prompt made it invent text, so they correct the result instead.
/// </summary>
public sealed class TranscriptVocabulary
{
    /// <summary>Terms at least this long (as sounds) also match a word one letter apart.</summary>
    internal const int MinimumFuzzyLength = 6;

    private static readonly HashSet<char> NameJoiners = ['-', '\'', '’'];

    private readonly List<(string Term, int WordCount, string Key)> _entries;

    public IReadOnlyList<string> Terms { get; }

    /// <summary>Terms separated by commas, semicolons or lines; blank entries and repeats (same sounds) are dropped.</summary>
    public TranscriptVocabulary(string text)
    {
        var terms = new List<string>();
        var keys = new HashSet<string>();
        foreach (var entry in text.Split([',', ';', '\n', '\r'], StringSplitOptions.None))
        {
            var term = string.Join(' ', entry.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            var key = TermKey(term).Key;
            if (key.Length == 0 || !keys.Add(key))
            {
                continue;
            }
            terms.Add(term);
        }
        Terms = terms;
        // Longer terms first (stable), so "Aitor García" wins over "Aitor".
        _entries = terms
            .Select(term => { var (count, key) = TermKey(term); return (term, count, key); })
            .OrderByDescending(entry => entry.count)
            .ToList();
    }

    public IReadOnlyList<TranscriptSegment> Correct(IEnumerable<TranscriptSegment> segments) =>
        segments.Select(segment => segment with { Text = Correct(segment.Text) }).ToList();

    /// <summary>Replaces each run of words that sounds like a term with the term as the user wrote it.</summary>
    public string Correct(string text)
    {
        if (_entries.Count == 0)
        {
            return text;
        }
        var words = WordRanges(text);
        var result = new StringBuilder();
        var copiedUpTo = 0;
        var index = 0;
        while (index < words.Count)
        {
            if (Match(index, words, text) is not { } match)
            {
                index++;
                continue;
            }
            var start = words[index].Start;
            var end = words[index + match.WordCount - 1].End;
            var original = text[start..end];
            // A lowercase term only fixes spelling: "Reel" opening a sentence keeps its capital.
            var keepsCase = match.Term == match.Term.ToLowerInvariant() && original.ToLowerInvariant() == match.Term;
            result.Append(text, copiedUpTo, start - copiedUpTo).Append(keepsCase ? original : match.Term);
            copiedUpTo = end;
            index += match.WordCount;
        }
        return result.Append(text, copiedUpTo, text.Length - copiedUpTo).ToString();
    }

    private (string Term, int WordCount)? Match(int index, List<(int Start, int End)> words, string text)
    {
        foreach (var entry in _entries)
        {
            if (index + entry.WordCount > words.Count)
            {
                continue;
            }
            var window = words.GetRange(index, entry.WordCount);
            var joinedWithinAName = window.Zip(window.Skip(1)).All(pair =>
                text[pair.First.End..pair.Second.Start].All(c => char.IsWhiteSpace(c) || NameJoiners.Contains(c)));
            if (!joinedWithinAName)
            {
                continue;
            }
            var key = SoundKey(string.Join(' ', window.Select(range => text[range.Start..range.End])));
            if (Sounds(key, entry.Key))
            {
                return (entry.Term, entry.WordCount);
            }
        }
        return null;
    }

    private static (int WordCount, string Key) TermKey(string term)
    {
        var words = WordRanges(term).Select(range => term[range.Start..range.End]).ToList();
        return (words.Count, SoundKey(string.Join(' ', words)));
    }

    private static bool Sounds(string key, string termKey)
    {
        if (key == termKey)
        {
            return true;
        }
        if (termKey.Length < MinimumFuzzyLength || key.Length == 0 || key[0] != termKey[0])
        {
            return false;
        }
        return EditDistance(key, termKey, 1) <= 1;
    }

    /// <summary>How a word sounds in Spanish, roughly: no case or accents, b = v, i = y, s = z, silent h, doubled letters once.</summary>
    internal static string SoundKey(string text)
    {
        var folded = TextFolding.RemoveDiacritics(text).ToLowerInvariant();
        var key = new StringBuilder();
        foreach (var character in folded)
        {
            char? sound = character switch
            {
                'h' => null,
                'v' => 'b',
                'y' => 'i',
                'z' => 's',
                _ => char.IsLetterOrDigit(character) || character == ' ' ? character : null,
            };
            if (sound is { } value && (key.Length == 0 || value != key[^1] || value == ' '))
            {
                key.Append(value);
            }
        }
        return string.Join(' ', key.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>Ranges of the runs of letters and digits.</summary>
    private static List<(int Start, int End)> WordRanges(string text)
    {
        var ranges = new List<(int, int)>();
        int? start = null;
        for (var index = 0; index < text.Length; index++)
        {
            var isWordCharacter = char.IsLetterOrDigit(text[index]) || char.IsSurrogate(text[index]);
            if (isWordCharacter && start is null)
            {
                start = index;
            }
            else if (!isWordCharacter && start is { } wordStart)
            {
                ranges.Add((wordStart, index));
                start = null;
            }
        }
        if (start is { } last)
        {
            ranges.Add((last, text.Length));
        }
        return ranges;
    }

    /// <summary>Levenshtein distance, or limit + 1 as soon as it is known to exceed the limit.</summary>
    private static int EditDistance(string lhs, string rhs, int limit)
    {
        if (Math.Abs(lhs.Length - rhs.Length) > limit)
        {
            return limit + 1;
        }
        var previous = Enumerable.Range(0, rhs.Length + 1).ToArray();
        for (var i = 0; i < lhs.Length; i++)
        {
            var current = new int[rhs.Length + 1];
            current[0] = i + 1;
            for (var j = 0; j < rhs.Length; j++)
            {
                current[j + 1] = Math.Min(Math.Min(previous[j + 1] + 1, current[j] + 1), previous[j] + (lhs[i] == rhs[j] ? 0 : 1));
            }
            if (current.Min() > limit)
            {
                return limit + 1;
            }
            previous = current;
        }
        return previous[rhs.Length];
    }
}

/// <summary>Turns Whisper token ids back into text with Whisper's multilingual vocabulary.</summary>
/// <remarks>Groq's segment text drops every word from the first accented letter on, on segments it merges; the tokens are right.</remarks>
public sealed class WhisperTokenizer
{
    public const int FirstSpecialToken = 50257;

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly Dictionary<int, byte[]> _bytesByToken = [];

    /// <summary>Reads a tiktoken vocabulary: one "&lt;base64 bytes&gt; &lt;rank&gt;" per line, blank lines ignored.</summary>
    public WhisperTokenizer(string tiktoken)
    {
        var lines = tiktoken.Split('\n');
        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index].TrimEnd('\r');
            if (line.Length == 0)
            {
                continue;
            }
            var fields = line.Split(' ');
            if (fields.Length != 2 || !int.TryParse(fields[1], out var rank))
            {
                throw new FormatException($"Malformed vocabulary line {index + 1}.");
            }
            try
            {
                // Whisper's vocabulary ends with "= 50256", an empty token.
                _bytesByToken[rank] = fields[0] == "=" ? [] : Convert.FromBase64String(fields[0]);
            }
            catch (FormatException)
            {
                throw new FormatException($"Malformed vocabulary line {index + 1}.");
            }
        }
    }

    /// <summary>The text of the tokens, skipping special ones; null when nothing decodes as text.</summary>
    public string? Text(IEnumerable<int> tokens)
    {
        var bytes = new List<byte>();
        foreach (var token in tokens)
        {
            if (token >= FirstSpecialToken)
            {
                continue;
            }
            if (!_bytesByToken.TryGetValue(token, out var piece))
            {
                return null;
            }
            bytes.AddRange(piece);
        }
        if (bytes.Count == 0)
        {
            return null;
        }
        try
        {
            return StrictUtf8.GetString(bytes.ToArray());
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
    }
}
