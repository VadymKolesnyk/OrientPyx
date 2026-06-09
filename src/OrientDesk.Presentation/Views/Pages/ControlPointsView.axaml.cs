using System.ComponentModel;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using OrientDesk.Presentation.ViewModels.Pages;

namespace OrientDesk.Presentation.Views.Pages;

public partial class ControlPointsView : UserControl
{
    private ControlPointsViewModel? _vm;

    public ControlPointsView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        DetachedFromVisualTree += (_, _) => Unsubscribe();
    }

    // DataGrid column headers live outside the visual tree, so they can't bind to
    // Localization[...] like cells do. We resolve them here and re-apply on language change.
    private void OnDataContextChanged(object? sender, System.EventArgs e)
    {
        Unsubscribe();
        _vm = DataContext as ControlPointsViewModel;
        if (_vm is null)
            return;

        _vm.Localization.PropertyChanged += OnLocalizationChanged;
        ApplyHeaders();
    }

    private void OnLocalizationChanged(object? sender, PropertyChangedEventArgs e) => ApplyHeaders();

    // File picking is a view concern (it needs the window's StorageProvider), so it lives here
    // rather than in the view model. We read the chosen file's text and hand it to the VM, which
    // owns parsing, the options modal, and the import itself.
    private async void OnImportClick(object? sender, RoutedEventArgs e)
    {
        if (_vm is null)
            return;

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null)
            return;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = _vm.Localization.Get("ControlPoints.Import.PickerTitle"),
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("IOF XML")
                {
                    Patterns = ["*.xml"],
                    MimeTypes = ["application/xml", "text/xml"]
                }
            ]
        });

        if (files.Count == 0)
            return;

        string xml;
        try
        {
            await using var stream = await files[0].OpenReadAsync();
            using var reader = new StreamReader(stream);
            xml = await reader.ReadToEndAsync();
        }
        catch
        {
            // Couldn't read the file (permissions, removed, etc.) — let the VM report via the modal.
            xml = string.Empty;
        }

        await _vm.ImportFromXmlAsync(xml);
    }

    private void ApplyHeaders()
    {
        if (_vm is null)
            return;

        var loc = _vm.Localization;
        var columns = Sheet.Columns;
        columns[0].Header = loc.Get("ControlPoints.Col.Code");
        columns[1].Header = loc.Get("ControlPoints.Col.Type");
        columns[2].Header = loc.Get("ControlPoints.Col.Lat");
        columns[3].Header = loc.Get("ControlPoints.Col.Lon");
        columns[4].Header = loc.Get("ControlPoints.Col.Actions");
    }

    private void Unsubscribe()
    {
        if (_vm is not null)
            _vm.Localization.PropertyChanged -= OnLocalizationChanged;
        _vm = null;
    }
}
