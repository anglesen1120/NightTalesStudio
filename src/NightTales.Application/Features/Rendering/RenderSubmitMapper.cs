using System.Text;
using ErrorOr;
using NightTales.Application.Abstractions.Stories;
using NightTales.Domain.Catalog;
using NightTales.Domain.Rendering;
using NightTales.Domain.Rendering.Images;
using NightTales.Domain.Rendering.Music;
using NightTales.Domain.Rendering.Subtitles;
using NightTales.RenderNode.Contract;

namespace NightTales.Application.Features.Rendering;

/// <summary>
/// The one place that composes the two render documents of 05 README D11 and D3: <see cref="BuildParams"/>
/// writes the non-content <c>render-params.v1</c> 09.6.1-T1 stores on the job, and <see cref="BuildSubmit"/>
/// maps that stored value plus the story text, the image plan and the prepared scene prompts onto the exact
/// <c>render-submit.v1</c> body the adapter accepts — so no other class composes an MPT field and a field D3
/// fixes can never be left to a default (F10). Pure and deterministic: no <c>DbContext</c>, no
/// <c>HttpClient</c>, no clock, no I/O.
/// </summary>
/// <remarks>
/// The submit body is validated in the D3 order — aspect, subject, script, terms — and the first failing rule
/// is returned, never an aggregate (plan §5.1 T8). The script limit is counted in Runes <b>after</b> NFC
/// normalisation (04 V1, CR-3), and the normalised text is what the body carries.
/// </remarks>
public static class RenderSubmitMapper
{
    /// <summary>The series aspect is neither <c>landscape</c> nor <c>portrait</c> (C14; 05 D3).</summary>
    public const string AspectInvalid = "render.aspect_invalid";

    /// <summary>The story title is empty or longer than <see cref="MaxSubjectLength"/> characters (05 D3).</summary>
    public const string SubjectInvalid = "render.subject_invalid";

    /// <summary>The NFC-normalised story body is empty or longer than <see cref="MaxScriptRunes"/> Runes (04 V1, CR-3).</summary>
    public const string ScriptTooLong = "render.script_too_long";

    /// <summary>The submit carries no prompt or more than <see cref="MaxTerms"/> of them, or one prompt text
    /// exceeds <see cref="MaxTermLength"/> characters (05 D3, 03 D4).</summary>
    public const string TermsInvalid = "render.terms_invalid";

    /// <summary>The longest <c>video_subject</c> (05 D3).</summary>
    public const int MaxSubjectLength = 200;

    /// <summary>The longest normalised <c>video_script</c>, in Runes (04 V1, CR-3).</summary>
    public const int MaxScriptRunes = 20_000;

    /// <summary>The most <c>video_terms</c> entries (05 D3, 03 D4).</summary>
    public const int MaxTerms = 200;

    /// <summary>The longest one <c>video_terms</c> entry (05 D3, 03 D4).</summary>
    public const int MaxTermLength = 1_000;

    /// <summary>The fixed <c>video_clip_duration</c> of 05 D3 (03 D3); never read from the stored params.</summary>
    public const int VideoClipDurationSeconds = 10;

    /// <summary>The <c>voice_name</c> prefix of 04 D10.</summary>
    private const string KokoroPrefix = "kokoro:";

    /// <summary>
    /// Builds the <c>render-params.v1</c> value of 05 README D11: a pure projection of
    /// <paramref name="input"/>, with <see cref="RenderParamsV1.CurrentMapperVersion"/> as the mapper revision.
    /// </summary>
    /// <param name="input">The settings 09.6.1-T1 collected; its <c>Subtitles.Preset</c> is already
    /// <c>&lt;aspect&gt;@&lt;revision&gt;</c> as D11 requires.</param>
    public static RenderParamsV1 BuildParams(RenderParamsInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        return new RenderParamsV1(
            RenderParamsV1.CurrentMapperVersion,
            input.StoryVersionId,
            input.Aspect,
            input.Language,
            input.VoiceId,
            input.StyleTemplateId,
            input.StylePromptVersion,
            input.ImagePlan,
            input.Subtitles,
            input.Music is null
                ? null
                : new MusicParams(input.Music.TrackId, input.Music.BgmVolume, input.MusicAttribution),
            input.ClipSeconds);
    }

