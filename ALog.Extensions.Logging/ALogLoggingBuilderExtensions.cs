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
    /// Adds ALog using the <c>ALog</c> configuration section (JSON/env).
    /// </summary>
    public static ILoggingBuilder AddALog(this ILoggingBuilder builder, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configuration);

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
    /// </summary>
    public static ALogOptions GetALogOptions(this IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var options = new ALogOptions();
        configuration.GetSection(ALogOptions.SectionName).Bind(options);
        return options;
    }
}
