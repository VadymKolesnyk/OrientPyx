using Avalonia;
using Avalonia.Controls;
using OrientPyx.Presentation.Controls;
using OrientPyx.Presentation.ViewModels.Pages;

namespace OrientPyx.Presentation.Views.Pages;

/// <summary>
/// The one way a page tells its view model that a table cell is being edited: every view that hosts a
/// <see cref="SheetTable"/> calls <see cref="Bind"/> once from its constructor with all of its tables, and the
/// page's <see cref="PageViewModelBase.IsEditingCell"/> then follows <see cref="SheetTable.IsEditing"/> of any of
/// them. A live-data reload (another station wrote to the competition) is deferred while the flag is up, so the
/// operator's typing is never replaced under their fingers.
/// </summary>
internal static class SheetEditing
{
    public static void Bind(UserControl view, params SheetTable[] tables)
    {
        PageViewModelBase? bound = null;

        void Apply()
        {
            var vm = view.DataContext as PageViewModelBase;
            // The page's view model is a singleton, but be safe: a view model this view no longer shows must not
            // stay stuck with the flag raised (its reloads would wait forever).
            if (!ReferenceEquals(bound, vm) && bound is not null)
                bound.IsEditingCell = false;
            bound = vm;
            if (vm is not null)
                vm.IsEditingCell = tables.Any(t => t.IsEditing);
        }

        foreach (var table in tables)
            table.PropertyChanged += (_, e) =>
            {
                if (e.Property == SheetTable.IsEditingProperty)
                    Apply();
            };
        view.DataContextChanged += (_, _) => Apply();
    }
}
