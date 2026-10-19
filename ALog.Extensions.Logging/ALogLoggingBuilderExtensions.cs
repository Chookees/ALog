namespace ALog.Extensions.Logging;

using ALog.Config;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

public static class ALogLoggingBuilderExtensions
{
    // Paths in the message are these spellings, not the casing from the configuration file.
    private const string ConsolePath = ALogOptions.SectionName + ":Console";
    private const string FilePath = ALogOptions.SectionName + ":File";

    private const string OneSingleValueMessage =
        "Invalid ALog configuration at '{0}': expected a section with settings, but found a single value. Put the settings inside the section, as shown in the README.";

    private const string OneListMessage =
        "Invalid ALog configuration at '{0}': expected a section with settings, but found a list. Put the settings inside the section, as shown in the README.";

    private const string TwoPathMessage =
        "Invalid ALog configuration at '{0}' ({1}) and '{2}' ({3}): expected a section with settings at each path. Put the settings inside each section, as shown in the README.";

    /// <summary>How a writer section is shaped. <see cref="SectionKind.Valid"/> is left for the binder.</summary>
    private enum SectionKind
    {
        /// <summary>Missing, or a section of named settings.</summary>
        Valid = 0,

        /// <summary>The section has a value, including empty, whitespace, or a value plus children.</summary>
        SingleValue = 1,

        /// <summary>The section has no value and only numeric children, such as JSON <c>[true]</c>.</summary>
        List = 2
    }

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
    /// <c>ALog:Console</c> and <c>ALog:File</c> must be sections with settings, not a single value or a list.
    /// </summary>
    /// <param name="builder">The logging builder. Null is rejected.</param>
    /// <param name="configuration">Application configuration. Null is rejected. The <c>ALog</c> section is read once.</param>
    /// <returns>The same builder, so startup can chain.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="builder"/> or <paramref name="configuration"/> is null.</exception>
    /// <exception cref="InvalidOperationException">
    /// Thrown when <c>ALog:Console</c> or <c>ALog:File</c> is not a section of settings.
    /// One exception names every affected path, console before file, and does not include the value.
    /// One path says single value or list. Two paths put each kind in parentheses.
    /// </exception>
    /// <remarks>
    /// Writer settings are copied now. A later reload of <paramref name="configuration"/> does not add a writer.
    /// Does not write a log line. Call from host startup.
    /// </remarks>
    public static ILoggingBuilder AddALog(this ILoggingBuilder builder, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configuration);

        RejectMalformedSections(configuration);
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
    /// <c>ALog:Console</c> and <c>ALog:File</c> must be sections with settings, not a single value or a list.
    /// </summary>
    /// <param name="configuration">Application configuration. Null is rejected.</param>
    /// <returns>The bound options. The caller owns the instance.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="configuration"/> is null.</exception>
    /// <exception cref="InvalidOperationException">
    /// Thrown when <c>ALog:Console</c> or <c>ALog:File</c> is not a section of settings.
    /// The message is the same sentence <see cref="AddALog(ILoggingBuilder, IConfiguration)"/> throws.
    /// </exception>
    /// <remarks>
    /// Calls <see cref="RejectMalformedSections"/> once, then binds. Does not register a logger.
    /// Does not read the stored value into the message. Safe to call from startup.
    /// </remarks>
    public static ALogOptions GetALogOptions(this IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        RejectMalformedSections(configuration);
        var options = new ALogOptions();
        configuration.GetSection(ALogOptions.SectionName).Bind(options);
        return options;
    }

    /// <summary>
    /// Stops startup when <c>ALog:Console</c> or <c>ALog:File</c> is a single value or a list.
    /// A missing section is left for the binder. One exception names every affected path.
    /// </summary>
    /// <param name="configuration">Application configuration. Not null. Not modified.</param>
    /// <exception cref="InvalidOperationException">
    /// Thrown when at least one writer section is malformed. Console is named before file.
    /// The path text is always <see cref="ConsolePath"/> or <see cref="FilePath"/>.
    /// The stored value is not included.
    /// </exception>
    /// <remarks>
    /// Called from <see cref="AddALog(ILoggingBuilder, IConfiguration)"/> and <see cref="GetALogOptions"/>.
    /// Looks at two paths. Does not write a log line. Safe to call from the startup thread.
    /// </remarks>
    private static void RejectMalformedSections(IConfiguration configuration)
    {
        var console = Classify(configuration.GetSection(ConsolePath));
        var file = Classify(configuration.GetSection(FilePath));
        if (console == SectionKind.Valid && file == SectionKind.Valid)
            return;

        throw new InvalidOperationException(FormatMessage(console, file));
    }

