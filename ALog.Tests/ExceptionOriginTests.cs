using System.Globalization;
using ALog.Core;
using OriginNoPdb;

namespace ALog.Tests;

/// <summary>
/// The plain-text <c>at</c> segment and the JSON <c>source</c> name the same frame.
/// </summary>
/// <remarks>The culture case changes the process culture, so the class sits in the non-parallel culture collection.</remarks>
[Collection("Culture")]
public sealed class ExceptionOriginTests
{
    /// <summary>An async method, a lambda, and a generic method agree, including under de-DE.</summary>
    /// <returns>A task that finishes after the async throw is observed.</returns>
    /// <remarks>
    /// Each throw is caught in this test. The culture is restored afterwards.
    /// Not parallel-safe with other culture tests. No reflection is used.
    /// </remarks>
    [Fact(Timeout = 30000)]
    public async Task AsyncLambdaAndGeneric_AgreeWithJsonSource()
    {
        await AssertAgreeAsync(ThrowAsync);
        AssertAgree(ThrowLambda);
        AssertAgree(ThrowGeneric<string>);
        using (new CultureScope("de-DE"))
            AssertAgree(ThrowPlain);
    }

    /// <summary>An assembly without a PDB has no file and no line on either path, and the method still matches.</summary>
    /// <returns>A task that finishes after the throw is observed.</returns>
    /// <remarks>The helper project is compiled with <c>DebugType=none</c>. Not thread-safe. No reflection is used.</remarks>
    [Fact(Timeout = 30000)]
    public async Task NoPdb_AgreesAndOmitsFile()
    {
        await Task.CompletedTask;
        Exception? caught = null;
        try
        {
            Thrower.Throw();
        }
        catch (Exception ex)
        {
            caught = ex;
        }

        Assert.NotNull(caught);
        var text = ExceptionReport.FormatText(caught);
        var source = ExceptionReport.CreateBody(caught).Source;
        var at = AtSegment(text);
        if (source == null)
        {
            Assert.Null(at);
            return;
        }

        Assert.NotNull(at);
        Assert.Contains(source.Method, at, StringComparison.Ordinal);
        Assert.Null(source.File);
        Assert.Null(source.Line);
        Assert.DoesNotContain(" in ", at, StringComparison.Ordinal);
    }

    /// <summary>Runs <paramref name="thrower"/> and compares the two origin paths.</summary>
    /// <param name="thrower">Method that throws. Not null.</param>
    /// <returns>A task that finishes after the exception is caught.</returns>
    /// <remarks>Does not throw. A missing exception fails the assert. Not thread-safe.</remarks>
    private static async Task AssertAgreeAsync(Func<Task> thrower)
    {
        Exception? caught = null;
        try
        {
            await thrower();
        }
        catch (Exception ex)
        {
            caught = ex;
        }

        Assert.NotNull(caught);
        AssertSameOrigin(caught);
    }

    /// <summary>Runs <paramref name="thrower"/> and compares the two origin paths.</summary>
    /// <param name="thrower">Method that throws. Not null.</param>
    /// <remarks>Does not throw. A missing exception fails the assert. Not thread-safe.</remarks>
    private static void AssertAgree(Action thrower)
    {
        Exception? caught = null;
        try
        {
            thrower();
        }
        catch (Exception ex)
        {
            caught = ex;
        }

        Assert.NotNull(caught);
        AssertSameOrigin(caught);
    }

    /// <summary>
    /// The JSON source method, file, and line are in the plain-text <c>at</c> segment, or both paths have no origin.
    /// </summary>
    /// <param name="exception">Caught exception. Not null.</param>
    /// <remarks>Does not throw. A mismatch is an assertion failure. Not thread-safe.</remarks>
    private static void AssertSameOrigin(Exception exception)
    {
        var text = ExceptionReport.FormatText(exception);
        var source = ExceptionReport.CreateBody(exception).Source;
        var at = AtSegment(text);
        if (source == null)
        {
            Assert.True(at == null, text);
            return;
        }

        Assert.True(at != null, text);
        Assert.Contains(source.Method, at, StringComparison.Ordinal);
        if (source.File == null)
            Assert.DoesNotContain(" in ", at, StringComparison.Ordinal);
        else
            Assert.Contains(source.File, at, StringComparison.Ordinal);

        if (source.Line == null)
            Assert.DoesNotContain(":", at, StringComparison.Ordinal);
        else
            Assert.Contains(":" + source.Line.Value.ToString(CultureInfo.InvariantCulture), at, StringComparison.Ordinal);
    }

    /// <summary>The <c>at</c> segment after the last <c> | </c>, or null when the line has no origin.</summary>
    /// <param name="text">Plain-text exception segment. Not null.</param>
    /// <returns>The segment starting with <c>at </c>, or null.</returns>
    /// <remarks>Does not throw. A <c> | </c> that is not an origin returns null. Not thread-safe.</remarks>
    private static string? AtSegment(string text)
    {
        const string marker = " | at ";
        var index = text.LastIndexOf(marker, StringComparison.Ordinal);
        if (index < 0)
            return null;
        return text[(index + " | ".Length)..];
    }

    /// <summary>Throws after a yield so the frame is an async state machine.</summary>
    /// <returns>Does not return.</returns>
    /// <remarks>The await is the point. The caller catches the exception.</remarks>
    private static async Task ThrowAsync()
    {
        await Task.Yield();
        throw new InvalidOperationException("async-origin");
    }

    /// <summary>Throws from a lambda.</summary>
    /// <remarks>The caller catches the exception. The lambda is the frame under test.</remarks>
    private static void ThrowLambda()
    {
        Action thrower = () => throw new InvalidOperationException("lambda-origin");
        thrower();
    }

    /// <summary>Throws from a generic method.</summary>
    /// <typeparam name="T">The type argument that appears in the frame.</typeparam>
    /// <remarks>The caller catches the exception. The open method is the frame under test.</remarks>
    private static void ThrowGeneric<T>()
        => throw new InvalidOperationException("generic-origin");

    /// <summary>Throws from an ordinary method so a localized stack still has an origin.</summary>
    /// <remarks>The caller catches the exception. The message is not the origin.</remarks>
    private static void ThrowPlain()
        => throw new InvalidOperationException("plain-origin");

    /// <summary>Restores the current culture and UI culture.</summary>
    /// <remarks>Not thread-safe. Only the test thread constructs it.</remarks>
    private sealed class CultureScope : IDisposable
    {
        private readonly CultureInfo _culture;
        private readonly CultureInfo _ui;

        /// <summary>Sets both cultures to <paramref name="name"/>.</summary>
        /// <param name="name">Culture name. Not null.</param>
        /// <remarks>Does not throw when the culture is installed. <see cref="Dispose"/> restores the previous pair.</remarks>
        internal CultureScope(string name)
        {
            _culture = CultureInfo.CurrentCulture;
            _ui = CultureInfo.CurrentUICulture;
            var next = CultureInfo.GetCultureInfo(name);
            CultureInfo.CurrentCulture = next;
            CultureInfo.CurrentUICulture = next;
        }

        /// <summary>Puts the cultures back.</summary>
        /// <remarks>Does not throw.</remarks>
        public void Dispose()
        {
            CultureInfo.CurrentCulture = _culture;
            CultureInfo.CurrentUICulture = _ui;
        }
    }
}
