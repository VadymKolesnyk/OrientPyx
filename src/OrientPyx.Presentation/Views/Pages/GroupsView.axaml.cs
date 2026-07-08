using System.ComponentModel;
using System.IO;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using OrientPyx.Presentation.Controls;
using OrientPyx.Presentation.ViewModels.Pages;

namespace OrientPyx.Presentation.Views.Pages;

public partial class GroupsView : UserControl
{
    private GroupsViewModel? _vm;

    public GroupsView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        DetachedFromVisualTree += (_, _) => Unsubscribe();

        // Capture the Ctrl modifier before the delete button consumes the press (it marks
        // PointerPressed handled), so Ctrl+Click on Delete can skip the confirmation prompt.
        AddHandler(PointerPressedEvent, OnTunnelPointerPressed, RoutingStrategies.Tunnel);
    }

    private void OnDataContextChanged(object? sender, System.EventArgs e)
    {
        Unsubscribe();
        _vm = DataContext as GroupsViewModel;
        if (_vm is null)
            return;

        // Column headers and which columns appear are baked into the band model, so a language
        // switch or a change to the day's disciplines is handled by rebuilding the bands.
        _vm.Localization.PropertyChanged += OnLocalizationChanged;
        _vm.ColumnsChanged += OnColumnsChanged;
        _vm.FocusGridRequested += OnFocusGridRequested;
        BuildBands();
    }

    private void OnLocalizationChanged(object? sender, PropertyChangedEventArgs e) => BuildBands();

    private void OnColumnsChanged(object? sender, System.EventArgs e) => BuildBands();

    // After the delete-confirmation modal closes, return keyboard focus to the table (on its new
    // selected row). Posted so it runs once the overlay has been torn down and the table is live again.
    private void OnFocusGridRequested(object? sender, System.EventArgs e)
        => Avalonia.Threading.Dispatcher.UIThread.Post(() => Sheet.Focus());

    // Builds the table's columns. The required-count and penalty columns appear only when some group
    // on the day uses that scoring format; the rest are always shown.
    private void BuildBands()
    {
        if (_vm is null)
            return;

        var loc = _vm.Localization;
        var builder = new SheetColumnBuilder(loc)
            .Text("Groups.Col.Name", nameof(GroupDayRowViewModel.Name),
                  editPath: nameof(GroupDayRowViewModel.Name), minWidth: 140)
            // Course order (set course) / list of allowed control points (score formats). Dimmed and
            // disabled for disciplines that don't use it. A scatter («розсіювання») group shows «N варіантів
            // дистанції» here (read-only) and edits its several orders in the bottom variants table instead.
            .Text("Groups.Col.CourseOrder", nameof(GroupDayRowViewModel.CourseOrderDisplay),
                  editPath: nameof(GroupDayRowViewModel.CourseOrder), minWidth: 160,
                  placeholder: "S1 31 32 33 F",
                  enabledPath: nameof(GroupDayRowViewModel.CanEditCourseOrderInline),
                  opacityPath: nameof(GroupDayRowViewModel.UsesCourseOrder))
            // Control count: auto-computed from the course/control list, read-only.
            .Text("Groups.Col.ControlCount", nameof(GroupDayRowViewModel.ControlCountText), minWidth: 90)
            // Participant count: how many participants are in this group on the day, read-only.
            .Text("Groups.Col.ParticipantCount", nameof(GroupDayRowViewModel.ParticipantCountText), minWidth: 90);

        // Required minimum control count (score by count).
        if (_vm.ShowRequiredCountColumn)
            builder.Text("Groups.Col.RequiredCount", nameof(GroupDayRowViewModel.RequiredCountText),
                         editPath: nameof(GroupDayRowViewModel.RequiredCountText), minWidth: 90,
                         mask: SheetColumnBuilder.NumericMask.Integer,
                         enabledPath: nameof(GroupDayRowViewModel.UsesRequiredCount),
                         opacityPath: nameof(GroupDayRowViewModel.UsesRequiredCount));

        // Penalty per minute late (score by time).
        if (_vm.ShowPenaltyColumn)
            builder.Text("Groups.Col.Penalty", nameof(GroupDayRowViewModel.PenaltyText),
                         editPath: nameof(GroupDayRowViewModel.PenaltyText), minWidth: 100,
                         mask: SheetColumnBuilder.NumericMask.Decimal,
                         enabledPath: nameof(GroupDayRowViewModel.UsesPenalty),
                         opacityPath: nameof(GroupDayRowViewModel.UsesPenalty));

        builder
            // Time limit (контрольний час) — applies to every discipline.
            .Text("Groups.Col.TimeLimit", nameof(GroupDayRowViewModel.TimeLimitText),
                  editPath: nameof(GroupDayRowViewModel.TimeLimitText), minWidth: 100,
                  placeholder: "гг:хх:сс", mask: SheetColumnBuilder.NumericMask.Time)
            .Text("Groups.Col.Distance", nameof(GroupDayRowViewModel.DistanceText),
                  editPath: nameof(GroupDayRowViewModel.DistanceText), minWidth: 110,
                  mask: SheetColumnBuilder.NumericMask.Decimal)
            .Combo("Groups.Col.Discipline",
                   nameof(GroupDayRowViewModel.DisciplineOptions),
                   nameof(GroupDayRowViewModel.SelectedDiscipline),
                   nameof(DisciplineOverrideOption.Label),
                   minWidth: 170,
                   sortPath: $"{nameof(GroupDayRowViewModel.SelectedDiscipline)}.Value")
            // Per-group course-setter (начальник дистанції) override; a blank cell shows the competition
            // default (greyed) as its placeholder, in both the resting label and the editor's watermark.
            .Text("Groups.Col.CourseSetter", nameof(GroupDayRowViewModel.CourseSetter),
                  editPath: nameof(GroupDayRowViewModel.CourseSetter), minWidth: 150,
                  placeholderPath: nameof(GroupDayRowViewModel.CourseSetterPlaceholder))
            .Text("Groups.Col.CourseSetterCategory", nameof(GroupDayRowViewModel.CourseSetterCategory),
                  editPath: nameof(GroupDayRowViewModel.CourseSetterCategory), minWidth: 90,
                  placeholderPath: nameof(GroupDayRowViewModel.EffectiveCourseSetterCategoryPlaceholder))
            // Per-group points-rule override; the "(default: …)" sentinel inherits the competition default.
            .Combo("Groups.Col.PointsRule",
                   nameof(GroupDayRowViewModel.PointsRuleOptions),
                   nameof(GroupDayRowViewModel.SelectedPointsRule),
                   nameof(PointsRuleOption.Label),
                   minWidth: 170)
            // Which sports-rank level this group awards (Додаток 89), and how many «Майстер спорту» to award.
            .Combo("Groups.Col.RankLevel",
                   nameof(GroupDayRowViewModel.RankLevelOptions),
                   nameof(GroupDayRowViewModel.SelectedRankLevel),
                   nameof(RankLevelOption.Label),
                   minWidth: 150,
                   sortPath: $"{nameof(GroupDayRowViewModel.SelectedRankLevel)}.Value")
            .Text("Groups.Col.MasterCount", nameof(GroupDayRowViewModel.MasterCountText),
                  editPath: nameof(GroupDayRowViewModel.MasterCountText), minWidth: 90,
                  mask: SheetColumnBuilder.NumericMask.Integer)
            // Age window (group-level, editable here): earliest ("не старше") and latest ("не молодше")
            // allowed birth year, both inclusive. A blank cell means that bound is unset.
            .Text("Groups.Col.MinBirthYear", nameof(GroupDayRowViewModel.MinBirthYearText),
                  editPath: nameof(GroupDayRowViewModel.MinBirthYearText), minWidth: 110,
                  mask: SheetColumnBuilder.NumericMask.Integer)
            .Text("Groups.Col.MaxBirthYear", nameof(GroupDayRowViewModel.MaxBirthYearText),
                  editPath: nameof(GroupDayRowViewModel.MaxBirthYearText), minWidth: 110,
                  mask: SheetColumnBuilder.NumericMask.Integer)
            .DeleteAction(OnDeleteButton, "Groups.Delete");

        Sheet.Bands = builder.Bands;

        BuildScatterBands();
    }

    // Builds the bottom scatter («розсіювання») variants table's columns: an editable Код and Дистанція,
    // plus the trailing delete column. Rebuilt alongside the main grid on a language change.
    private void BuildScatterBands()
    {
        if (_vm is null)
            return;

        ScatterSheet.Bands = new SheetColumnBuilder(_vm.Localization)
            .Text("Groups.Scatter.Col.Code", nameof(ScatterVariantRowViewModel.Code),
                  editPath: nameof(ScatterVariantRowViewModel.Code), width: 120,
                  placeholder: "A")
            .Text("Groups.Scatter.Col.Order", nameof(ScatterVariantRowViewModel.CourseOrder),
                  editPath: nameof(ScatterVariantRowViewModel.CourseOrder), minWidth: 240,
                  placeholder: "S1 31 32 33 F")
            .DeleteAction(OnScatterDeleteButton, "Groups.Scatter.Remove")
            .Bands;
    }

    // The table raises this on a keyboard Delete (Ctrl+Delete ⇒ skip the prompt).
    private void OnDeleteRequested(object? sender, SheetDeleteEventArgs e)
    {
        if (_vm is null || e.Row is not GroupDayRowViewModel row)
            return;
        DeleteRow(row, e.SkipConfirm);
    }

    // Scatter variants table: keyboard Delete removes the selected variant row (no confirm — the whole
    // set autosaves, so a mistaken delete is trivially undone by re-adding).
    private void OnScatterDeleteRequested(object? sender, SheetDeleteEventArgs e)
    {
        if (_vm is null || e.Row is not ScatterVariantRowViewModel row)
            return;
        _vm.RemoveScatterVariantCommand.Execute(row);
    }

    // Scatter variants table: the per-row delete button.
    private void OnScatterDeleteButton(object row)
    {
        if (_vm is null || row is not ScatterVariantRowViewModel variant)
            return;
        _vm.RemoveScatterVariantCommand.Execute(variant);
    }

    // The per-row delete button. Button.Click doesn't carry key modifiers, and the button marks its
    // own PointerPressed handled — so we capture the Ctrl state in the tunnel phase. A plain click
    // confirms first; Ctrl+Click deletes immediately.
    private bool _deleteCtrlDown;

    private void OnTunnelPointerPressed(object? sender, PointerPressedEventArgs e)
        => _deleteCtrlDown = e.KeyModifiers.HasFlag(KeyModifiers.Control);

    private void OnDeleteButton(object row)
    {
        if (_vm is null || row is not GroupDayRowViewModel group)
            return;
        var skipConfirm = _deleteCtrlDown;
        _deleteCtrlDown = false;
        DeleteRow(group, skipConfirm);
    }

    private void DeleteRow(GroupDayRowViewModel row, bool skipConfirm)
    {
        if (skipConfirm)
            _ = _vm!.DeleteGroupNoConfirmAsync(row);
        else
            _ = _vm!.DeleteGroupCommand.ExecuteAsync(row);
    }

    // File picking is a view concern (it needs the window's StorageProvider), so it lives here rather
    // than in the view model. We read the chosen file's text and hand it to the VM, which owns
    // parsing, the options modal, and the import itself. Mirrors ControlPointsView.OnImportClick.
    private async void OnImportClick(object? sender, RoutedEventArgs e)
    {
        if (_vm is null)
            return;

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null)
            return;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = _vm.Localization.Get("Import.PickerTitle"),
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

        // Read the raw bytes once so we can both parse the text and archive the exact original file
        // into the day's folder. Decode through a StreamReader so any byte-order mark is detected and
        // stripped (matching the previous behaviour); the kept bytes stay the untouched original.
        string xml;
        byte[]? content = null;
        var fileName = files[0].Name;
        try
        {
            await using var stream = await files[0].OpenReadAsync();
            using var memory = new MemoryStream();
            await stream.CopyToAsync(memory);
            content = memory.ToArray();

            using var reader = new StreamReader(new MemoryStream(content), detectEncodingFromByteOrderMarks: true);
            xml = await reader.ReadToEndAsync();
        }
        catch
        {
            // Couldn't read the file (permissions, removed, etc.) — let the VM report via the modal.
            xml = string.Empty;
        }

        await _vm.ImportFromXmlAsync(xml, fileName, content);
    }

    private void Unsubscribe()
    {
        if (_vm is not null)
        {
            _vm.Localization.PropertyChanged -= OnLocalizationChanged;
            _vm.ColumnsChanged -= OnColumnsChanged;
            _vm.FocusGridRequested -= OnFocusGridRequested;
        }
        _vm = null;
    }
}
