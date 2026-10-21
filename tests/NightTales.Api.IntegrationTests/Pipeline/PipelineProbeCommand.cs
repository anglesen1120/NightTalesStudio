using ErrorOr;
using Mediator;
using NightTales.Application.Abstractions.Messaging;

namespace NightTales.Api.IntegrationTests.Pipeline;

/// <summary>
/// The command the pipeline probes of 09.1.3-T2 send: a real command shape (<see cref="ICommand{T}"/>,
/// so the behaviours' message constraints are satisfied) that also carries an idempotency key, exactly
/// like a paid endpoint's command does.
/// </summary>
/// <param name="Body">The request body the canonical hash covers; two probes with one key differ by it.</param>
/// <param name="IdempotencyKey">The client key; null runs the command once and stores nothing.</param>
internal sealed record PipelineProbeCommand(string Body = "body", string? IdempotencyKey = null)
    : ICommand<ErrorOr<Guid>>, IIdempotentCommand;
