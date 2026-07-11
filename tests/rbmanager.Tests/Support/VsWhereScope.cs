namespace RbManager.Tests.Support;

// Temporarily overrides the Devkit.VsWhere static and restores it on Dispose.
// Only safe inside the Serial collection.
internal sealed class VsWhereScope : IDisposable
{
    private readonly string _prev = Devkit.VsWhere;

    public VsWhereScope(string value) => Devkit.VsWhere = value;

    public void Dispose() => Devkit.VsWhere = _prev;
}
