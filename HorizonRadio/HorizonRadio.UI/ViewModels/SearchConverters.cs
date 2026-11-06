using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace HorizonRadio.UI.ViewModels;

/// <summary>Value converters for the search surfaces.</summary>
public static class SearchConverters
{
    /// <summary>A disabled filter chip dims; an enabled one is full opacity.</summary>
    public static readonly IValueConverter EnabledOpacity =
        new FuncValueConverter<bool, double>(enabled => enabled ? 1.0 : 0.45);
}
