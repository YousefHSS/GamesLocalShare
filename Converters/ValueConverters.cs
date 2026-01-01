using System.Globalization;
using System.Windows;
using System.Windows.Data;
using GamesLocalShare.Models;
using System.Windows.Media.Imaging;
using System.Windows.Media;

namespace GamesLocalShare.Converters;

/// <summary>
/// Inverts a boolean value
/// </summary>
public class InvertBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is bool boolValue)
            return !boolValue;
        return value;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is bool boolValue)
            return !boolValue;
        return value;
    }
}

/// <summary>
/// Converts inverted boolean to Visibility
/// </summary>
public class InvertedBoolToVisConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is bool boolValue)
            return boolValue ? Visibility.Collapsed : Visibility.Visible;
        return Visibility.Visible;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
/// Converts boolean to Online/Offline text
/// </summary>
public class BoolToOnlineConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is bool boolValue)
            return boolValue ? "Online" : "Offline";
        return "Offline";
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
/// Converts zero count to Visibility (shows when zero)
/// </summary>
public class ZeroToVisConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is int intValue)
            return intValue == 0 ? Visibility.Visible : Visibility.Collapsed;
        return Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
/// Converts null to boolean (true if not null)
/// </summary>
public class NullToBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value != null;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
/// Converts boolean to speed mode text (WiFi/High-Speed)
/// </summary>
public class BoolToSpeedModeConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is bool isHighSpeed)
            return isHighSpeed ? "🖇️ Wired" : "\U0001f6dc WiFi";
        return "?? WiFi";
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
/// Converts string resource key to resource value
/// </summary>
public class StringToResourceConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is string key && Application.Current.MainWindow?.Resources.Contains(key) == true)
        {
            return Application.Current.MainWindow.Resources[key];
        }
        return null;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
/// Converts GamePlatform to icon resource
/// </summary>
public class GamePlatformToIconConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        string key = "IconGames";
        if (value is GamePlatform platform)
        {
            key = platform switch
            {
                GamePlatform.Steam => "IconSteam",
                GamePlatform.EpicGames => "IconEpic",
                GamePlatform.Xbox => "IconXbox",
                _ => "IconGames"
            };
        }

        // Try to get resource from main window resources
        var main = Application.Current?.MainWindow;
        if (main != null && main.Resources.Contains(key))
        {
            var res = main.Resources[key];
            // If it's already an ImageSource (BitmapImage / DrawingImage), return it
            if (res is ImageSource imgSrc)
                return imgSrc;
            if (res is DrawingImage dImg && dImg.Drawing != null)
                return dImg;
        }

        // Fallback: try to load a PNG from the Icons folder next to the executable
        try
        {
            var basePath = AppDomain.CurrentDomain.BaseDirectory;
            var name = key.StartsWith("Icon") ? key.Substring(4).ToLower() : key.ToLower(); // e.g., IconSteam -> steam
            var pngPath = System.IO.Path.Combine(basePath, "Icons", $"{name}.png");
            if (System.IO.File.Exists(pngPath))
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.UriSource = new Uri(pngPath, UriKind.Absolute);
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.EndInit();
                // Also store in resources for future calls
                main?.Resources.Remove(key);
                if (main != null)
                    main.Resources[key] = bmp;
                return bmp;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"GamePlatformToIconConverter fallback load error: {ex.Message}");
        }

        return null;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
