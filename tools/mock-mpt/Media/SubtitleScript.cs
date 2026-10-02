using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace NightTales.MockMpt.Media;

/// <summary>An SRT the mock wrote: its bytes, its sha256 and how many cues it holds.</summary>
/// <param name="Content">The SubRip text as UTF-8 bytes.</param>
/// <param name="Cues">The cue count, reported as <c>output.subtitles.cues</c>.</param>
/// <param name="Sha256">Lowercase hex sha256 of <see cref="Content"/>.</param>
public sealed record SubtitleFile(byte[] Content, int Cues, string Sha256);

/// <summary>
/// The mock's subtitles: it splits the submitted <c>video_script</c> into sentences — the boundary rule the 05
/// contract pins (one cue per script chunk, 05 C15 / 05.2.3-T1) — and spreads one cue per sentence evenly over
/// the canned duration. The real backend gets its timing from whisper; the mock invents it.
/// </summary>
public static class SubtitleScript
{
    /// <summary>The sentence terminators of both languages; a run of them stays one boundary.</summary>
    private static readonly char[] Terminators = ['.', '!', '?', '…', '。', '！', '？'];

    /// <summary>
    /// Splits <paramref name="script"/> at sentence terminators and newlines, trimming and collapsing
    /// whitespace. A script without a terminator is one sentence; an empty script yields no cue.
    /// </summary>
    public static IReadOnlyList<string> SplitSentences(string script)
    {
        ArgumentNullException.ThrowIfNull(script);

        var sentences = new List<string>();
        var start = 0;
        for (var index = 0; index < script.Length; index++)
        {
            var character = script[index];
            if (character is '\n' or '\r')
            {
                Add(sentences, script[start..index]);
                start = index + 1;
                continue;
            }

            if (Array.IndexOf(Terminators, character) < 0)
            {
                continue;
            }

            var end = index + 1;
            while (end < script.Length && Array.IndexOf(Terminators, script[end]) >= 0)
            {
                end++;
            }

            Add(sentences, script[start..end]);
            start = end;
            index = end - 1;
        }

        Add(sentences, script[start..]);
        return sentences;
    }

    /// <summary>
    /// Builds the SRT for <paramref name="script"/> over <paramref name="durationSeconds"/>, one cue per
    /// sentence, cue <c>i</c> starting where cue <c>i − 1</c> ended.
    /// </summary>
    public static SubtitleFile Build(string script, double durationSeconds)
    {
        var sentences = SplitSentences(script);
        var text = new StringBuilder();
        var step = sentences.Count == 0 ? 0 : durationSeconds / sentences.Count;

        for (var index = 0; index < sentences.Count; index++)
        {
            var start = index * step;
            var end = index == sentences.Count - 1 ? durationSeconds : ((index + 1) * step) - 0.001;
            text.Append(CultureInfo.InvariantCulture, $"{index + 1}\n");
            text.Append(CultureInfo.InvariantCulture, $"{Format(start)} --> {Format(end)}\n");
            text.Append(sentences[index]).Append("\n\n");
        }

        var content = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(text.ToString());
        return new SubtitleFile(content, sentences.Count, Convert.ToHexStringLower(SHA256.HashData(content)));
    }

    /// <summary>SubRip's <c>HH:MM:SS,mmm</c> timestamp.</summary>
    private static string Format(double seconds) =>
        TimeSpan.FromSeconds(Math.Max(0, seconds)).ToString(@"hh\:mm\:ss\,fff", CultureInfo.InvariantCulture);

    private static void Add(List<string> sentences, string candidate)
    {
        var collapsed = string.Join(' ', candidate.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (collapsed.Length > 0)
        {
            sentences.Add(collapsed);
        }
    }
}
