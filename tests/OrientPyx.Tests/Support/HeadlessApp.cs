using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Headless;
using Avalonia.Threading;
using OrientPyx.Presentation;
using OrientPyx.Presentation.ViewModels;
using OrientPyx.Presentation.Views;
using Xunit;

namespace OrientPyx.Tests.Support;

/// <summary>
/// The real application (<see cref="App"/>, its DI composition, <see cref="MainWindow"/>) running on
/// Avalonia's headless platform with Skia drawing, so the tests can drive the view models, pump the UI
/// thread and capture rendered frames to <see cref="TestEnvironment.ResultsDir"/>. Started once per test
/// run (collection fixture) over the <see cref="TestEnvironment"/> data root.
/// </summary>
public sealed class HeadlessApp : IAsyncLifetime
{
    private readonly CancellationTokenSource _stop = new();
    private readonly TaskCompletionSource<bool> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Thread? _uiThread;

    public MainWindow Window { get; private set; } = null!;

    public MainWindowViewModel Vm { get; private set; } = null!;

    /// <summary>Exceptions the UI thread reported through <see cref="Dispatcher.UnhandledException"/>.</summary>
    public List<Exception> UiErrors { get; } = [];

    public async Task InitializeAsync()
    {
        _ = TestEnvironment.Instance; // data root + databases + competitions, before the app opens anything

        _uiThread = new Thread(RunUi) { Name = "OrientPyx headless UI", IsBackground = true };
        _uiThread.Start();
        await _ready.Task.WaitAsync(TimeSpan.FromSeconds(60));

        // The app restores the last session or shows the competition picker; a fresh data root ⇒ the picker.
        await WaitUntilAsync(() => Vm.CurrentView is EventSelectionViewModel && !Vm.Busy.IsBusy, TimeSpan.FromSeconds(60));
    }

    private void RunUi()
    {
        try
        {
            var lifetime = new ClassicDesktopStyleApplicationLifetime { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            AppBuilder.Configure<App>()
                .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
                .UseSkia()
                .WithInterFont()
                .SetupWithLifetime(lifetime);

            Dispatcher.UIThread.UnhandledException += (_, e) => UiErrors.Add(e.Exception);

            Window = (MainWindow)lifetime.MainWindow!;
            Vm = (MainWindowViewModel)Window.DataContext!;
            Window.Show();
            _ready.TrySetResult(true);
            Dispatcher.UIThread.MainLoop(_stop.Token);
        }
        catch (Exception ex)
        {
            _ready.TrySetException(ex);
        }
    }

    public Task DisposeAsync()
    {
        _stop.Cancel();
        _uiThread?.Join(TimeSpan.FromSeconds(10));
        return Task.CompletedTask;
    }

    /// <summary>Runs <paramref name="action"/> on the UI thread.</summary>
    public Task OnUiAsync(Func<Task> action) => Dispatcher.UIThread.InvokeAsync(action);

    public Task<T> OnUiAsync<T>(Func<T> func) => Dispatcher.UIThread.InvokeAsync(func).GetTask();

    public Task OnUiAsync(Action action) => Dispatcher.UIThread.InvokeAsync(action).GetTask();

    /// <summary>Pumps the UI thread (and the headless render timer) until the condition holds.</summary>
    public async Task WaitUntilAsync(Func<bool> condition, TimeSpan? timeout = null, string? what = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(30));
        while (true)
        {
            var done = await OnUiAsync(() =>
            {
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                return condition();
            });
            if (done)
                return;
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"Condition not met in time: {what ?? "(unnamed)"}");
            await Task.Delay(50);
        }
    }

    /// <summary>Waits for the busy overlay to clear and settles layout/render.</summary>
    public async Task SettleAsync()
    {
        await WaitUntilAsync(() => !Vm.Busy.IsBusy, what: "busy overlay cleared");
        await OnUiAsync(() =>
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
        });
    }

    /// <summary>Renders the main window and saves it as <c>&lt;name&gt;.png</c> in the run's results folder.</summary>
    public async Task<string> ScreenshotAsync(string name)
    {
        var path = Path.Combine(TestEnvironment.ResultsDir, $"{name}.png");
        await OnUiAsync(() =>
        {
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            using var frame = Window.CaptureRenderedFrame()
                              ?? throw new InvalidOperationException("The headless window produced no frame.");
            frame.Save(path);
        });
        return path;
    }
}

[CollectionDefinition(Name)]
public sealed class AppCollection : ICollectionFixture<HeadlessApp>
{
    public const string Name = "app";
}
