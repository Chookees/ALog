using System.Globalization;
using ALog.Core;

namespace ALog.Tests;

/// <summary>The template cache stops storing once it is full or the text is too long. Rendering still works.</summary>
public sealed class TemplateCacheTests
{
    /// <summary>
    /// Filling the cache to the cap leaves the next template unstored, and that template still renders.
    /// </summary>
    /// <returns>A task that finishes after the extra template is rendered.</returns>
    /// <remarks>
    /// The cache is process-wide, so the loop stops when the count is already at the cap.
    /// The loop is bounded by <see cref="MessageTemplate.MaxCachedTemplates"/>. Not thread-safe.
    /// </remarks>
    [Fact(Timeout = 30000)]
    public async Task FullCache_CompilesTheNextTemplateWithoutStoringIt()
    {
        await Task.CompletedTask;
        for (var i = 0; i < MessageTemplate.MaxCachedTemplates; i++)
        {
            if (MessageTemplate.CachedTemplateCount >= MessageTemplate.MaxCachedTemplates)
                break;

            var text = "cap" + i.ToString("D4", CultureInfo.InvariantCulture) + " {Id}";
            var rendered = MessageTemplate.Format(text, i);
            Assert.Contains(i.ToString(CultureInfo.InvariantCulture), rendered.Rendered, StringComparison.Ordinal);
        }

        Assert.Equal(MessageTemplate.MaxCachedTemplates, MessageTemplate.CachedTemplateCount);
        var extra = MessageTemplate.Format("beyond the cap {Id}", 7);
        Assert.Contains("7", extra.Rendered, StringComparison.Ordinal);
        Assert.Equal(MessageTemplate.MaxCachedTemplates, MessageTemplate.CachedTemplateCount);
    }

    /// <summary>A template longer than the length cap renders and is not stored.</summary>
    /// <returns>A task that finishes after the long template is rendered.</returns>
    /// <remarks>
    /// The text is one character over <see cref="MessageTemplate.MaxCachedTemplateLength"/> plus a hole.
    /// The count is read before and after. Not thread-safe. The loop that builds the text is the string constructor.
    /// </remarks>
    [Fact(Timeout = 30000)]
    public async Task LongTemplate_RendersAndIsNotStored()
    {
        await Task.CompletedTask;
        var before = MessageTemplate.CachedTemplateCount;
        var text = new string('x', MessageTemplate.MaxCachedTemplateLength) + " {Id}";
        Assert.True(text.Length > MessageTemplate.MaxCachedTemplateLength);
        var rendered = MessageTemplate.Format(text, 3);
        Assert.EndsWith("3", rendered.Rendered, StringComparison.Ordinal);
        Assert.Equal(before, MessageTemplate.CachedTemplateCount);
    }

    /// <summary>Threads filling the cache together cannot push the count past the cap.</summary>
    /// <returns>A task that finishes after every writer has returned.</returns>
    /// <remarks>
    /// Eight threads, two hundred templates each. The loop bounds are those two numbers.
    /// A render that drops the value throws, which fails the task. The count is read after they join.
    /// </remarks>
    [Fact(Timeout = 30000)]
    public async Task ConcurrentFill_DoesNotExceedTheCap()
    {
        const int threads = 8;
        const int each = 200;
        var tasks = new Task[threads];
        for (var t = 0; t < threads; t++)
        {
            var id = t;
            tasks[t] = Task.Run(() =>
            {
                for (var i = 0; i < each; i++)
                {
                    var text = "t" + id.ToString("00", CultureInfo.InvariantCulture)
                        + "i" + i.ToString("0000", CultureInfo.InvariantCulture)
                        + " {Id}";
                    var rendered = MessageTemplate.Format(text, i);
                    if (!rendered.Rendered.Contains(i.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal))
                        throw new InvalidOperationException("template did not render");
                }
            });
        }

        await Task.WhenAll(tasks);
        Assert.True(MessageTemplate.CachedTemplateCount <= MessageTemplate.MaxCachedTemplates);
    }
}
