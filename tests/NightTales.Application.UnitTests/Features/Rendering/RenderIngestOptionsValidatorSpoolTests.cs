using NightTales.Application.Rendering;
using Shouldly;
using Xunit;

namespace NightTales.Application.UnitTests.Features.Rendering;

/// <summary>
/// S57 security review (S1): the spool directory is refused unless it is a real directory private to the account
/// the Worker runs as, and the boot probe never opens a name another local account could have planted there.
/// Plain unit tests against the real filesystem — the directory is the thing under test, so a fake would hide the
/// behaviour — under this process's own temp path, with no host and no database.
/// </summary>
[Trait("Category", "Unit")]
public sealed class RenderIngestOptionsValidatorSpoolTests : IDisposable
{
    /// <summary>The mode the validator requires of the spool: owner read/write/execute (0700).</summary>
    private const UnixFileMode OwnerOnly =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "nts-ingest-validator-tests",
        Guid.CreateVersion7().ToString("N"));

    public RenderIngestOptionsValidatorSpoolTests() => Directory.CreateDirectory(_root);

    /// <inheritdoc />
    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void WorldReadableDirectory_IsRefused()
    {
        if (OperatingSystem.IsWindows())
        {
            return; // POSIX permission bits are not reported on Windows.
        }

        // Every bit of group and others, one at a time: any single one lets another local account read a spooled
        // render or plant a file the job would upload.
        var bits = new[]
        {
            UnixFileMode.GroupRead, UnixFileMode.GroupWrite, UnixFileMode.GroupExecute,
            UnixFileMode.OtherRead, UnixFileMode.OtherWrite, UnixFileMode.OtherExecute,
        };

        foreach (var bit in bits)
        {
            var directory = NewDirectory($"loose-{bit}");
            File.SetUnixFileMode(directory, OwnerOnly | bit);

            var problems = RenderIngestOptionsValidator.Validate(
                new RenderIngestOptions { TempDirectory = directory });

            problems.ShouldHaveSingleItem($"mode {OwnerOnly | bit} must be refused");
            problems[0].ShouldContain(nameof(RenderIngestOptions.TempDirectory));
            problems[0].ShouldContain("0700");
        }
    }

    [Fact]
    public void SymlinkedDirectory_IsRefused()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Creating symlinks requires a privilege on Windows.");

        var target = NewDirectory("spool-target");
        var link = Path.Combine(_root, "spool-link");
        Directory.CreateSymbolicLink(link, target);

        var problems = RenderIngestOptionsValidator.Validate(
            new RenderIngestOptions { TempDirectory = link });

        problems.ShouldHaveSingleItem();
        problems[0].ShouldContain(nameof(RenderIngestOptions.TempDirectory));
        problems[0].ShouldContain("symbolic link");
    }

    [Fact]
    public void OwnerOnlyDirectory_IsAccepted()
    {
        var directory = NewDirectory("spool");

        RenderIngestOptionsValidator.Validate(new RenderIngestOptions { TempDirectory = directory })
            .ShouldBeEmpty();
    }

    [Fact]
    public void MissingDirectory_IsCreatedOwnerOnly()
    {
        var directory = Path.Combine(_root, "fresh", "spool");

        RenderIngestOptionsValidator.Validate(new RenderIngestOptions { TempDirectory = directory })
            .ShouldBeEmpty();

        Directory.Exists(directory).ShouldBeTrue();

        if (!OperatingSystem.IsWindows())
        {
            File.GetUnixFileMode(directory).ShouldBe(OwnerOnly);
        }
    }

    [Fact]
    public void Probe_LeavesNoFileBehind()
    {
        var directory = NewDirectory("probe");

        RenderIngestOptionsValidator.Validate(new RenderIngestOptions { TempDirectory = directory })
            .ShouldBeEmpty();

        Directory.GetFileSystemEntries(directory).ShouldBeEmpty();
    }

    [Fact]
    public void PlantedProbeFileName_IsNotFollowed()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Creating symlinks requires a privilege on Windows.");

        var directory = NewDirectory("planted");
        var victim = Path.Combine(_root, "victim.mp4");
        File.WriteAllText(victim, "keep");

        // The name the old fixed-name probe used: a planted link must never be opened by the check, or the check
        // itself would be the write primitive (and would truncate whatever the link points at).
        var planted = Path.Combine(directory, ".nts-ingest-probe");
        File.CreateSymbolicLink(planted, victim);

        RenderIngestOptionsValidator.Validate(new RenderIngestOptions { TempDirectory = directory })
            .ShouldBeEmpty();

        File.ReadAllText(victim).ShouldBe("keep");
        File.GetAttributes(planted).HasFlag(FileAttributes.ReparsePoint).ShouldBeTrue();
        Directory.GetFileSystemEntries(directory).ShouldBe(new[] { planted });
    }

    /// <summary>Creates a directory the validator must accept: owner-only on POSIX, a plain one on Windows.</summary>
    /// <param name="name">The leaf name, under this test's own root.</param>
    /// <returns>The absolute path.</returns>
    private string NewDirectory(string name)
    {
        var path = Path.Combine(_root, name);

        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(path);
        }
        else
        {
            Directory.CreateDirectory(path, OwnerOnly);
        }

        return path;
    }
}
