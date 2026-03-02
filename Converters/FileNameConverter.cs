using System.Globalization;
using System.IO;
using System.Windows.Data;

namespace Revenant_Theme_Studio.Converters
{
    public class FileNameConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => Path.GetFileNameWithoutExtension(value?.ToString() ?? "");

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }
}
