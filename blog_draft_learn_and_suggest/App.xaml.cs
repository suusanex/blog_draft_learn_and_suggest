using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using blog_draft_learn_and_suggest.Activation;
using blog_draft_learn_and_suggest.Contracts.Services;
using blog_draft_learn_and_suggest.Helpers;
using blog_draft_learn_and_suggest.Services;
using blog_draft_learn_and_suggest.ViewModels;
using blog_draft_learn_and_suggest.Views;

namespace blog_draft_learn_and_suggest;

// To learn more about WinUI 3, see https://docs.microsoft.com/windows/apps/winui/winui3/.
public partial class App : Application
{
    // The .NET Generic Host provides dependency injection, configuration, logging, and other services.
    // https://docs.microsoft.com/dotnet/core/extensions/generic-host
    // https://docs.microsoft.com/dotnet/core/extensions/dependency-injection
    // https://docs.microsoft.com/dotnet/core/extensions/configuration
    // https://docs.microsoft.com/dotnet/core/extensions/logging
    public IHost Host
    {
        get;
    }

    public static T GetRequiredService<T>()
        where T : class
    {
        if ((App.Current as App)!.Host.Services.GetRequiredService(typeof(T)) is not T service)
        {
            throw new ArgumentException($"{typeof(T)} needs to be registered in ConfigureServices within App.xaml.cs.");
        }

        return service;
    }

    public static WindowEx MainWindow { get; } = new MainWindow();

    public static UIElement? AppTitlebar { get; set; }

    public App()
    {
        InitializeComponent();

        try
        {
            Host = Microsoft.Extensions.Hosting.Host.
            CreateDefaultBuilder().
            UseContentRoot(AppContext.BaseDirectory).
            ConfigureAppConfiguration((context, config) =>
            {
                config.AddJsonFile("appsettings.json", optional: true, reloadOnChange: true);
                // ユーザーシークレット（開発環境のみ）
                config.AddUserSecrets<App>(optional: true);
                // 環境変数も上書き可能
                config.AddEnvironmentVariables();
            }).
            ConfigureLogging(logging =>
            {
                logging.ClearProviders();
                logging.AddDebug();
                logging.AddConsole();
                logging.SetMinimumLevel(LogLevel.Debug);
            }).
            ConfigureServices((context, services) =>
            {
                // Default Activation Handler
                services.AddTransient<ActivationHandler<LaunchActivatedEventArgs>, DefaultActivationHandler>();

                // Services
                services.AddSingleton<IActivationService, ActivationService>();
                services.AddSingleton<IPageService, PageService>();
                services.AddSingleton<INavigationService, NavigationService>();

                // Retrieval (Azure AI Search)
                services.AddSingleton<blog_draft_learn_and_suggest.Services.RetrievalService>();

                // Views and ViewModels
                services.AddTransient<MainViewModel>();
                services.AddTransient<MainPage>();
                // ChatModel DI登録
                services.AddSingleton<blog_draft_learn_and_suggest.Models.ChatModel>();

            }).
            Build();
        }
        catch (Exception ex)
        {
            // Host構築中の例外をデバッグ出力に
            System.Diagnostics.Debug.WriteLine("Host build failed: " + ex);
            throw;
        }

        UnhandledException += App_UnhandledException;
    }

    private async void App_UnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        var dlg = new ContentDialog
        {
            Title = "Unhandled Exception",
            Content = e.Exception.ToString(),
            PrimaryButtonText = "Close"
        };
        dlg.XamlRoot = MainWindow.Content.XamlRoot;
        await dlg.ShowAsync();
    }

    protected async override void OnLaunched(LaunchActivatedEventArgs args)
    {
        base.OnLaunched(args);

        await App.GetRequiredService<IActivationService>().ActivateAsync(args);
    }
}
