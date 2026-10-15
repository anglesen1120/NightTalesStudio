namespace NightTales.Application.Features.SeriesBible.Abstractions;

/// <summary>
/// The kill switch of the bible-proposal feature (02.6.3 plan §7.1 step 3, §11): whether an approved story
/// queues a proposal run at all.
/// </summary>
/// <remarks>
/// <para>
/// The switch is the bound <c>Bible:Proposal:Enabled</c> key, read here through a port because this assembly
/// may not reference <c>Microsoft.Extensions.Options</c> (architecture rule
/// <c>Application_ReferencesOnlyAllowedPackages</c>); the Worker implements it over the section it binds, and
/// <see cref="BibleProposalJobOptions"/> documents the key itself.
/// </para>
/// <para>
/// The answer is read per outbox delivery rather than captured once, because the plan's rollback (plan §11,
/// 09.10.3) turns the feature off with a configuration change and needs no restart. With the switch off an
/// approval stores nothing and spends nothing; proposals already staged stay visible and can still be accepted
/// or dismissed, so no data rollback is needed.
/// </para>
/// </remarks>
public interface IBibleProposalSwitch
{
    /// <summary>Whether an approved story queues a proposal run (<see langword="false"/> = rolled back).</summary>
    bool Enabled { get; }
}
