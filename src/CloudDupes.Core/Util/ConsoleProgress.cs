namespace CloudDupes.Core.Util;

/// <summary>Synchronous progress sink (unlike <see cref="Progress{T}"/>, messages stay in order).</summary>
public sealed class ConsoleProgress : IProgress<string>
{
    readonly Lock _lock = new();

    public void Report(string value)
    {
        lock (_lock) Console.WriteLine(value);
    }
}
