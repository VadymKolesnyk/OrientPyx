using System;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Reactive;
using OrientPyx.Localization;
using OrientPyx.Presentation.ViewModels.Pages;

namespace OrientPyx.Presentation.Views.Pages;

public partial class AboutView : UserControl
{
    private ILocalizationService? _localization;

    public AboutView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        // Rebuild once attached so the accent brush (a themed resource) resolves.
        AttachedToVisualTree += (_, _) => BuildTributeBody();
        // The About overlay is toggled with IsVisible and lives permanently in the tree, so a language
        // change made while it is hidden rebuilds the inlines against a collapsed TextBlock that never
        // re-measures. When the overlay opens the view gets a real size again; rebuild then so the
        // paragraph is laid out fresh against the current language.
        this.GetObservable(BoundsProperty).Subscribe(new AnonymousObserver<Rect>(bounds =>
        {
            if (bounds.Width > 0 && bounds.Height > 0)
                BuildTributeBody();
        }));
    }

    // The tribute body's inlines (plain text + the author-name link) are built here rather than in
    // XAML: a TextBlock does not re-lay-out its Inlines when their bindings change, so a language
    // switch left the paragraph collapsed. Rebuilding on every language change keeps it correct.
    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_localization is not null)
            _localization.PropertyChanged -= OnLanguageChanged;

        _localization = (DataContext as AboutViewModel)?.Localization;
        if (_localization is not null)
            _localization.PropertyChanged += OnLanguageChanged;

        BuildTributeBody();
    }

    private void OnLanguageChanged(object? sender, PropertyChangedEventArgs e) => BuildTributeBody();

    private void BuildTributeBody()
    {
        if (_localization is null)
            return;

        // Assign a fresh InlineCollection (rather than mutating the existing one) so the TextBlock
        // re-runs its text layout — mutating in place leaves the paragraph collapsed after a
        // language switch.
        var inlines = new InlineCollection
        {
            new Run(_localization["About.Tribute.BodyBefore"]),
            new Run(_localization["About.Tribute.AuthorLink"])
            {
                Foreground = this.TryFindResource("AccentBrush", out var accent) ? accent as IBrush : null,
                TextDecorations = TextDecorations.Underline,
            },
            new Run(_localization["About.Tribute.BodyAfter"]),
        };

        TributeBody.Inlines = inlines;
        TributeBody.InvalidateMeasure();
    }

    // Char range of the author-name run = [before.Length, before.Length + link.Length); returns true
    // when the given text position lands on the name.
    private bool IsOnAuthorLink(int textPosition)
    {
        if (_localization is null)
            return false;

        var before = _localization["About.Tribute.BodyBefore"].Length;
        var link = _localization["About.Tribute.AuthorLink"].Length;
        return textPosition >= before && textPosition < before + link;
    }

    // Show a hand cursor while hovering the author's name so it reads as a link.
    private void OnBodyPointerMoved(object? sender, PointerEventArgs e)
    {
        if (sender is not TextBlock body)
            return;

        var hit = body.TextLayout.HitTestPoint(e.GetPosition(body));
        var onLink = hit.IsInside && IsOnAuthorLink(hit.TextPosition);
        body.Cursor = onLink ? new Cursor(StandardCursorType.Hand) : Cursor.Default;
    }

    private void OnBodyPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not TextBlock body || DataContext is not AboutViewModel vm)
            return;

        var hit = body.TextLayout.HitTestPoint(e.GetPosition(body));
        if (hit.IsInside && IsOnAuthorLink(hit.TextPosition))
            LaunchUrl(vm.AuthorMemoryUrl);
    }

    // Opens the clicked link (GitHub / Orientir / memory) in the default browser. Same launch
    // pattern as OnlineResultsView: the URL travels on the button's Tag.
    private void OnLinkClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { Tag: string url })
            LaunchUrl(url);
    }

    private void LaunchUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return;

        var launcher = TopLevel.GetTopLevel(this)?.Launcher;
        if (launcher is null || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return;

        _ = launcher.LaunchUriAsync(uri);
    }
}
