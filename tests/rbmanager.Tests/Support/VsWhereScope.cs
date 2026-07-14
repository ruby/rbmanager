namespace RbManager.Tests.Support;

// Temporarily overrides the Msvc.VsWhere static and restores it on Dispose.
// Only safe inside the Serial collection.
internal sealed class VsWhereScope : IDisposable
{
    private readonly string _prev = Msvc.VsWhere;

    public VsWhereScope(string value) => Msvc.VsWhere = value;

    public void Dispose() => Msvc.VsWhere = _prev;
}
