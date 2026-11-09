using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using OrientPyx.BusinessLogic.Interfaces;
using OrientPyx.Presentation.ViewModels;
using OrientPyx.Presentation.ViewModels.Pages;
using OrientPyx.Tests.Support;
using SkiaSharp;

namespace OrientPyx.Tests.DocShots;

/// <summary>
/// Drives the headless app for the documentation: a fixed 1280×800 window, opening a demo competition on a day,
/// opening pages through the root view model's commands, and saving PNGs into <see cref="DocShotOutput.ShotsDir"/> —
/// the whole window or cropped to a control, with numbered frames around the controls the text talks about. Every
/// shot is listed with its description in <c>shots/manifest.json</c> (what the page authors pick from).
/// </summary>
internal sealed class DocShooter
{
    public const int Width = 1280;
    public const int Height = 800;

    private static readonly ConcurrentDictionary<string, string> Manifest = new(StringComparer.Ordinal);
    private static readonly SKColor MarkColor = new(0xE1, 0x1D, 0x48);

    private readonly HeadlessApp _app;

    public DocShooter(HeadlessApp app) => _app = app;

    public HeadlessApp App => _app;

    public MainWindowViewModel Vm => _app.Vm;

    public ShellViewModel Shell => (ShellViewModel)_app.Vm.CurrentView;

    public Window Window => _app.Window;

    private static IServiceProvider Core => TestEnvironment.Instance.Services;

    /// <summary>Builds the demos (once), hides the test competition from the picker and sizes the window.</summary>
    public async Task PrepareAsync()
    {
        await DemoCompetitions.EnsureAsync();
        await HideTestCompetitionAsync();
        await _app.OnUiAsync(() =>
        {
            Window.Width = Width;
            Window.Height = Height;
        });
        await _app.SettleAsync();
    }

    private static readonly Lazy<Task> Hidden = new(async () =>
    {
        var store = Core.GetRequiredService<IEventStore>();
        var folder = (await TestEnvironment.Instance.GetEventAsync(TestEnvironment.Instance.SyntheticId)).FolderPath;
        var info = await store.GetCompetitionInfoAsync(folder);
        if (info is { IsHidden: false })
        {
            info.IsHidden = true;
            await store.SaveCompetitionInfoAsync(folder, info);
        }
    });

    private static Task HideTestCompetitionAsync() => Hidden.Value;

    public async Task GoToPickerAsync()
    {
        await _app.SettleAsync();
        await DismissDialogAsync();
        if (_app.Vm.IsEventSelected)
        {
            await _app.OnUiAsync(() => _app.Vm.ChangeEventCommand.ExecuteAsync(null));
            await _app.SettleAsync();
        }
        else if (_app.Vm.CurrentView is CreateEventViewModel create)
        {
            await _app.OnUiAsync(() => create.CancelCommand.Execute(null));
            await _app.SettleAsync();
        }
        await _app.WaitUntilAsync(() => _app.Vm.CurrentView is EventSelectionViewModel, what: "picker shown");
        var selection = (EventSelectionViewModel)_app.Vm.CurrentView;
        var items = await selection.FetchEventsAsync();
        await _app.OnUiAsync(() => selection.Populate(items));
        await _app.SettleAsync();
    }

    /// <summary>
    /// Closes a dialog a failed scenario left open (through its Cancel/Close command), so the next scenario starts clean.
    /// </summary>
    public async Task DismissDialogAsync()
    {
        for (var attempt = 0; attempt < 3 && _app.Vm.Dialogs.Current is { } dialog; attempt++)
        {
            await _app.OnUiAsync(() =>
            {
                var command = dialog.GetType().GetProperties()
                    .Where(p => p.Name is "CancelCommand" or "CloseCommand")
                    .Select(p => p.GetValue(dialog) as ICommand)
                    .FirstOrDefault(c => c is not null);
                command?.Execute(null);
            });
            await SettleLongAsync();
        }
        if (_app.Vm.Dialogs.Current is { } left)
            throw new InvalidOperationException($"A dialog is still open: {left.GetType().Name}");
    }

