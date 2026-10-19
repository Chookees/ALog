namespace ALog.Extensions.Logging;

using ALog.Config;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

public static class ALogLoggingBuilderExtensions
{
    // One sentence for one path and for both. {0} is the path list.
    // Two paths are provisional: 'ALog:Console', 'ALog:File' until UX confirms the wording.
    private const string SingleValueMessage =
        "Invalid ALog configuration at '{0}': expected a section with settings, but found a single value. Put the settings inside the section, as shown in the README.";

    private const int MaxSectionChildren = 32;

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
    /// Thrown when <c>ALog:Console</c> or <c>ALog:File</c> is not a section of settings.
    /// One exception names every affected path, console before file, and does not include the value.
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
    /// <c>ALog:Console</c> and <c>ALog:File</c> must be sections with settings, not single values.
    /// </summary>
    /// <param name="configuration">Application configuration. Null is rejected.</param>
    /// <returns>The bound options. The caller owns the instance.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="configuration"/> is null.</exception>
    /// <exception cref="InvalidOperationException">
    /// Thrown when <c>ALog:Console</c> or <c>ALog:File</c> is not a section of settings.
    /// The message is the same sentence <see cref="AddALog(ILoggingBuilder, IConfiguration)"/> throws.
    /// </exception>
    /// <remarks>
    /// Calls <see cref="RejectSingleValueSections"/> once, then binds. Does not register a logger.
    /// Does not read the stored value into the message. Safe to call from startup.
    /// </remarks>
    public static ALogOptions GetALogOptions(this IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        RejectSingleValueSections(configuration);
        var options = new ALogOptions();
        configuration.GetSection(ALogOptions.SectionName).Bind(options);
        return options;
    }

    /// <summary>
    /// Stops startup when <c>ALog:Console</c> or <c>ALog:File</c> is not a section of settings.
    /// A missing section is left for the binder. One exception names every affected path.
    /// </summary>
    /// <param name="configuration">Application configuration. Not null. Not modified.</param>
    /// <exception cref="InvalidOperationException">
    /// Thrown when at least one writer section is malformed. Console is named before file.
    /// The text is <see cref="SingleValueMessage"/>. The stored value is not included.
    /// </exception>
    /// <remarks>
    /// Called from <see cref="AddALog(ILoggingBuilder, IConfiguration)"/> and <see cref="GetALogOptions"/>.
    /// Looks at two paths. Does not write a log line. Safe to call from the startup thread.
    /// </remarks>
    private static void RejectSingleValueSections(IConfiguration configuration)
    {
        var console = ALogOptions.SectionName + ":Console";
        var file = ALogOptions.SectionName + ":File";
        var consoleBad = IsMalformed(configuration.GetSection(console));
        var fileBad = IsMalformed(configuration.GetSection(file));
        if (!consoleBad && !fileBad)
            return;

        var listed = console;
        if (consoleBad && fileBad)
            listed = console + "', '" + file;
        else if (fileBad)
            listed = file;

        throw new InvalidOperationException(SingleValueMessage.Replace("{0}", listed, StringComparison.Ordinal));
    }

    /// <summary>
    /// A malformed section has a value, is an array of numeric children, or has both a value and children.
    /// JSON <c>"Console": true</c>, <c>""</c>, whitespace, and <c>[true]</c> are malformed.
    /// JSON <c>"Console": { "Enabled": true }</c> is not.
    /// </summary>
    /// <param name="section">The section to inspect. Not null.</param>
    /// <returns>True when startup must stop. False when the section is missing or holds named settings.</returns>
    /// <remarks>
    /// A non-null value is enough, including empty and whitespace. The value text is not returned.
    /// At most <see cref="MaxSectionChildren"/> children are inspected.
    /// </remarks>
    private static bool IsMalformed(IConfigurationSection section)
    {
        if (section.Value is not null)
            return true;

        var seen = 0;
        foreach (var child in section.GetChildren())
        {
            seen++;
            if (seen > MaxSectionChildren)
                return true;

            if (!IsIndexKey(child.Key))
                return false;
        }

        return seen > 0;
    }

    /// <summary>
    /// An array child is named with digits only, such as <c>0</c> for JSON <c>[true]</c>.
    /// </summary>
    /// <param name="key">Child key. Not null. Empty is not an index.</param>
    /// <returns>True when every character is a digit and the key is at most 9 characters.</returns>
    /// <remarks>Does not parse the number. A named setting such as <c>Enabled</c> is not an index.</remarks>
    private static bool IsIndexKey(string key)
    {
        if (key.Length == 0 || key.Length > 9)
            return false;

        for (var i = 0; i < key.Length; i++)
        {
            var digit = key[i];
            if (digit < '0' || digit > '9')
                return false;
        }

        return true;
    }
}
