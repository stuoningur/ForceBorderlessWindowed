using System.Globalization;
using System.IO;

using Microsoft.Extensions.DependencyInjection;

using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Formatting.Display;

namespace ForceBorderlessWindowed.Services;

internal static class LoggingSetup
{
    public static string LogFolder { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ForceBorderlessWindowed",
        "logs");

    private const long FileSizeLimitBytes = 10 * 1024 * 1024;

    // With the UTC offset, so a shared log can't be misread. Source = the class name only (see ShortSourceEnricher).
    private const string OutputTemplate = "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Source}: {Message:lj}{NewLine}{Exception}";

    /// <summary>False if no log file is written in this run (unsafe folder or file not opened as administrator).</summary>
    public static bool FileLogActive { get; private set; }

    // Logs use the invariant culture, so they look the same regardless of the UI language
    public static IServiceCollection AddAppLogging(this IServiceCollection services) =>
        services.AddSerilog(config =>
        {
            config
                .MinimumLevel.Information()
                .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
                .Enrich.With<ShortSourceEnricher>()
                .WriteTo.Debug(formatProvider: CultureInfo.InvariantCulture);

            // As administrator, a junction in the log path could redirect our writes into a protected folder
            // (see ElevatedWriteGuard). Rare enough that running without a log file is the right answer.
            if (!ElevatedWriteGuard.IsSafeFolder(LogFolder))
            {
                return;
            }

            if (Environment.IsPrivilegedProcess)
            {
                AddElevatedLogFile(config);
            }
            else
            {
                config.WriteTo.File(
                    Path.Combine(LogFolder, "log-.txt"),
                    formatProvider: CultureInfo.InvariantCulture,
                    rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: 14,
                    fileSizeLimitBytes: FileSizeLimitBytes,
                    rollOnFileSizeLimit: true,
                    outputTemplate: OutputTemplate);
                FileLogActive = true;
            }
        });

    /// <summary>
    /// The rolling file sink opens new files by path whenever the day changes, without the guard's protection.
    /// As administrator, the log is therefore one file opened once at startup through the guard and kept
    /// open: same folder and name pattern as the rolling sink (so its clean-up in later non-elevated runs removes
    /// these files as well), but no switch to a new file at midnight. Same 10 MB limit per file, though: once
    /// reached, it stops writing until the next start.
    /// </summary>
    private static void AddElevatedLogFile(LoggerConfiguration config)
    {
        StreamWriter writer;
        try
        {
            // Creates the folder too, through no junction (see ElevatedWriteGuard)
            var path = Path.Combine(LogFolder, $"log-{DateTime.Now.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}.txt");
            writer = new StreamWriter(ElevatedWriteGuard.OpenForWrite(path, append: true)) { AutoFlush = true };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;   // no log file, as with an unsafe folder
        }

        // Never disposed on purpose: it lives as long as the app, AutoFlush already wrote everything
        config.WriteTo.Sink(new WriterSink(writer));
        FileLogActive = true;
    }

    /// <summary>
    /// For a failure before the app is up (see Program): a file of its own, "crash-....txt" in the log folder, so it
    /// can't collide with a log file the logger may already hold open. Not removed by the log clean-up, but rare
    /// enough not to pile up. False if it couldn't be written.
    /// </summary>
    public static bool TryWriteCrashFile(Exception ex)
    {
        try
        {
            var now = DateTimeOffset.Now;
            var path = Path.Combine(LogFolder, string.Create(CultureInfo.InvariantCulture, $"crash-{now:yyyyMMdd-HHmmss}.txt"));
            using var writer = new StreamWriter(ElevatedWriteGuard.OpenForWrite(path, append: true));
            writer.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"{now:yyyy-MM-dd HH:mm:ss.fff zzz} [FTL] Startup failed, version {AppInfo.DisplayVersion}, administrator: {Environment.IsPrivilegedProcess}"));
            writer.WriteLine(ex.ToString());
            return true;
        }
        catch (Exception writeError) when (writeError is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// "WindowTracker" instead of "ForceBorderlessWindowed.Services.WindowTracker": the namespace is the same on almost
    /// every line and only makes them harder to read. A template can't shorten a property, so it's a property of its own.
    /// </summary>
    private sealed class ShortSourceEnricher : ILogEventEnricher
    {
        public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
        {
            // Always set: a missing property would be printed as "{Source}"
            var source = logEvent.Properties.TryGetValue(Constants.SourceContextPropertyName, out var value)
                && value is ScalarValue { Value: string context }
                    ? context[(context.LastIndexOf('.') + 1)..]
                    : "-";
            logEvent.AddPropertyIfAbsent(propertyFactory.CreateProperty("Source", source));
        }
    }

    /// <summary>Writes formatted events to one open file, up to the size limit. Locked: several threads log at once.</summary>
    private sealed class WriterSink(StreamWriter writer) : ILogEventSink
    {
        private readonly MessageTemplateTextFormatter _formatter = new(OutputTemplate, CultureInfo.InvariantCulture);
        private readonly Lock _lock = new();
        private bool _full;

        public void Emit(LogEvent logEvent)
        {
            lock (_lock)
            {
                if (_full)
                {
                    return;
                }

                // The file's size so far, including earlier runs of the same day (opened for appending)
                if (writer.BaseStream.Length >= FileSizeLimitBytes)
                {
                    _full = true;
                    writer.WriteLine(string.Create(CultureInfo.InvariantCulture,
                        $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} [WRN] LoggingSetup: Log file reached {FileSizeLimitBytes / 1024 / 1024} MB, nothing more is written until the app is restarted"));
                    return;
                }

                _formatter.Format(logEvent, writer);
            }
        }
    }
}
