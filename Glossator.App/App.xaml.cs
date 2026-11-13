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
    private bool _globalExceptionHandlersRegistered;

    public static IServiceProvider Services { get; private set; } = null!;

    private async void Application_Startup(object sender, StartupEventArgs e)
    {
        var isSmokeTest = string.Equals(Environment.GetEnvironmentVariable("GLOSSATOR_SMOKE_TEST"), "1", StringComparison.Ordinal);
        RegisterGlobalExceptionHandlers(isSmokeTest);

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
            // Ham prompt/çeviri içerikleri gizlilik gereği varsayılan olarak kapalıdır; yalnızca yapılandırmayla açılır.
            AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true);
            var captureSensitive = config.GetValue<bool>("OpenTelemetry:CaptureMessageContent")
                                   || config.GetValue<bool>("GlossatorAi:EnableSensitiveTelemetry");
            if (captureSensitive)
            {
                Environment.SetEnvironmentVariable("OTEL_INSTRUMENTATION_GENAI_CAPTURE_MESSAGE_CONTENT", "true");
            }

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

            if (_host.Services.GetService<ILogger<App>>() is { } appLogger)
            {
                Glossator.App.ViewModels.AsyncRelayCommand.UnhandledExceptionHandler = ex =>
                    appLogger.LogError(ex, "İşlenmemiş AsyncRelayCommand hatası");
            }

            var shellWindow = _host.Services.GetRequiredService<Glossator.App.Views.ShellWindow>();
            if (isSmokeTest)
            {
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
            WriteCrashLog("startup_error.log", ex);

            if (!isSmokeTest)
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

    private void RegisterGlobalExceptionHandlers(bool isSmokeTest)
    {
        if (_globalExceptionHandlersRegistered)
            return;

        _globalExceptionHandlersRegistered = true;

        DispatcherUnhandledException += (_, args) =>
        {
            if (args.Exception is OutOfMemoryException or StackOverflowException)
                return;

            TryLogHostException("DispatcherUnhandledException", args.Exception);
            WriteCrashLog("unhandled_error.log", args.Exception);
            args.Handled = true;

            if (isSmokeTest)
            {
                Shutdown(-1);
                return;
            }

            MessageBox.Show(
                $"Beklenmeyen bir arayüz hatası yakalandı:\n\n{args.Exception.Message}\n\nAyrıntılar ~/.glossator/unhandled_error.log dosyasına yazıldı.",
                "Beklenmeyen Hata",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex)
            {
                TryLogHostException("AppDomain.UnhandledException", ex);
                WriteCrashLog("unhandled_error.log", ex);
            }
        };

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            TryLogHostException("TaskScheduler.UnobservedTaskException", args.Exception);
            WriteCrashLog("unhandled_error.log", args.Exception);
            args.SetObserved();
        };
    }

    private void TryLogHostException(string source, Exception ex)
    {
        try
        {
            _host?.Services.GetService<ILogger<App>>()?.LogError(ex, "Global yakalanan istisna ({Source})", source);
        }
        catch { }
    }

    private static void WriteCrashLog(string fileName, Exception ex)
    {
        try
        {
            var logPath = Path.Combine(GlossatorPaths.GetGlossatorHomeDirectory(), fileName);
            File.WriteAllText(logPath, $"[{DateTimeOffset.Now:O}] {ex}{Environment.NewLine}");
        }
        catch { }
    }

    /// <summary>
    /// Pencere kapanışı sırasında CheckpointStore, OTel provider ve DI Host'u UI'ı kilitlemeden async kapatır.
    /// İkinci çağrılar idempotent biçimde no-op döner.
    /// </summary>
    internal async Task ShutdownHostAsync()
    {
        var host = Interlocked.Exchange(ref _host, null);
        if (host == null)
        {
            return;
        }

        try
        {
            if (host.Services.GetService<CheckpointStore>() is { } checkpointStore)
            {
                using var disposeCts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                await checkpointStore.DisposeAsync().AsTask().WaitAsync(disposeCts.Token).ConfigureAwait(false);
            }
        }
        catch { }

        try
        {
            host.Services.GetService<Glossator.App.Infrastructure.StudioTelemetryMonitor>()?.Dispose();
        }
        catch { }

        try
        {
            host.Services.GetService<TracerProvider>()?.ForceFlush(1000);
            host.Services.GetService<MeterProvider>()?.ForceFlush(1000);
        }
        catch { }

        try
        {
            await host.StopAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
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
    }

    private void Application_Exit(object sender, ExitEventArgs e)
    {
        // Normal kapanışta ShellWindow_Closing ShutdownHostAsync()'i çoktan tamamlamıştır.
        // Bu fallback yalnızca pencere dışından Shutdown(...) çağrıldığı durumlarda (örn. smoke test) devreye girer.
        if (_host == null)
        {
            return;
        }

        try
        {
            Task.Run(ShutdownHostAsync).Wait(TimeSpan.FromSeconds(3));
        }
        catch
        {
            // Çıkışta sessiz kapanış sağla
        }
    }
}
