namespace ALog.Extensions.Logging;

using ALog.Config;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

public static class ALogLoggingBuilderExtensions
{
    /// <summary>
    /// Adds ALog as an <see cref="ILoggerProvider"/> using an explicit <see cref="LoggerConfig"/>.
    /// </summary>
    public static ILoggingBuilder AddALog(this ILoggingBuilder builder, LoggerConfig config)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(config);
        builder.Services.AddSingleton<ILoggerProvider>(sp =>
            new ALogLoggerProvider(config, ownsEngine: true, sp.GetService<IOptionsMonitor<LoggerFilterOptions>>()));
        return builder;
    }

    /// <summary>
    /// Adds ALog using the <c>ALog</c> configuration section (JSON or environment).
    /// <c>ALog:Console</c> and <c>ALog:File</c> must be sections with settings, not single values.
    /// </summary>
    /// <param name="builder">The logging builder. Null is rejected.</param>
    /// <param name="configuration">Application configuration. Null is rejected. The <c>ALog</c> section is read once.</param>
    /// <returns>The same builder, so startup can chain.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="builder"/> or <paramref name="configuration"/> is null.</exception>
    /// <exception cref="InvalidOperationException">
    /// Thrown when <c>ALog:Console</c> or <c>ALog:File</c> is a single value.
    /// The message names the path and does not include the value, because that value can be a secret.
    /// </exception>
    /// <remarks>
    /// Writer settings are copied now. A later reload of <paramref name="configuration"/> does not add a writer.
    /// Does not write a log line. Call from host startup.
    /// </remarks>
    public static ILoggingBuilder AddALog(this ILoggingBuilder builder, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configuration);

        RejectSingleValueSections(configuration);
        var options = new ALogOptions();
        configuration.GetSection(ALogOptions.SectionName).Bind(options);
        var config = LoggerConfigFactory.FromOptions(options);
        return builder.AddALog(config);
    }

    /// <summary>
    /// Adds ALog using a custom options factory (e.g. after binding + extra writers).
    /// </summary>
    public static ILoggingBuilder AddALog(this ILoggingBuilder builder, Action<ALogOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configure);

        var options = new ALogOptions();
        configure(options);
        return builder.AddALog(LoggerConfigFactory.FromOptions(options));
    }

    /// <summary>
    /// Binds <see cref="ALogOptions"/> from configuration without registering MEL.
    /// A single value at <c>ALog:Console</c> or <c>ALog:File</c> is not rejected here.
    /// </summary>
    /// <param name="configuration">Application configuration. Null is rejected.</param>
    /// <returns>The bound options. Writer sections that are single values stay at their defaults.</returns>
    /// <remarks>
    /// <see cref="RejectSingleValueSections"/> is the startup check. Putting it on this method is the one call before <c>Bind</c>.
    /// That call is not made yet.
    /// </remarks>
    public static ALogOptions GetALogOptions(this IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var options = new ALogOptions();
        configuration.GetSection(ALogOptions.SectionName).Bind(options);
        return options;
    }

    /// <summary>
    /// Stops startup when <c>ALog:Console</c> or <c>ALog:File</c> is a single value.
    /// A missing section and a section that has children are left for the binder.
    /// </summary>
    /// <param name="configuration">Application configuration. Not null. Not modified.</param>
    /// <exception cref="InvalidOperationException">Thrown for the first single-value section, console before file.</exception>
    /// <remarks>
    /// Called from <see cref="AddALog(ILoggingBuilder, IConfiguration)"/> before options are bound.
    /// The two paths are the only writer sections <see cref="ALogOptions"/> binds.
    /// Does not allocate beyond the section lookup. Safe to call from the startup thread.
    /// </remarks>
    private static void RejectSingleValueSections(IConfiguration configuration)
    {
        RejectSingleValue(configuration, ALogOptions.SectionName + ":Console");
        RejectSingleValue(configuration, ALogOptions.SectionName + ":File");
    }

    /// <summary>
    /// Throws when <paramref name="path"/> stores one value and has no child settings.
    /// </summary>
    /// <param name="configuration">Application configuration. Not null.</param>
    /// <param name="path">Section path, <c>ALog:Console</c> or <c>ALog:File</c>.</param>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the section is a single value. The message is <see cref="SingleValueMessage"/>.
    /// </exception>
    /// <remarks>Does not throw for a missing key. A child key means the caller put settings inside the section.</remarks>
    private static void RejectSingleValue(IConfiguration configuration, string path)
    {
        var section = configuration.GetSection(path);
        if (!IsSingleValue(section))
            return;

        throw new InvalidOperationException(SingleValueMessage(path));
    }

    /// <summary>
    /// A single value has non-whitespace text and no children. JSON <c>"Console": true</c> is that shape.
    /// JSON <c>"Console": { "Enabled": true }</c> has children and a null value.
    /// </summary>
    /// <param name="section">The section to inspect. Not null.</param>
    /// <returns>True when startup must stop. False when the section is missing, blank, or has settings.</returns>
    /// <remarks>
    /// Empty and whitespace values are not classified: the binder already ignores them, and this method leaves that result.
    /// Looks at the first child only. Does not read the value into the result.
    /// </remarks>
    private static bool IsSingleValue(IConfigurationSection section)
    {
        // Blank values stay with the binder. A non-whitespace value with no child is the scalar "Console": true.
        if (string.IsNullOrWhiteSpace(section.Value))
            return false;

        foreach (var child in section.GetChildren())
        {
            _ = child;
            return false;
        }

        return true;
    }

    /// <summary>
    /// The startup sentence for a single value stored at <paramref name="path"/>.
    /// </summary>
    /// <param name="path">Section path inserted between quotes. Not null.</param>
    /// <returns>The full message. The configuration value is not part of it.</returns>
    /// <remarks>Keep this sentence identical to the M1 text. Callers compare it exactly.</remarks>
    private static string SingleValueMessage(string path)
    {
        return "Invalid ALog configuration at '" + path
            + "': expected a section with settings, but found a single value. Put the settings inside the section, as shown in the README.";
    }
}
