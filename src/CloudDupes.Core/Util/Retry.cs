namespace CloudDupes.Core.Util;

public static class Retry
{
    /// <summary>
    /// Runs <paramref name="action"/>, retrying while <paramref name="retryDelay"/> returns a delay for the exception.
    /// A returned delay of zero means "use exponential backoff".
    /// </summary>
    public static async Task<T> ExecuteAsync<T>(
        Func<CancellationToken, Task<T>> action,
        Func<Exception, TimeSpan?> retryDelay,
        CancellationToken ct,
        int maxAttempts = 6)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await action(ct);
            }
            catch (Exception ex) when (attempt < maxAttempts && retryDelay(ex) is { } delay)
            {
                await Task.Delay(delay == TimeSpan.Zero ? Backoff(attempt) : delay, ct);
            }
        }
    }

    public static TimeSpan Backoff(int attempt) =>
        TimeSpan.FromSeconds(Math.Min(60, Math.Pow(2, attempt))) + TimeSpan.FromMilliseconds(Random.Shared.Next(0, 500));
}
