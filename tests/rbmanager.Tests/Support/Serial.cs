namespace RbManager.Tests.Support;

// In-process tests that touch process-global state — environment variables
// (RBMANAGER_*), Console.Out/Error, and the Devkit.VsWhere static — must not
// run concurrently with each other or with anything else. Classes tagged
// [Collection(Serial.Name)] share this collection; DisableParallelization
// keeps it from overlapping the parallel-safe collections too.
[CollectionDefinition(Serial.Name, DisableParallelization = true)]
public sealed class SerialCollection { }

public static class Serial
{
    public const string Name = "Serial";
}
