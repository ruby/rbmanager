namespace RbManager.Tests.Support;

// Redirects Console.Out/Error to string buffers and restores them on
// Dispose. Only safe inside the Serial collection. Normalizes trailing
// newlines so assertions can use line lists.
internal sealed class ConsoleCapture : IDisposable
{
    private readonly TextWriter _prevOut = Console.Out;
    private readonly TextWriter _prevErr = Console.Error;
    private readonly StringWriter _out = new();
    private readonly StringWriter _err = new();

    public ConsoleCapture()
    {
        Console.SetOut(_out);
        Console.SetError(_err);
    }

    public string Out => _out.ToString();
    public string Err => _err.ToString();

    public string[] OutLines => Split(Out);
    public string[] ErrLines => Split(Err);

    private static string[] Split(string s) =>
        s.Replace("\r\n", "\n").TrimEnd('\n').Split('\n', StringSplitOptions.None);

    public void Dispose()
    {
        Console.SetOut(_prevOut);
        Console.SetError(_prevErr);
        _out.Dispose();
        _err.Dispose();
    }
}
