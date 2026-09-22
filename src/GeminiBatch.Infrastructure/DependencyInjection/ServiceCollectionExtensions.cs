using GeminiBatch.Application.Abstractions;
using GeminiBatch.Application.Options;
using GeminiBatch.Infrastructure.Accounts;
using GeminiBatch.Infrastructure.Fakes;
using GeminiBatch.Infrastructure.Gemini;
using GeminiBatch.Infrastructure.Logging;
using GeminiBatch.Infrastructure.Manifest;
using GeminiBatch.Infrastructure.Processing;
using GeminiBatch.Infrastructure.Prompts;
using GeminiBatch.Infrastructure.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;

namespace GeminiBatch.Infrastructure.DependencyInjection;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Real storage/manifest/sources plus the Playwright Gemini session. Set <c>Batch:UseFakeSession=true</c>
    /// (appsettings.json, or GEMINIBATCH_Batch__UseFakeSession=true) to swap in the offline fake instead.
    /// </summary>
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<BatchOptions>(configuration.GetSection(BatchOptions.SectionName));
        services.Configure<FakeSessionOptions>(configuration.GetSection(FakeSessionOptions.SectionName));
        services.Configure<AccountStoreOptions>(configuration.GetSection(AccountStoreOptions.SectionName));
        services.Configure<GeminiSessionOptions>(configuration.GetSection(GeminiSessionOptions.SectionName));

        var useFakeSession = configuration.GetValue($"{BatchOptions.SectionName}:{nameof(BatchOptions.UseFakeSession)}", false);
        if (useFakeSession)
        {
            services.AddSingleton<IGeminiSessionFactory, FakeGeminiSessionFactory>();
            services.AddSingleton<IAccountLoginService, UnavailableAccountLoginService>();
        }
        else
        {
            // One launcher for the whole app: it owns the Playwright driver process.
            services.AddSingleton<PlaywrightBrowserLauncher>();
            services.AddSingleton<IGeminiSessionFactory, PlaywrightGeminiSessionFactory>();
            services.AddSingleton<IAccountLoginService, PlaywrightAccountLoginService>();
        }

        services.AddSingleton<IImageStorage, FileSystemImageStorage>();
        services.AddSingleton<IImageProcessor, NullImageProcessor>();
        services.AddSingleton<IJobManifest, JsonJobManifest>();
        services.AddSingleton<IAccountStore, JsonAccountStore>();
        services.AddSingleton<IPromptSource, TextPromptSource>();

        var logFolder = configuration["Logging:Folder"] ?? "logs";
        services.AddLogging(builder =>
        {
            builder.ClearProviders();
            builder.AddSerilog(SerilogSetup.CreateLogger(logFolder), dispose: true);
        });

        return services;
    }
}
