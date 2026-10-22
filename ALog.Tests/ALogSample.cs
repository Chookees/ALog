namespace ALogSample;

/// <summary>User code whose namespace starts with the letters of ALog and must still be shown.</summary>
public static class Worker
{
    /// <summary>Throws from line 3 of <c>Worker.cs</c>.</summary>
    public static void Run()
    {
#line 3 "Worker.cs"
        throw new InvalidOperationException("sample");
#line default
    }
}
