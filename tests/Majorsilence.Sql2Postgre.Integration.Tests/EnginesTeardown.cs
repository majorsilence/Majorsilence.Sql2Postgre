using Majorsilence.Sql2Postgre.Integration.Tests.Infrastructure;

namespace Majorsilence.Sql2Postgre.Integration.Tests;

/// <summary>Stops the shared containers once every test in the assembly has run.</summary>
[SetUpFixture]
public sealed class EnginesTeardown
{
    [OneTimeTearDown]
    public Task StopContainers() => Engines.StopAsync();
}
