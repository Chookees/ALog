// The only TimeProvider.System in product code.
// Public constructors use this default. Tests pass a clock they can move.
// Next file: ALog/Core/BackgroundLogQueue.cs.

namespace ALog.Core;

using System.Diagnostics.CodeAnalysis;

/// <summary>
/// The clock the public constructors pass when the caller does not supply one.
/// </summary>
internal static class ProductionClock
{
    /// <summary>
    /// The process clock. Tests pass a different <see cref="TimeProvider"/>.
    /// </summary>
    /// <remarks>
    /// This getter is the only <see cref="TimeProvider.System"/> in product code.
    /// Does not throw. The value does not change.
    /// </remarks>
    [SuppressMessage(
        "ApiDesign",
        "RS0030",
        Justification = "Default clock when the app sets no TimeProvider; tests inject a FakeTimeProvider.")]
    internal static TimeProvider Default => TimeProvider.System;
}
