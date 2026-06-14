using Avalonia.Controls;

namespace HorizonRadio.UI.Views;

/// <summary>The full search-results page. Bound to <see cref="ViewModels.SearchViewModel"/>;
/// purely declarative (row actions are command bindings), so no code-behind logic.</summary>
public partial class SearchView : UserControl
{
    public SearchView()
    {
        InitializeComponent();
    }
}
