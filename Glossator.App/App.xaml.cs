using System.IO;
using System.Windows;
using Glossator.AI.Configuration;
using Glossator.AI.Extensions;
using Glossator.Core.Data;
using Glossator.Core.Glossary;
using Glossator.Core.Models.Checkpoint;
using Glossator.Core.Models.Glossary;
using Glossator.Core.Rules;
using Glossator.Core.Serialization;
using Glossator.Core.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Glossator.App;

public partial class App : Application
{
    private IHost? _host;

    public static IServiceProvider Services { get; private set; } = null!;

    private async void Application_Startup(object sender, StartupEventArgs e)
    {
        try
        {
            var builder = Host.CreateApplicationBuilder(e.Args);

            builder.Configuration
                .SetBasePath(AppContext.BaseDirectory)
                .AddJsonFile("appsettings.example.json", optional: true, reloadOnChange: false)
                .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
                .AddJsonFile("appsettings.Development.json", optional: true, reloadOnChange: true)
                .AddJsonFile(GlossatorPaths.GetUserSettingsPath(), optional: true, reloadOnChange: true)
                .AddEnvironmentVariables();

            var config = builder.Configuration;
            var themeService = new Glossator.App.Services.StudioThemeService();
            var preferDark = !string.Equals(config["Studio:Theme"], "Light", StringComparison.OrdinalIgnoreCase);
            themeService.ApplyTheme(preferDark);

            // OpenTelemetry Kurulumu (Docker Aspire Dashboard)
            AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true);
            Environment.SetEnvironmentVariable("OTEL_INSTRUMENTATION_GENAI_CAPTURE_MESSAGE_CONTENT", "true");

            var otelEndpoint = config["OpenTelemetry:Endpoint"];
            if (string.IsNullOrWhiteSpace(otelEndpoint)) otelEndpoint = "http://localhost:4317";

            var configuredServiceName = config["OpenTelemetry:ServiceName"];
            var serviceName = string.IsNullOrWhiteSpace(configuredServiceName) ||
                              configuredServiceName.Equals("Glossator.Cli", StringComparison.OrdinalIgnoreCase)
                ? "Glossator.Studio"
                : configuredServiceName;

            builder.Logging.AddOpenTelemetry(logging =>
            {
                logging.IncludeFormattedMessage = true;
                logging.IncludeScopes = true;
                logging.SetResourceBuilder(ResourceBuilder.CreateDefault().AddService(serviceName));
                logging.AddOtlpExporter(opt =>
                {
                    opt.Endpoint = new Uri(otelEndpoint);
                    opt.Protocol = OpenTelemetry.Exporter.OtlpExportProtocol.Grpc;
                    opt.TimeoutMilliseconds = 1500;
                });
            });

            builder.Services.AddOpenTelemetry()
                .ConfigureResource(resource => resource.AddService(serviceName))
                .WithTracing(tracing => tracing
                    .AddSource(Glossator.AI.Diagnostics.GlossatorDiagnostics.SourceName)
                    .AddSource("Microsoft.Agents.*")
                    .AddSource("Microsoft.Agents.AI.Workflows*")
                    .AddSource("Experimental.Microsoft.Extensions.AI*")
                    .AddHttpClientInstrumentation()
                    .AddOtlpExporter(opt =>
                    {
                        opt.Endpoint = new Uri(otelEndpoint);
                        opt.Protocol = OpenTelemetry.Exporter.OtlpExportProtocol.Grpc;
                        opt.TimeoutMilliseconds = 1500;
                    }))
                .WithMetrics(metrics => metrics
                    .AddMeter(Glossator.AI.Diagnostics.GlossatorDiagnostics.SourceName)
                    .AddMeter("Microsoft.Agents.*")
                    .AddMeter("Microsoft.Agents.AI.Workflows*")
                    .AddMeter("Experimental.Microsoft.Extensions.AI*")
                    .AddHttpClientInstrumentation()
                    .AddOtlpExporter(opt =>
                    {
                        opt.Endpoint = new Uri(otelEndpoint);
                        opt.Protocol = OpenTelemetry.Exporter.OtlpExportProtocol.Grpc;
                        opt.TimeoutMilliseconds = 1500;
                    }));

            // AI Yapılandırması
            builder.Services.Configure<GlossatorAiOptions>(config.GetSection("GlossatorAi"));
            builder.Services.AddGlossatorAiClients();
            builder.Services.AddSingleton<IExchangeRateService, ExchangeRateService>();
            builder.Services.AddSingleton<Glossator.App.Infrastructure.UserSettingsService>();
            builder.Services.AddSingleton<Glossator.App.Infrastructure.StudioTelemetryMonitor>();

            // Veritabanı ve Sözlük Servisleri
            var defaultDbPath = GlossatorPaths.GetMasterDatabasePath();
            var connectionFactory = new SqliteConnectionFactory(defaultDbPath);
            builder.Services.AddSingleton<ISqliteConnectionFactory>(connectionFactory);

            var repo = new SqliteGlossaryRepository(connectionFactory);
            builder.Services.AddSingleton(repo);
            builder.Services.AddSingleton<IGlossarySerializationService, GlossarySerializationService>();
            builder.Services.AddSingleton<CheckpointStore>();

            // Scope Bağımlılıkları
            builder.Services.AddScoped(_ => new TermUsageTracker("default"));
            builder.Services.AddScoped<TermGlossingPostProcessor>();
            builder.Services.AddSingleton<Glossator.App.Engine.StudioLocalizationEngine>();

            // Servisler ve MVVM ViewModel Katmanı
            builder.Services.AddSingleton(themeService);
            builder.Services.AddSingleton<Glossator.App.Services.BannerService>();
            builder.Services.AddSingleton<Glossator.App.Services.StudioDialogService>();
            builder.Services.AddSingleton<Glossator.App.Services.IStudioDialogService>(sp =>
                sp.GetRequiredService<Glossator.App.Services.StudioDialogService>());

            builder.Services.AddSingleton<Glossator.App.ViewModels.LibraryViewModel>();
            builder.Services.AddSingleton<Glossator.App.ViewModels.BookWorkspaceViewModel>();
            builder.Services.AddSingleton<Glossator.App.ViewModels.GlossaryViewModel>();
            builder.Services.AddSingleton<Glossator.App.ViewModels.SettingsViewModel>();
            builder.Services.AddSingleton<Glossator.App.ViewModels.TelemetryViewModel>();
            builder.Services.AddSingleton<Glossator.App.ViewModels.AboutViewModel>();
            builder.Services.AddSingleton<Glossator.App.ViewModels.ShellViewModel>();

            // Windows UI
            builder.Services.AddSingleton<Glossator.App.Views.ShellWindow>();

            _host = builder.Build();
            Services = _host.Services;

            await _host.StartAsync();

            // Başlangıçta Master Sözlük seed & temizlik (CLI ile uyumlu)
            await repo.SeedDefaultTurkishTranslationsAsync();

            var shellWindow = _host.Services.GetRequiredService<Glossator.App.Views.ShellWindow>();
            if (string.Equals(Environment.GetEnvironmentVariable("GLOSSATOR_SMOKE_TEST"), "1", StringComparison.Ordinal))
            {
                DispatcherUnhandledException += (_, args) =>
                {
                    try
                    {
                        var logPath = Path.Combine(GlossatorPaths.GetGlossatorHomeDirectory(), "startup_error.log");
                        File.WriteAllText(logPath, args.Exception.ToString());
                    }
                    catch { }
                    args.Handled = true;
                    Shutdown(-1);
                };

                shellWindow.Show();
                await Task.Delay(1200);
                var shellVm = _host.Services.GetRequiredService<Glossator.App.ViewModels.ShellViewModel>();
                foreach (var page in Enum.GetValues<Glossator.App.ViewModels.AppPage>())
                {
                    shellVm.CurrentPage = page;
                    await Task.Delay(150);
                }
                themeService.ApplyTheme(!preferDark);
                await Task.Delay(120);
                themeService.ApplyTheme(preferDark);
                Shutdown(0);
                return;
            }

            shellWindow.Show();
        }
        catch (Exception ex)
        {
            try
            {
                var logPath = System.IO.Path.Combine(GlossatorPaths.GetGlossatorHomeDirectory(), "startup_error.log");
                await System.IO.File.WriteAllTextAsync(logPath, ex.ToString());
            }
            catch { }

            if (!string.Equals(Environment.GetEnvironmentVariable("GLOSSATOR_SMOKE_TEST"), "1", StringComparison.Ordinal))
            {
                var detail = ex.InnerException != null ? $"{ex.Message}\n\nDetay: {ex.InnerException.Message}" : ex.Message;
                MessageBox.Show(
                    $"Glossator Studio başlatılırken kritik bir hata oluştu:\n\n{detail}",
                    "Başlatma Hatası",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
            Shutdown(-1);
        }
    }

    private void Application_Exit(object sender, ExitEventArgs e)
    {
        var host = Interlocked.Exchange(ref _host, null);
        if (host == null)
        {
            return;
        }

        // Yerel SQLite checkpoint deposunu bekletmeden güvenli boşalt, ardından
        // Aspire/OTLP kapalıyken arayüzün asılı kalmaması için host durdurmasını kısa tut.
        try
        {
            Task.Run(async () =>
            {
                try
                {
                    if (host.Services.GetService<CheckpointStore>() is { } checkpointStore)
                    {
                        await checkpointStore.DisposeAsync().ConfigureAwait(false);
                    }
                }
                catch { }

                try
                {
                    host.Services.GetService<Glossator.App.Infrastructure.StudioTelemetryMonitor>()?.Dispose();
                }
                catch { }
            }).Wait(TimeSpan.FromSeconds(1));

            Task.Run(async () =>
            {
                try
                {
                    await host.StopAsync(TimeSpan.FromMilliseconds(250)).ConfigureAwait(false);
                }
                catch { }

                try
                {
                    if (host is IAsyncDisposable asyncDisposable)
                    {
                        await asyncDisposable.DisposeAsync().ConfigureAwait(false);
                    }
                    else
                    {
                        host.Dispose();
                    }
                }
                catch { }
            }).Wait(TimeSpan.FromMilliseconds(250));
        }
        catch
        {
            // Çıkışta sessiz kapanış sağla
        }
    }
}
