namespace ALog.QuickstartSample;

/// <summary>Application exception whose full name must appear on the line.</summary>
public sealed class CardException : Exception
{
    /// <summary>Creates the exception with the caller's message.</summary>
    /// <param name="message">Message text.</param>
    public CardException(string message)
        : base(message)
    {
    }
}

/// <summary>Stand-in for the checkout sample. Each method throws from a fixed file and line.</summary>
public static class CheckoutService
{
    /// <summary>
    /// Reads a path that does not exist, so the first frames are <c>System.Private.CoreLib</c>.
    /// The origin must still be this method at line 7 of <c>Cls.cs</c>.
    /// </summary>
    public static void ReadIo()
    {
#line 7 "Cls.cs"
        File.ReadAllText("/nope/y");
#line default
    }

    /// <summary>Throws the card exception from line 42 of <c>CheckoutService.cs</c>.</summary>
    public static void Pay()
    {
#line 42 "CheckoutService.cs"
        throw new InvalidOperationException("Card declined");
#line default
    }

    /// <summary>Throws after an await so the frame is an async state machine.</summary>
    /// <returns>A faulted task.</returns>
    public static async Task PayAsync()
    {
        await Task.Yield();
#line 88 "BlobClient.cs"
        throw new InvalidOperationException("async");
#line default
    }

    /// <summary>Throws from a lambda so the frame name is compiler-generated.</summary>
    public static void Run()
    {
        Action act = () =>
        {
#line 7 "Lambda.cs"
            throw new InvalidOperationException("lam");
#line default
        };
        act();
    }

    /// <summary>Throws from a local function so the frame name contains <c>g__</c>.</summary>
    public static void Bill()
    {
        void ChargeLocal()
        {
#line 9 "Local.cs"
            throw new InvalidOperationException("loc");
#line default
        }

        ChargeLocal();
    }
}