    /// <summary>
    /// Builds the <c>render-submit.v1</c> body of 05 README D3/D4 for one submit attempt.
    /// </summary>
    /// <param name="renderJobId">The backend's render job id; it is also the adapter's idempotency key.</param>
    /// <param name="p">The job's stored <c>render-params.v1</c> (<see cref="BuildParams"/>).</param>
    /// <param name="story">The approved version's text, title and keywords (<c>IStoryRenderInputProvider</c>).</param>
    /// <param name="plan">The image planner's output; it supplies the term count and the relay's
    /// <c>image_plan.max_images</c> (03 D4).</param>
    /// <param name="prompts">The prompts to send, in slot order — exactly one per <paramref name="plan"/> term,
    /// from <see cref="BuildPrompts"/>; their <see cref="ScenePrompt.Text"/> are the <c>video_terms</c> and their
    /// <see cref="ScenePrompt.Sha256"/> the relay's <c>image_plan.prompt_sha256</c> (03 D1/D4).</param>
    /// <param name="preset">The subtitle preset in force for <paramref name="p"/>'s aspect (D6/D7).</param>
    /// <param name="track">The picked music track, or <c>null</c> when the params carry no music (D8).</param>
    /// <returns>The body, or the first D3 validation failure — see the four codes on this type.</returns>
    /// <exception cref="ArgumentException"><paramref name="prompts"/> does not hold one entry per
    /// <paramref name="plan"/> term, or <paramref name="p"/> names a music track but <paramref name="track"/>
    /// is <c>null</c> or a different track: a programming error, never a new error code (05.1.2-T1).</exception>
    public static ErrorOr<AdapterSubmitRequestV1> BuildSubmit(
        Guid renderJobId,
        RenderParamsV1 p,
        StoryRenderInput story,
        RenderImagePlan plan,
        IReadOnlyList<ScenePrompt> prompts,
        SubtitlePreset preset,
        MusicTrack? track)
    {
        ArgumentNullException.ThrowIfNull(p);
        ArgumentNullException.ThrowIfNull(story);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(prompts);
        ArgumentNullException.ThrowIfNull(preset);

        // 1. The aspect decides the canvas; an unknown one is never given a silent "16:9" default (C14).
        var aspect = p.Aspect switch
        {
            "landscape" => "16:9",
            "portrait" => "9:16",
            _ => null,
        };

        if (aspect is null)
        {
            return Error.Validation(
                AspectInvalid,
                "The series aspect must be 'landscape' or 'portrait'.");
        }

        // 2. The title is the subject MPT shows in the task list.
        if (story.Title.Length is < 1 or > MaxSubjectLength)
        {
            return Error.Validation(
                SubjectInvalid,
                $"The story title must be 1-{MaxSubjectLength} characters.");
        }

        // 3. NFC first, the Rune count second (04 V1, CR-3), and the normalised text is what is sent.
        // An unpaired surrogate is not valid UTF-16: Normalize throws ArgumentException (a programming error, not a
        // validation result). It cannot arrive from PostgreSQL text or a UTF-8 JSON body, so no error code is added.
        var script = story.Body.Normalize(NormalizationForm.FormC);
        if (CountRunes(script) is < 1 or > MaxScriptRunes)
        {
            return Error.Validation(
                ScriptTooLong,
                $"The story body must be 1-{MaxScriptRunes} code points after NFC normalisation.");
        }

        // 4. The terms are the prepared scene prompts, in slot order (03 D4). The caller builds them from this
        // very plan (<see cref="BuildPrompts"/>), so a different count is a programming error, not user input;
        // the D3 bounds then apply to the prompt text that is sent.
        if (prompts.Count != plan.Terms.Count)
        {
            throw new ArgumentException(
                $"The plan holds {plan.Terms.Count} terms but {prompts.Count} prompts were passed.",
                nameof(prompts));
        }

        if (prompts.Count is < 1 or > MaxTerms)
        {
            return Error.Validation(TermsInvalid, $"A submit needs 1-{MaxTerms} video terms.");
        }

        foreach (var prompt in prompts)
        {
            if (prompt.Text.Length > MaxTermLength)
            {
                return Error.Validation(
                    TermsInvalid,
                    $"A scene prompt must be at most {MaxTermLength} characters.");
            }
        }

        string bgmType;
        string bgmFile;
        decimal bgmVolume;
        if (p.Music is null)
        {
            // The stored params are the record of what was rendered, so no music wins over a picked track.
            bgmType = string.Empty;
            bgmFile = string.Empty;
            bgmVolume = 0m;
        }
        else
        {
            if (track is null)
            {
                throw new ArgumentException(
                    "The stored render params carry music but no track was passed to the mapper.",
                    nameof(track));
            }

            if (!string.Equals(p.Music.TrackId, track.TrackId, StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    $"The stored render params name music track '{p.Music.TrackId}' but the passed track is '{track.TrackId}'.",
                    nameof(track));
            }

            bgmType = "custom";
            bgmFile = track.FileName;
            bgmVolume = p.Music.BgmVolume;
        }

        var mpt = new MptSubmitFields(
            VideoSubject: story.Title,
            VideoScript: script,
            VideoTerms: [.. prompts.Select(prompt => prompt.Text)],
            VideoAspect: aspect,
            VideoFitMode: "cover",
            VideoConcatMode: "sequential",
            VideoTransitionMode: null,
            VideoClipDuration: VideoClipDurationSeconds,
            VideoClipSpeed: 1.0m,
            MatchMaterialsToScript: true,
            VideoCount: 1,
            VideoSource: "openai_image",
            VideoMaterials: null,
            CustomAudioFile: null,
            VideoLanguage: p.Language,
            VoiceName: KokoroPrefix + p.VoiceId,
            VoiceVolume: 1.0m,
            VoiceRate: 1.0m,
            BgmType: bgmType,
            BgmFile: bgmFile,
            BgmVolume: bgmVolume,
            VideoMusicPrompt: string.Empty,
            SoniloBgmPrompt: string.Empty,
            SubtitleEnabled: p.Subtitles.Enabled,
            SubtitleDisplayMode: "sentence",
            SubtitleAnimation: "none",
            SubtitlePosition: preset.SubtitlePosition,
            CustomPosition: preset.CustomPosition,
            FontSize: preset.FontSize,
            StrokeColor: preset.StrokeColor,
            StrokeWidth: preset.StrokeWidth,
            TextForeColor: preset.TextForeColor,
            TextBackgroundColor: preset.TextBackgroundColor,
            RoundedSubtitleBackground: preset.RoundedSubtitleBackground,
            FontName: preset.FontName,
            ParagraphNumber: 1,
            VideoScriptPrompt: string.Empty,
            CustomSystemPrompt: string.Empty);

        return new AdapterSubmitRequestV1(
            Schema: RenderNodeSchemas.RenderSubmit,
            RenderJobId: renderJobId,
            Mpt: mpt,
            ImagePlan: new ImagePlanV1(
                plan.MaxTerms,
                [.. prompts.Select(prompt => prompt.Sha256)]));
    }

    /// <summary>
    /// Builds the prompts <see cref="BuildSubmit"/> sends: one per planned term, in slot order (03 D1/D4).
    /// 09.6.2-T1 calls this before every submit attempt and passes the result on, so the submitted
    /// <c>video_terms</c> and the relay's <c>prompt_sha256</c> are always the same two readings of one prompt.
    /// The mapper builds no prompt itself.
    /// </summary>
    /// <param name="plan">The plan whose terms are sent, in slot order (03 D4).</param>
    /// <param name="style">The series' style template (03 D1); the template is rule-checked at seed time.</param>
    public static IReadOnlyList<ScenePrompt> BuildPrompts(RenderImagePlan plan, StyleTemplate style)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(style);

        return [.. plan.Terms.Select(term => ScenePromptBuilder.Build(style, term.Keyword))];
    }

    /// <summary>Counts the Unicode scalar values of <paramref name="text"/>; an unpaired surrogate counts
    /// as one Rune (U+FFFD), never a rejection.</summary>
    private static int CountRunes(string text)
    {
        var count = 0;
        foreach (var _ in text.EnumerateRunes())
        {
            count++;
        }

        return count;
    }
}
