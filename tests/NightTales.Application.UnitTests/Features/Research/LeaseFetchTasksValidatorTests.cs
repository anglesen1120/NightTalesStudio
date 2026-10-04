using FluentValidation.Results;
using NightTales.Application.Features.Research.Fetcher;
using NightTales.Application.Features.Research.Fetcher.Commands.LeaseFetchTasks;
using Shouldly;
using Xunit;

namespace NightTales.Application.UnitTests.Features.Research;

/// <summary><see cref="LeaseFetchTasksValidator"/> (02.5.2-T2): <c>max</c> in [1, 4] and a well-formed <c>workerId</c>.</summary>
public sealed class LeaseFetchTasksValidatorTests
{
    private static readonly LeaseFetchTasksValidator Validator = new();

    private static ValidationResult Check(string? workerId, int max = 1) =>
        Validator.Validate(new LeaseFetchTasksCommand(workerId, max));

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public void Valid_Passes(int max) => Check("fetcher-1", max).IsValid.ShouldBeTrue();

    [Fact]
    public void MaxZero_Fails()
    {
        var result = Check("w", 0);

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.PropertyName == nameof(LeaseFetchTasksCommand.Max));
    }

    [Theory]
    [InlineData(5)]
    [InlineData(int.MaxValue)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void MaxAboveFour_Fails(int max) =>
        Check("w", max).Errors.ShouldContain(e => e.PropertyName == nameof(LeaseFetchTasksCommand.Max));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void WorkerIdMissing_Fails(string? workerId) =>
        Check(workerId).Errors.ShouldContain(e => e.PropertyName == nameof(LeaseFetchTasksCommand.WorkerId));

    [Fact]
    public void WorkerId_AtLengthCap_Passes() =>
        Check(new string('a', FetchResultLimits.MaxWorkerIdLength)).IsValid.ShouldBeTrue();

    [Fact]
    public void WorkerId_OverLengthCap_Fails() =>
        Check(new string('a', FetchResultLimits.MaxWorkerIdLength + 1)).IsValid.ShouldBeFalse();

    [Fact]
    public void WorkerId_Megabyte_Fails() => Check(new string('a', 1_048_576)).IsValid.ShouldBeFalse();

    [Theory]
    [InlineData("w\0")]
    [InlineData("w\n1")]
    [InlineData("w\r")]
    [InlineData("w\t")]
    [InlineData("w 1")]
    [InlineData("w\u007f")]
    [InlineData("w\u001b")]
    [InlineData("w/1")]
    [InlineData("w;drop")]
    [InlineData("wörker")]
    [InlineData("w\u202e")]
    public void WorkerId_ControlOrOutsideCharset_Fails(string workerId) =>
        Check(workerId).Errors.ShouldContain(e => e.PropertyName == nameof(LeaseFetchTasksCommand.WorkerId));

    public static TheoryData<string> LoneSurrogateIds() => ["" + '\ud800', "w" + '\udc00'];

    [Theory]
    [MemberData(nameof(LoneSurrogateIds))]
    public void WorkerId_LoneSurrogate_Fails(string workerId) => Check(workerId).IsValid.ShouldBeFalse();

    [Theory]
    [InlineData("fetcher-1")]
    [InlineData("host.example:8081")]
    [InlineData("A_b-C.9")]
    public void WorkerId_AllowedCharset_Passes(string workerId) => Check(workerId).IsValid.ShouldBeTrue();
}
