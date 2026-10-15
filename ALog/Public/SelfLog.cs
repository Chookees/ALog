namespace ALog;

/// <summary>
/// Reports failures that were caught so logging can continue.
/// The default writes one line to <see cref="Console.Error"/>.
/// <see cref="Enable"/> replaces stderr for this process.
/// <see cref="Disable"/> stops output. Reporting never throws, including when
/// the target or stderr fails, so diagnostics cannot take down the caller.
/// </summary>
public static class SelfLog
{
    private const int ModeStderr = 0;
    private const int ModeTarget = 1;
    private const int ModeDisabled = 2;

    private static Action<string>? _target;
    private static int _mode;

    /// <summary>
    /// Sends each internal failure line to <paramref name="target"/> instead of stderr.
    /// A later call replaces the callback. If <paramref name="target"/> throws, the
    /// exception is swallowed: diagnostics must not escape into the application.
    /// </summary>
    /// <param name="target">Receives one single-line failure message.</param>
    public static void Enable(Action<string> target)
    {
        ArgumentNullException.ThrowIfNull(target);
        // Publish the callback before the mode so a reader that observes ModeTarget
        // also observes the action.
        Volatile.Write(ref _target, target);
        Volatile.Write(ref _mode, ModeTarget);
    }

    /// <summary>
    /// Stops internal failure output, including stderr.
    /// The original logging failure is still swallowed.
    /// Call <see cref="Enable"/> to receive lines again.
    /// </summary>
    public static void Disable()
    {
        Volatile.Write(ref _mode, ModeDisabled);
        Volatile.Write(ref _target, null);
    }

    /// <summary>
    /// Test hook. Null restores the stderr default; it does not <see cref="Disable"/>.
    /// </summary>
    internal static Action<string>? Target
    {
        get => Volatile.Read(ref _target);
        set
        {
            if (value is null)
            {
                Volatile.Write(ref _target, null);
                Volatile.Write(ref _mode, ModeStderr);
                return;
            }

            Enable(value);
        }
    }

    internal static void Report(string source, Exception exception)
    {
        if (exception is null)
            return;

        var mode = Volatile.Read(ref _mode);
        if (mode == ModeDisabled)
            return;

        try
        {
            var line = BuildLine(source, exception);
            if (mode == ModeTarget)
            {
                Volatile.Read(ref _target)?.Invoke(line);
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
