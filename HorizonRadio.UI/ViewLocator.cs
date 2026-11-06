using System;
using System.Diagnostics.CodeAnalysis;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using HorizonRadio.UI.ViewModels;

namespace HorizonRadio.UI;

/// <summary>
/// Given a view model, returns the corresponding view if possible. By convention the view is the
/// type in the view model's own assembly whose full name is the view model's with "ViewModel"
/// replaced by "View" — so a plugin's pages resolve to the views the plugin ships, not ours.
/// </summary>
[RequiresUnreferencedCode(
    "Default implementation of ViewLocator involves reflection which may be trimmed away.",
    Url = "https://docs.avaloniaui.net/docs/concepts/view-locator")]
public class ViewLocator : IDataTemplate
{
    public Control? Build(object? param)
    {
        if (param is null)
            return null;

        var type = ResolveViewType(param.GetType());
        if (type != null)
        {
            return (Control)Activator.CreateInstance(type)!;
        }

        return new TextBlock { Text = "Not Found: " + ViewName(param.GetType()) };
    }

    // Our own view models always match (a missing view shows "Not Found" rather than the type
    // name); anything else — e.g. a plugin page's view model — matches only if it has a view.
    public bool Match(object? data) =>
        data is ViewModelBase || (data is not null && ResolveViewType(data.GetType()) is not null);

    // Only *ViewModel types are candidates, and the result must be a Control: without both checks
    // a type with no "ViewModel" in its name (e.g. string content) maps to itself.
    private static Type? ResolveViewType(Type viewModel)
    {
        if (viewModel.FullName?.EndsWith("ViewModel", StringComparison.Ordinal) != true)
            return null;
        var view = viewModel.Assembly.GetType(ViewName(viewModel));
        return view is not null && typeof(Control).IsAssignableFrom(view) ? view : null;
    }

    private static string ViewName(Type viewModel) =>
        viewModel.FullName!.Replace("ViewModel", "View", StringComparison.Ordinal);
}
