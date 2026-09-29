namespace NightTales.Application.Abstractions.Jobs;

/// <summary>
/// Marker of a background job class (09 README D12). A job is a plain <c>sealed</c> class in
/// <c>Application/Features/&lt;F&gt;/Jobs/&lt;Name&gt;Job.cs</c> that the Worker resolves from DI —
/// it carries no Hangfire type in its signature, only <see cref="JobPolicyAttribute"/> metadata.
/// </summary>
/// <remarks>
/// Every public method takes ids only (<see cref="System.Guid"/>, numbers, enums,
/// <see cref="System.DateTimeOffset"/> or a whitelisted <c>string</c>) plus a
/// <see cref="System.Threading.CancellationToken"/>; the architecture test
/// <c>Jobs_TakeIdsOnly</c> enforces it, because job arguments are stored in plain text in the
/// Hangfire schema.
/// </remarks>
public interface IBackgroundJob;
