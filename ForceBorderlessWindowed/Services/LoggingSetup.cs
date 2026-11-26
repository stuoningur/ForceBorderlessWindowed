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

    private const string OutputTemplate = "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}";

    // Logs use the invariant culture, so they look the same regardless of the UI language
    public static IServiceCollection AddAppLogging(this IServiceCollection services) =>
        services.AddSerilog(config =>
        {
            config
                .MinimumLevel.Information()
                .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
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
                        $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [WRN] Log file reached {FileSizeLimitBytes / 1024 / 1024} MB, nothing more is written until the app is restarted"));
                    return;
                }

                _formatter.Format(logEvent, writer);
            }
        }
    }
}
