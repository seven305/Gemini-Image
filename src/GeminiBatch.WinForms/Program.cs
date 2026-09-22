using GeminiBatch.Application;
using GeminiBatch.Application.Options;
using GeminiBatch.Infrastructure.DependencyInjection;
using GeminiBatch.Infrastructure.Fakes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GeminiBatch.WinForms;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();

        // Content root = exe folder so appsettings.json / accounts.json resolve regardless of the launch cwd.
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            Args = args,
            ContentRootPath = AppContext.BaseDirectory,
        });
        Directory.SetCurrentDirectory(AppContext.BaseDirectory);

        builder.Configuration.AddEnvironmentVariables("GEMINIBATCH_");
        builder.Services.AddApplication();
        builder.Services.AddInfrastructure(builder.Configuration);
        builder.Services.AddTransient<MainForm>();

        using var host = builder.Build();
        host.Start();
        LogEffectiveOptions(host.Services);
        try
        {
            System.Windows.Forms.Application.Run(host.Services.GetRequiredService<MainForm>());
        }
        finally
        {
            host.StopAsync().GetAwaiter().GetResult();
        }
    }

    private static void LogEffectiveOptions(IServiceProvider services)
    {
        var logger = services.GetRequiredService<ILogger<MainForm>>();
        var batch = services.GetRequiredService<IOptions<BatchOptions>>().Value;
        var fake = services.GetRequiredService<IOptions<FakeSessionOptions>>().Value;
        logger.LogInformation("Effective Batch options: {@Batch}", batch);
        logger.LogInformation("Effective Fake options: {@Fake}", fake);
    }
}
