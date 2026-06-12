using Avalonia.Controls;
using OrientDesk.Presentation.Controls;
using OrientDesk.Presentation.ViewModels.Pages;

namespace OrientDesk.Presentation.Views.Pages;

public partial class ParticipantsView : UserControl
{
    private ParticipantsViewModel? _vm;

    public ParticipantsView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        DetachedFromVisualTree += (_, _) => Unsubscribe();
    }

    private void OnDataContextChanged(object? sender, System.EventArgs e)
    {
        Unsubscribe();
        _vm = DataContext as ParticipantsViewModel;
        if (_vm is null)
            return;

        _vm.RosterColumnsChanged += OnRosterColumnsChanged;
        _vm.FocusGridRequested += OnFocusGridRequested;
    }

    private void OnFocusGridRequested(object? sender, System.EventArgs e)
        => Avalonia.Threading.Dispatcher.UIThread.Post(() => DayTable.Focus());

    // A collapse/expand toggle (or day-set change) asks the roster table to rebuild its columns.
    private void OnRosterColumnsChanged(object? sender, System.EventArgs e) => RosterTable.Rebuild();

    // The day table raises this on a keyboard Delete (Ctrl+Delete ⇒ skip the prompt).
    private void OnDayDeleteRequested(object? sender, RosterDeleteEventArgs e)
    {
        if (_vm is null || e.Row is not ParticipantDayRowViewModel row)
            return;
        if (e.SkipConfirm)
            _ = _vm.DeleteParticipantNoConfirmAsync(row);
        else
            _ = _vm.DeleteParticipantCommand.ExecuteAsync(row);
    }

    // The roster table raises this on a keyboard Delete (Ctrl+Delete ⇒ skip the prompt).
    private void OnRosterDeleteRequested(object? sender, RosterDeleteEventArgs e)
    {
        if (_vm is null || e.Row is not ParticipantRosterRowViewModel row)
            return;
        if (e.SkipConfirm)
            _ = _vm.DeleteRosterParticipantNoConfirmAsync(row);
        else
            _ = _vm.DeleteRosterParticipantCommand.ExecuteAsync(row);
    }

    private void Unsubscribe()
    {
        if (_vm is not null)
        {
            _vm.RosterColumnsChanged -= OnRosterColumnsChanged;
            _vm.FocusGridRequested -= OnFocusGridRequested;
        }
        _vm = null;
    }
}
