namespace NightTales.TestKit.Storage;

/// <summary>
/// The one fake storage endpoint every harness points a host at (09.7.2-T1).
/// </summary>
/// <remarks>
/// It is a closed loopback port, not the dev container's hostname: a connection to it is refused in
/// microseconds, so the storage prober of a test host fails fast and quietly instead of waiting on a
/// DNS lookup for a service the test never starts. A test that needs a real store uses the blob-store
/// tests' own category and their <c>NTS_STORAGE_URL</c>.
/// </remarks>
public static class StorageTestEndpoint
{
    /// <summary>The unreachable endpoint a harness configures without a container.</summary>
    public const string Unreachable = "http://127.0.0.1:9";
}
