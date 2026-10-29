using Avalonia;
using Avalonia.Controls;

namespace WhisperLocal.Ui.Pages;

/// <summary>
/// One page of the main window: a title and a scrolling column of cards. While the page is on screen the window
/// calls <see cref="Refresh"/> every second or two, so a refresh only rebuilds what changed.
/// </summary>
internal abstract class Page : UserControl
{
    private readonly StackPanel _cards = new() { Margin = new Thickness(16, 14, 16, 16), Spacing = 10 };

    protected Page(string title)
    {
        Title = title;
        _cards.Children.Add(Widgets.PageTitle(title));
        Content = new ScrollViewer { Content = _cards };
    }

    public string Title { get; }

    protected void Add(Control card) => _cards.Children.Add(card);

    public abstract void Refresh();

    /// <summary>The page was selected or the window shown.</summary>
    public virtual void OnShown() => Refresh();

    /// <summary>Another page was selected or the window hidden.</summary>
    public virtual void OnHidden()
    {
    }
}
