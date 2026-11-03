namespace OrientPyx.Tests.Support;

/// <summary>Unique competition identifiers for tests that build their own competition in the shared data root.</summary>
public static class TestIds
{
    private static int _counter;

    public static string Next(string prefix) => $"t_{prefix}_{Interlocked.Increment(ref _counter)}_{Environment.ProcessId % 1000}";
}
