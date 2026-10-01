using NightTales.Domain.Catalog;
using Shouldly;
using Xunit;

namespace NightTales.Domain.UnitTests.Catalog;

/// <summary>
/// <c>StylePromptVersion</c> (03.1.1-T2 Acceptance checks, 03 README D1/F16): the attribution string is
/// <c>style=&lt;name&gt;@&lt;8 hex of sha256(template)&gt;;neg=v1</c>, and an edit to the template
/// changes it, so a render always points at the text that produced it.
/// </summary>
public sealed class StylePromptVersionTests
{
    private const string Template = "Dark folklore ink illustration of {term}. Black ink wash on aged rice paper, muted grey tones.";

    private const string EditedTemplate = "Dark folklore ink illustration of {term}. Black ink wash on aged rice paper, faded indigo tones.";

    [Fact]
    public void StylePromptVersion_Format()
    {
        var style = StyleTemplate.Create("dark-folklore-ink", Template);

        StylePromptVersion.For(style).ShouldBe("style=dark-folklore-ink@7cc05a50;neg=v1");
    }

    [Fact]
    public void StylePromptVersion_ChangesWhenTemplateChanges()
    {
        var original = StyleTemplate.Create("dark-folklore-ink", Template);
        var edited = StyleTemplate.Create("dark-folklore-ink", EditedTemplate);

        StylePromptVersion.For(edited).ShouldNotBe(StylePromptVersion.For(original));
        StylePromptVersion.For(edited).ShouldStartWith("style=dark-folklore-ink@");
        StylePromptVersion.For(edited).ShouldEndWith(";neg=v1");
    }
}
