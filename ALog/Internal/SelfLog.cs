namespace ALog.Internal;

/// <summary>
/// Reports failures that were caught so logging can continue.
/// The default writes one line to <see cref="Console.Error"/>.
/// Set <see cref="Target"/> to opt into a different sink; that replaces stderr
/// for this process. <see cref="Report"/> never throws, including when the
/// target or stderr fails, so diagnostics cannot take down the caller.
/// </summary>
internal static class SelfLog
{
    internal static Action<string>? Target { get; set; }

    internal static void Report(string source, Exception exception)
    {
        if (exception is null)
            return;

        try
        {
            var line = BuildLine(source, exception);
            var target = Target;
            if (target is not null)
            {
                target(line);
                return;
            }

            Console.Error.WriteLine(line);
        }
        catch
        {
            // Last-resort diagnostics must not escape into the application.
        }
    }

    private static string BuildLine(string source, Exception exception)
    {
        var name = string.IsNullOrEmpty(source) ? "ALog" : source;
        var detail = SafeMessage(exception).Replace('\r', ' ').Replace('\n', ' ');
        return "[ALog] " + name + " failed: " + exception.GetType().Name + ": " + detail;
    }

    private static string SafeMessage(Exception exception)
    {
        try
        {
            return exception.Message ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }
}