    /// <summary>
    /// Builds the one-path or two-path sentence. Console is named before file.
    /// </summary>
    /// <param name="console">Shape of <see cref="ConsolePath"/>.</param>
    /// <param name="file">Shape of <see cref="FilePath"/>.</param>
    /// <returns>The full message. At least one argument is not <see cref="SectionKind.Valid"/>.</returns>
    /// <remarks>
    /// One path uses <see cref="OneSingleValueMessage"/> or <see cref="OneListMessage"/>.
    /// Two paths use <see cref="TwoPathMessage"/> with each kind in parentheses.
    /// </remarks>
    private static string FormatMessage(SectionKind console, SectionKind file)
    {
        var consoleBad = console != SectionKind.Valid;
        var fileBad = file != SectionKind.Valid;
        if (consoleBad && fileBad)
        {
            return TwoPathMessage
                .Replace("{0}", ConsolePath, StringComparison.Ordinal)
                .Replace("{1}", KindWord(console), StringComparison.Ordinal)
                .Replace("{2}", FilePath, StringComparison.Ordinal)
                .Replace("{3}", KindWord(file), StringComparison.Ordinal);
        }

        if (consoleBad)
            return OnePathMessage(ConsolePath, console);

        return OnePathMessage(FilePath, file);
    }

    /// <summary>The one-path sentence for <paramref name="kind"/>.</summary>
    /// <param name="path"><see cref="ConsolePath"/> or <see cref="FilePath"/>.</param>
    /// <param name="kind"><see cref="SectionKind.SingleValue"/> or <see cref="SectionKind.List"/>.</param>
    /// <returns>The full one-path message. The value is not included.</returns>
    /// <remarks>Does not read configuration. Safe to call from the startup thread.</remarks>
    private static string OnePathMessage(string path, SectionKind kind)
    {
        var template = kind == SectionKind.List ? OneListMessage : OneSingleValueMessage;
        return template.Replace("{0}", path, StringComparison.Ordinal);
    }

    /// <summary>The word used in parentheses when two paths are malformed.</summary>
    /// <param name="kind"><see cref="SectionKind.SingleValue"/> or <see cref="SectionKind.List"/>.</param>
    /// <returns><c>single value</c> or <c>list</c>.</returns>
    /// <remarks>Does not allocate a builder. Safe to call from the startup thread.</remarks>
    private static string KindWord(SectionKind kind)
        => kind == SectionKind.List ? "list" : "single value";

    /// <summary>
    /// A single value has <see cref="IConfigurationSection.Value"/> set, including empty, whitespace, and a value plus children.
    /// A list has no value and only numeric children, such as JSON <c>[true]</c>.
    /// JSON <c>"Console": { "Enabled": true }</c> is valid.
    /// </summary>
    /// <param name="section">The section to inspect. Not null. The path spelling is ignored.</param>
    /// <returns><see cref="SectionKind.Valid"/> when the section is missing or holds named settings.</returns>
    /// <remarks>
    /// A non-null value wins over children. The value text is not returned.
    /// At most <see cref="MaxSectionChildren"/> children are inspected. More than that is a list.
    /// </remarks>
    private static SectionKind Classify(IConfigurationSection section)
    {
        if (section.Value is not null)
            return SectionKind.SingleValue;

        var seen = 0;
        foreach (var child in section.GetChildren())
        {
            seen++;
            if (seen > MaxSectionChildren)
                return SectionKind.List;

            if (!IsIndexKey(child.Key))
                return SectionKind.Valid;
        }

        return seen > 0 ? SectionKind.List : SectionKind.Valid;
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
