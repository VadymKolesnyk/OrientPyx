using System;
using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using OrientPyx.Presentation.Controls;
using OrientPyx.Presentation.ViewModels.Pages;

namespace OrientPyx.Presentation.Views.Pages;

public partial class RanksView : UserControl
{
    private RanksViewModel? _vm;

    public RanksView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        DetachedFromVisualTree += (_, _) => Unsubscribe();

        // Capture the Ctrl modifier before the delete button consumes the press, so Ctrl+Click on
        // Delete can skip the confirmation prompt (see ChipsView for the same approach).
        AddHandler(PointerPressedEvent, OnTunnelPointerPressed, RoutingStrategies.Tunnel);
    }

    // The table raises this on a keyboard Delete (Ctrl+Delete ⇒ skip the prompt).
    private void OnDeleteRequested(object? sender, SheetDeleteEventArgs e)
    {
        if (_vm is null || e.Row is not RankRowViewModel row)
            return;
        DeleteRow(row, e.SkipConfirm);
    }

    private void OnQualDeleteRequested(object? sender, SheetDeleteEventArgs e)
    {
        if (_vm is null || e.Row is not RankQualRowViewModel row)
            return;
        DeleteQualRow(row, e.SkipConfirm);
    }

    private bool _deleteCtrlDown;

    private void OnTunnelPointerPressed(object? sender, PointerPressedEventArgs e)
        => _deleteCtrlDown = e.KeyModifiers.HasFlag(KeyModifiers.Control);

    private void OnDeleteButton(object row)
    {
        if (_vm is null || row is not RankRowViewModel rank)
            return;
        var skipConfirm = _deleteCtrlDown;
        _deleteCtrlDown = false;
        DeleteRow(rank, skipConfirm);
    }

    private void OnQualDeleteButton(object row)
    {
        if (_vm is null || row is not RankQualRowViewModel qual)
            return;
        var skipConfirm = _deleteCtrlDown;
        _deleteCtrlDown = false;
        DeleteQualRow(qual, skipConfirm);
    }

    private void DeleteRow(RankRowViewModel row, bool skipConfirm)
    {
        if (skipConfirm)
            _ = _vm!.DeleteRankNoConfirmAsync(row);
        else
            _ = _vm!.DeleteRankCommand.ExecuteAsync(row);
    }

    private void DeleteQualRow(RankQualRowViewModel row, bool skipConfirm)
    {
        if (skipConfirm)
            _ = _vm!.DeleteQualRowNoConfirmAsync(row);
        else
            _ = _vm!.DeleteQualRowCommand.ExecuteAsync(row);
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        Unsubscribe();
        _vm = DataContext as RanksViewModel;
        if (_vm is null)
            return;

        _vm.Localization.PropertyChanged += OnLocalizationChanged;
        _vm.FocusGridRequested += OnFocusGridRequested;
        BuildBands();
    }

    private void OnLocalizationChanged(object? sender, PropertyChangedEventArgs e) => BuildBands();

    // After the delete-confirmation modal closes, return focus to the grid (see ChipsView).
    private void OnFocusGridRequested(object? sender, EventArgs e)
        => Avalonia.Threading.Dispatcher.UIThread.Post(() => Sheet.Focus());

    // Builds the table's columns: an editable rank name and its points (decimal). Headers are baked
    // into the band model, so a language switch is handled by rebuilding.
    private void BuildBands()
    {
        if (_vm is null)
            return;

        Sheet.Bands = new SheetColumnBuilder(_vm.Localization)
            .Text("Ranks.Col.Name", nameof(RankRowViewModel.Name),
                  editPath: nameof(RankRowViewModel.Name), minWidth: 200)
            .Text("Ranks.Col.Points", nameof(RankRowViewModel.PointsText),
                  editPath: nameof(RankRowViewModel.PointsText), minWidth: 120,
                  mask: SheetColumnBuilder.NumericMask.Decimal)
            .DeleteAction(OnDeleteButton, "Ranks.Delete")
            .Bands;

        BuildQualBands();
    }

    // The qualification table: a course-rank threshold and the ten percentage cells (five time, five
    // points), all integer-masked. Headers are baked into the band model, so a language switch rebuilds.
    private void BuildQualBands()
    {
        if (_vm is null)
            return;

        SheetColumnBuilder Cell(SheetColumnBuilder b, string key, string path) =>
            b.Text(key, path, editPath: path, minWidth: 80, mask: SheetColumnBuilder.NumericMask.Integer);

        var builder = new SheetColumnBuilder(_vm.Localization)
            .Text("Ranks.Qual.Col.Rank", nameof(RankQualRowViewModel.RankText),
                  editPath: nameof(RankQualRowViewModel.RankText), minWidth: 80,
                  mask: SheetColumnBuilder.NumericMask.Integer);
        Cell(builder, "Ranks.Qual.Col.TimeKms", nameof(RankQualRowViewModel.TimeKmsText));
        Cell(builder, "Ranks.Qual.Col.TimeFirst", nameof(RankQualRowViewModel.TimeFirstText));
        Cell(builder, "Ranks.Qual.Col.TimeSecond", nameof(RankQualRowViewModel.TimeSecondText));
        Cell(builder, "Ranks.Qual.Col.TimeThird", nameof(RankQualRowViewModel.TimeThirdText));
        Cell(builder, "Ranks.Qual.Col.TimeThirdJunior", nameof(RankQualRowViewModel.TimeThirdJuniorText));
        Cell(builder, "Ranks.Qual.Col.PointsKms", nameof(RankQualRowViewModel.PointsKmsText));
        Cell(builder, "Ranks.Qual.Col.PointsFirst", nameof(RankQualRowViewModel.PointsFirstText));
        Cell(builder, "Ranks.Qual.Col.PointsSecond", nameof(RankQualRowViewModel.PointsSecondText));
        Cell(builder, "Ranks.Qual.Col.PointsThird", nameof(RankQualRowViewModel.PointsThirdText));
        Cell(builder, "Ranks.Qual.Col.PointsThirdJunior", nameof(RankQualRowViewModel.PointsThirdJuniorText));
        builder.DeleteAction(OnQualDeleteButton, "Ranks.Qual.Delete");

        QualSheet.Bands = builder.Bands;
    }

    private void Unsubscribe()
    {
        if (_vm is not null)
        {
            _vm.Localization.PropertyChanged -= OnLocalizationChanged;
            _vm.FocusGridRequested -= OnFocusGridRequested;
        }
        _vm = null;
    }
}
