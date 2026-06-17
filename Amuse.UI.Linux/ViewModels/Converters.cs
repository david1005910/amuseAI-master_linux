using Avalonia.Data.Converters;
using System;
using System.Globalization;

namespace Amuse.UI.Linux.ViewModels
{
    public class NotNullConverter : IValueConverter
    {
        public static readonly NotNullConverter Instance = new();
        public object Convert(object value, Type t, object p, CultureInfo c) => value != null;
        public object ConvertBack(object value, Type t, object p, CultureInfo c) => throw new NotImplementedException();
    }

    public class NullConverter : IValueConverter
    {
        public static readonly NullConverter Instance = new();
        public object Convert(object value, Type t, object p, CultureInfo c) => value == null;
        public object ConvertBack(object value, Type t, object p, CultureInfo c) => throw new NotImplementedException();
    }

    public class StringNotEmptyConverter : IValueConverter
    {
        public static readonly StringNotEmptyConverter Instance = new();
        public object Convert(object value, Type t, object p, CultureInfo c) =>
            value is string s && !string.IsNullOrEmpty(s);
        public object ConvertBack(object value, Type t, object p, CultureInfo c) => throw new NotImplementedException();
    }
}
