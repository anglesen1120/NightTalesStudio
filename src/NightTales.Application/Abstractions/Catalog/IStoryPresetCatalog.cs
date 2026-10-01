namespace NightTales.Application.Abstractions.Catalog;

/// <summary>
/// The known story-preset keys of the 02 prompt catalog (02 D5), so a series can only store a preset
/// the story writer can load. 02.1.4-T1 owns the implementation over the embedded <c>prompts/</c>
/// manifest; 09.4.1-T1 adds the port and the wave-1 null implementation
/// (<see cref="NullStoryPresetCatalog"/>, [DECISION] 09.4.1 plan §4 U1).
/// </summary>
/// <remarks>
/// The port is one predicate and not the catalog itself: the series only needs "would the writer
/// recognise this key?" — reading preset text is 02's.
/// </remarks>
public interface IStoryPresetCatalog
{
    /// <summary>Whether <paramref name="key"/> names a preset of the shipped catalog.</summary>
    /// <param name="key">The preset key as the client sent it, never <see langword="null"/>.</param>
    /// <returns><see langword="true"/> when the story writer would find a template for it.</returns>
    bool IsKnown(string key);
}

/// <summary>
/// The wave-1 implementation: no prompt catalog is shipped yet (02.1.4-T1 loads it), so no key is
/// known and a series can only be created or patched with a null <c>storyPreset</c>
/// ([DECISION] 09.4.1 plan §4 U1). 02.1.4-T1 replaces this registration.
/// </summary>
public sealed class NullStoryPresetCatalog : IStoryPresetCatalog
{
    /// <inheritdoc />
    public bool IsKnown(string key) => false;
}
