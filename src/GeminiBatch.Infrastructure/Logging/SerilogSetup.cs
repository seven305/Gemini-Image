using Serilog;
using Serilog.Events;

namespace GeminiBatch.Infrastructure.Logging;

public static class SerilogSetup
{
    private const string Template =
        "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] [{AccountId}] [{JobId}] {Message:lj}{NewLine}{Exception}";

    /// <summary>Rolling daily file under <paramref name="logFolder"/> plus console/debug output.</summary>
    public static Serilog.ILogger CreateLogger(string logFolder = "logs")
    {
        Directory.CreateDirectory(logFolder);

        return new LoggerConfiguration()
            .MinimumLevel.Debug()
            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
            .Enrich.FromLogContext()
            .WriteTo.File(
                Path.Combine(logFolder, "geminibatch-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14,
                outputTemplate: Template,
                shared: true)
            .WriteTo.Console(outputTemplate: Template)
            .WriteTo.Debug(outputTemplate: Template)
            .CreateLogger();
    }
}
