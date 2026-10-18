using ErrorOr;
using Mediator;
using NightTales.Application.Abstractions.Audit;

namespace NightTales.Application.Features.Publishing.Channels.Commands.ConnectChannelConnection;

/// <summary>
/// The Mediator face of <see cref="IConnectChannelConnection"/> (06.1.2-T2 task §Design): one channel
/// connection written inside the caller's unit of work, from the callback or from a test that dispatches the
/// command directly.
/// </summary>
/// <remarks>
/// <para>
/// It carries the same <see cref="ConnectChannelConnectionRequest"/> the callback builds and answers the same
/// <see cref="ConnectChannelOutcome"/>, so both entry points run one implementation
/// (<c>ConnectChannelConnectionHandler</c>). Dispatched through Mediator, <c>UnitOfWorkBehavior</c> opens the
/// transaction this write joins; dispatched as the port from inside 06.1.2-T1's callback, the callback's unit #2
/// is already open. Either way the handler never flushes.
/// </para>
/// <para>
/// It is <see cref="NotAuditedAttribute"/> on purpose: the flow this write belongs to records
/// <c>publishing.channel_connected</c> once (06.1.2-T1 task §Handlers step 10, through
/// <c>IAuditWriter</c>), and a pipeline row here would double-count the one connect it writes.
/// </para>
/// </remarks>
/// <param name="Request">The account, the channel, the tokens and the pinned client.</param>
[NotAudited(
    "06.1.2-T1's callback writes publishing.channel_connected itself, once per successful connect, through "
    + "IAuditWriter (task §Handlers step 10); this write is only ever reached inside that flow, so a pipeline "
    + "row here would double-count one connect.")]
public sealed record ConnectChannelConnectionCommand(ConnectChannelConnectionRequest Request)
    : ICommand<ErrorOr<ConnectChannelOutcome>>;
