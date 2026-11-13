using System.Collections;
using Avalonia;
using Avalonia.Controls;
using HorizonRadio.Sdk.Tools;
using HorizonRadio.UI.ViewModels;

namespace HorizonRadio.UI.Views;

/// <summary>
/// Renders a settings form from a collection of <see cref="ConfigFieldViewModel"/> (one widget per
/// field type): a source's settings on the Sources tab, a metadata provider's on the Metadata tab.
/// </summary>
public partial class ConfigFormView : UserControl
{
    /// <summary>The config-field view models to render.</summary>
    public static readonly StyledProperty<IEnumerable?> FieldsProperty =
        AvaloniaProperty.Register<ConfigFormView, IEnumerable?>(nameof(Fields));

    public IEnumerable? Fields
    {
        get => GetValue(FieldsProperty);
        set => SetValue(FieldsProperty, value);
    }

    public ConfigFormView()
    {
        InitializeComponent();
    }

    private void ToolPick_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is not ComboBox combo || combo.Tag is not ToolFieldViewModel vm) return;
        if (combo.SelectedItem is InstalledTool tool) vm.PickInstalled(tool);
    }
}
