namespace RbManager.Tests.Support;

// Sets process environment variables and restores their prior values on
// Dispose (in reverse order). Only safe inside the Serial collection.
internal sealed class EnvScope : IDisposable
{
    private readonly List<(string Name, string? Prev)> _prev = [];

    public void Set(string name, string? value)
    {
        _prev.Add((name, Environment.GetEnvironmentVariable(name)));
        Environment.SetEnvironmentVariable(name, value);
    }

    public void Dispose()
    {
        for (int i = _prev.Count - 1; i >= 0; i--)
            Environment.SetEnvironmentVariable(_prev[i].Name, _prev[i].Prev);
    }
}
