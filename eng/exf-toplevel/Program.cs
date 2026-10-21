try
{
    await Task.Yield();
#line 10 "Program.cs"
    throw new InvalidOperationException("Card declined");
#line default
}
catch (Exception ex)
{
    ExfTopLevel.TopLevelCapture.Awaited = ex;
}

ExfTopLevel.TopLevelCapture.Lambda = CatchLambda();
ExfTopLevel.TopLevelCapture.Local = CatchLocal();
ExfTopLevel.TopLevelCapture.AsyncLambda = await CatchAsyncLambda();
ExfTopLevel.TopLevelCapture.AsyncLocal = await CatchAsyncLocal();

static Exception CatchLambda()
{
    try
    {
        Action act = () =>
        {
#line 4 "LambdaTop.cs"
            throw new InvalidOperationException("lam");
#line default
        };
        act();
        throw new InvalidOperationException("no");
    }
    catch (Exception ex)
    {
        return ex;
    }
}

static Exception CatchLocal()
{
    try
    {
        void Local()
        {
#line 3 "LocalTop.cs"
            throw new InvalidOperationException("loc");
#line default
        }

        Local();
        throw new InvalidOperationException("no");
    }
    catch (Exception ex)
    {
        return ex;
    }
}

static async Task<Exception> CatchAsyncLambda()
{
    try
    {
        Func<Task> act = async () =>
        {
            await Task.Yield();
#line 5 "AsyncLambda.cs"
            throw new InvalidOperationException("al");
#line default
        };
        await act();
        throw new InvalidOperationException("no");
    }
    catch (Exception ex)
    {
        return ex;
    }
}

static async Task<Exception> CatchAsyncLocal()
{
    try
    {
        async Task Helper()
        {
            await Task.Yield();
#line 6 "AsyncLocal.cs"
            throw new InvalidOperationException("hl");
#line default
        }

        await Helper();
        throw new InvalidOperationException("no");
    }
    catch (Exception ex)
    {
        return ex;
    }
}

namespace ExfTopLevel
{
    /// <summary>
    /// Holds the exceptions thrown by this top-level program.
    /// The test invokes <c>&lt;Main&gt;$</c> and reads these properties; the process entry is not started by the test host.
    /// </summary>
    public static class TopLevelCapture
    {
        /// <summary>The awaited top-level <c>InvalidOperationException</c>.</summary>
        public static Exception? Awaited { get; set; }

        /// <summary>A lambda thrown from the top-level body.</summary>
        public static Exception? Lambda { get; set; }

        /// <summary>A local function thrown from the top-level body.</summary>
        public static Exception? Local { get; set; }

        /// <summary>An async lambda thrown from the top-level body.</summary>
        public static Exception? AsyncLambda { get; set; }

        /// <summary>An async local function thrown from the top-level body.</summary>
        public static Exception? AsyncLocal { get; set; }
    }
}