    /// <summary>Opens a demo competition on the given day (the session's current day, shared by every page).</summary>
    public async Task OpenAsync(string identifier, int day = 1)
    {
        var session = Session;
        if (!(_app.Vm.IsEventSelected && session.CurrentEvent?.Identifier == identifier))
        {
            await GoToPickerAsync();
            var selection = (EventSelectionViewModel)_app.Vm.CurrentView;
            await _app.OnUiAsync(() => selection.ShowHidden = true);      // the scratch competition of the imports is hidden
            await _app.WaitUntilAsync(() => selection.Events.Any(e => e.Identifier == identifier), what: $"{identifier} listed");
            await _app.OnUiAsync(async () =>
            {
                selection.SelectedEvent = selection.Events.First(e => e.Identifier == identifier);
                await selection.OpenCommand.ExecuteAsync(null);
            });
            await _app.WaitUntilAsync(() => _app.Vm.IsEventSelected, what: $"{identifier} opened");
            await _app.OnUiAsync(() => selection.ShowHidden = false);
            await _app.SettleAsync();
        }
        await SetDayAsync(day);
    }

    public ISessionService Session =>
        (ISessionService)typeof(MainWindowViewModel).GetField("_session", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(_app.Vm)!;

    public async Task SetDayAsync(int number)
    {
        var session = Session;
        if (session.CurrentDay?.Number == number)
            return;
        var folder = session.CurrentEvent!.FolderPath;
        var day = (await Core.GetRequiredService<IEventStore>().GetDaysAsync(folder)).Single(d => d.Number == number);
        await _app.OnUiAsync(() => session.SetCurrentDayAsync(day));
        await _app.WaitUntilAsync(() => session.CurrentDay?.Number == number, what: $"day {number} current");
        await SettleLongAsync();
    }

    /// <summary>Runs one of the root view model's page commands and returns the page it shows.</summary>
    public async Task<T> PageAsync<T>(Func<MainWindowViewModel, ICommand> command) where T : PageViewModelBase
    {
        var cmd = command(_app.Vm);
        await _app.OnUiAsync(async () =>
        {
            switch (cmd)
            {
                case IAsyncRelayCommand async:
                    await async.ExecuteAsync(null);
                    break;
                default:
                    cmd.Execute(null);
                    break;
            }
        });
        await SettleLongAsync();
        return await _app.OnUiAsync(() => Assert.IsType<T>(Shell.SelectedPage));
    }

    /// <summary>Settles twice around a short pause: pages load their rows in the background after the busy overlay.</summary>
    public async Task SettleLongAsync(int pauseMs = 350)
    {
        await _app.SettleAsync();
        await Task.Delay(pauseMs);
        await _app.SettleAsync();
        await Task.Delay(100);
        await _app.SettleAsync();
    }

    public async Task ResizeAsync(int width, int height)
    {
        await _app.OnUiAsync(() =>
        {
            Window.Width = width;
            Window.Height = height;
        });
        await SettleLongAsync();
    }

    /// <summary>Opens a top-level menu by its header text.</summary>
    public async Task OpenMenuAsync(string header)
    {
        var item = (MenuItem)await FindTextAsync(header, c => c is MenuItem);
        await _app.OnUiAsync(() => item.IsSubMenuOpen = true);
        await SettleLongAsync();
    }

    public async Task CloseMenusAsync()
    {
        await _app.OnUiAsync(() =>
        {
            foreach (var item in Window.GetVisualDescendants().OfType<MenuItem>().Where(m => m.IsSubMenuOpen))
                item.IsSubMenuOpen = false;
            foreach (var menu in Window.GetVisualDescendants().OfType<Menu>())
                menu.Close();
        });
        await SettleLongAsync();
    }

    public async Task CloseDropDownsAsync()
    {
        await _app.OnUiAsync(() =>
        {
            foreach (var combo in Window.GetVisualDescendants().OfType<ComboBox>().Where(c => c.IsDropDownOpen))
                combo.IsDropDownOpen = false;
        });
        await SettleLongAsync();
    }

    /// <summary>Shows a button's flyout (the «Імпорт ▾» / «Дії ▾» menus).</summary>
    public async Task OpenFlyoutAsync(Button button)
    {
        await _app.OnUiAsync(() => (button.Flyout ?? throw new InvalidOperationException("The button has no flyout.")).ShowAt(button));
        await SettleLongAsync();
    }

    public async Task HideFlyoutAsync(Button button)
    {
        await _app.OnUiAsync(() =>
        {
            button.Flyout?.Hide();
            foreach (var popup in Window.GetVisualDescendants().OfType<Popup>().Where(p => p.IsOpen))
                popup.IsOpen = false;
        });
        await SettleLongAsync();
    }

    /// <summary>
    /// Drags a page's row splitter: the bottom row of the grid holding the first row <c>GridSplitter</c> (the course
    /// editor on «Групи») gets <paramref name="height"/> pixels, as if the user had pulled the splitter up.
    /// </summary>
    public async Task SetBottomPaneHeightAsync(double height)
    {
        await _app.OnUiAsync(() =>
        {
            var splitter = Window.GetVisualDescendants().OfType<GridSplitter>()
                .First(g => g.IsEffectivelyVisible && g.ResizeDirection == GridResizeDirection.Rows);
            var grid = (Grid)splitter.Parent!;
            grid.RowDefinitions[^1].Height = new GridLength(height);
        });
        await SettleLongAsync();
    }

    /// <summary>Scrolls the named <c>SheetTable</c> so that <paramref name="item"/> (a row view model) is in view.</summary>
    public async Task ScrollTableToAsync(string tableName, object item)
    {
        var table = await FindAsync<OrientPyx.Presentation.Controls.SheetTable>(tableName);
        await _app.OnUiAsync(() => table.GetVisualDescendants().OfType<ListBox>().First().ScrollIntoView(item));
        await SettleLongAsync();
    }

    /// <summary>The card (<c>Border.card</c>) a control sits in — the natural frame to crop a section to.</summary>
    public Task<Control> CardOfAsync(Control control) =>
        _app.OnUiAsync(() => (Control?)control.GetVisualAncestors().OfType<Border>().FirstOrDefault(b => b.Classes.Contains("card"))
                             ?? throw new InvalidOperationException("The control is not inside a card."));

    public Task OnUiAsync(Action action) => _app.OnUiAsync(action);

    public Task OnUiAsync(Func<Task> action) => _app.OnUiAsync(action);

    public Task<T> OnUiAsync<T>(Func<T> func) => _app.OnUiAsync(func);

    public Task WaitUntilAsync(Func<bool> condition, string what) => _app.WaitUntilAsync(condition, what: what);

    // ---- finding controls -------------------------------------------------------------------------------------------

    /// <summary>The first control of type <typeparamref name="T"/> (optionally named) inside the window or an open popup.</summary>
    public Task<T> FindAsync<T>(string? name = null, Func<T, bool>? where = null) where T : Control =>
        _app.OnUiAsync(() => AllControls().OfType<T>()
            .Where(c => c.IsEffectivelyVisible && (name is null || c.Name == name) && (where is null || where(c)))
            .FirstOrDefault() ?? throw new InvalidOperationException($"No visible {typeof(T).Name} {name}"));

    /// <summary>The innermost visible control showing exactly <paramref name="text"/> (button content, text block, menu header).</summary>
    public Task<Control> FindTextAsync(string text, Func<Control, bool>? where = null) =>
        _app.OnUiAsync(() =>
        {
            var hit = AllControls()
                .Where(c => c.IsEffectivelyVisible && (where is null || where(c)))
                .Where(c => c switch
                {
                    TextBlock t => t.Text == text,
                    HeaderedSelectingItemsControl h => h.Header as string == text,
                    ContentControl cc => cc.Content as string == text,
                    _ => false,
                })
                .FirstOrDefault() ?? throw new InvalidOperationException($"No visible control with the text «{text}»");
            // A text block inside a button: frame the button.
            return hit is TextBlock && hit.FindAncestorOfType<Button>() is { } button ? button : hit;
        });

    private IEnumerable<Control> AllControls()
    {
        foreach (var c in Window.GetVisualDescendants().OfType<Control>())
            yield return c;
        foreach (var root in OpenPopupRoots())
            foreach (var c in root.GetVisualDescendants().OfType<Control>())
                yield return c;
    }

    private IEnumerable<TopLevel> OpenPopupRoots() =>
        Window.GetLogicalDescendants().OfType<Popup>().Concat(Window.GetVisualDescendants().OfType<Popup>()).Distinct()
            .Where(p => p.IsOpen && p.Child is not null)
            .Select(p => TopLevel.GetTopLevel(p.Child))
            .OfType<TopLevel>()
            .Where(t => !ReferenceEquals(t, Window))
            .Distinct();

    // ---- capturing --------------------------------------------------------------------------------------------------

    /// <summary>
    /// Saves <c>shots/&lt;name&gt;.png</c>: the window (with any open popup drawn in place), cropped to
    /// <paramref name="crop"/> (plus <paramref name="pad"/>) when given, with a numbered frame around each of
    /// <paramref name="marks"/> (just a frame when there is one).
    /// </summary>
    public async Task<string> ShotAsync(string name, string description, Control? crop = null, int pad = 12, params Control[] marks)
    {
        await _app.SettleAsync();
        var (png, layers, cropRect, markRects) = await _app.OnUiAsync(() =>
        {
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            var scale = Window.RenderScaling;
            var window = Encode(Window);
            var popups = OpenPopupRoots().Select(root => (Encode(root), Offset(root))).ToList();
            return (window, popups, crop is null ? (SKRectI?)null : Rect(crop, scale), marks.Select(m => Rect(m, scale)).ToList());
        });

        Directory.CreateDirectory(DocShotOutput.ShotsDir);
        var path = Path.Combine(DocShotOutput.ShotsDir, $"{name}.png");
        using (var bitmap = SKBitmap.Decode(png))
        using (var surface = SKSurface.Create(new SKImageInfo(bitmap.Width, bitmap.Height)))
        {
            var canvas = surface.Canvas;
            canvas.DrawBitmap(bitmap, 0, 0);
            foreach (var (layer, offset) in layers)
            {
                using var popup = SKBitmap.Decode(layer);
                canvas.DrawBitmap(popup, offset.X, offset.Y);
            }
            DrawMarks(canvas, markRects);
            using var image = surface.Snapshot();
            var bounds = new SKRectI(0, 0, bitmap.Width, bitmap.Height);
            var area = cropRect is { } r
                ? SKRectI.Intersect(bounds, new SKRectI(r.Left - pad, r.Top - pad, r.Right + pad, r.Bottom + pad))
                : bounds;
            using var cropped = image.Subset(area);
            using var data = cropped.Encode(SKEncodedImageFormat.Png, 100);
            await File.WriteAllBytesAsync(path, data.ToArray());
        }

        Manifest[name] = description;
        await File.WriteAllTextAsync(Path.Combine(DocShotOutput.ShotsDir, "manifest.json"),
            JsonSerializer.Serialize(new SortedDictionary<string, string>(Manifest, StringComparer.Ordinal),
                new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
        return path;
    }

    private static byte[] Encode(TopLevel topLevel)
    {
        using var frame = topLevel.CaptureRenderedFrame() ?? throw new InvalidOperationException($"{topLevel.GetType().Name} produced no frame.");
        using var stream = new MemoryStream();
        frame.Save(stream);
        return stream.ToArray();
    }

    // A popup's position relative to the window, in pixels (both report their position through the platform).
    private SKPointI Offset(TopLevel popup)
    {
        var origin = Window.PointToScreen(new Point(0, 0));
        var at = popup.PointToScreen(new Point(0, 0));
        return new SKPointI(at.X - origin.X, at.Y - origin.Y);
    }

    private SKRectI Rect(Control control, double scale)
    {
        var root = (Visual?)TopLevel.GetTopLevel(control) ?? Window;
        var topLeft = control.TranslatePoint(new Point(0, 0), root) ?? throw new InvalidOperationException($"{control.GetType().Name} is not in the window.");
        var offset = root is TopLevel top && !ReferenceEquals(top, Window) ? Offset(top) : new SKPointI(0, 0);
        var size = control.Bounds.Size;
        return new SKRectI(
            (int)Math.Floor(topLeft.X * scale) + offset.X,
            (int)Math.Floor(topLeft.Y * scale) + offset.Y,
            (int)Math.Ceiling((topLeft.X + size.Width) * scale) + offset.X,
            (int)Math.Ceiling((topLeft.Y + size.Height) * scale) + offset.Y);
    }

    private static void DrawMarks(SKCanvas canvas, IReadOnlyList<SKRectI> marks)
    {
        using var frame = new SKPaint { Color = MarkColor, Style = SKPaintStyle.Stroke, StrokeWidth = 3, IsAntialias = true };
        using var fill = new SKPaint { Color = MarkColor, Style = SKPaintStyle.Fill, IsAntialias = true };
        using var font = new SKFont(SKTypeface.FromFamilyName(null, SKFontStyle.Bold), 15);
        using var text = new SKPaint { Color = SKColors.White, IsAntialias = true };
        for (var i = 0; i < marks.Count; i++)
        {
            var r = marks[i];
            canvas.DrawRoundRect(new SKRect(r.Left - 4, r.Top - 4, r.Right + 4, r.Bottom + 4), 6, 6, frame);
            if (marks.Count < 2)
                continue;
            var center = new SKPoint(r.Left - 4, r.Top - 4);
            canvas.DrawCircle(center, 12, fill);
            var label = (i + 1).ToString();
            var width = font.MeasureText(label);
            canvas.DrawText(label, center.X - width / 2, center.Y + 5.5f, font, text);
        }
    }
}
