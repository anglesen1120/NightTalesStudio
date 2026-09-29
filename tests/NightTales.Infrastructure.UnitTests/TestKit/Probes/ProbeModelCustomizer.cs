using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace NightTales.Infrastructure.UnitTests.TestKit.Probes;

/// <summary>
/// Adds the test-only probe model to whatever context it is registered for, on top of the pipeline
/// the context runs itself (<c>base.Customize</c> calls <c>OnModelCreating</c> first). Registered
/// with <c>ReplaceService&lt;IModelCustomizer, ProbeModelCustomizer&gt;()</c>; never used by
/// <c>src</c>.
/// </summary>
internal sealed class ProbeModelCustomizer : RelationalModelCustomizer
{
    public ProbeModelCustomizer(ModelCustomizerDependencies dependencies)
        : base(dependencies)
    {
    }

    public override void Customize(ModelBuilder modelBuilder, DbContext context)
    {
        base.Customize(modelBuilder, context);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(ProbeModelCustomizer).Assembly);
    }
}
