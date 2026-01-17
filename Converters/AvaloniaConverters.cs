using System;
using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;
using GamesLocalShare.Models;

namespace GamesLocalShare.Converters;

/// <summary>
/// Inverts a boolean value
/// </summary>
public class InvertBoolConverter : IValueConverter
{
    public static readonly InvertBoolConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool boolValue)
            return !boolValue;
        return value;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool boolValue)
            return !boolValue;
        return value;
    }
}

/// <summary>
/// Converts boolean to visibility (true = visible, false = collapsed)
/// </summary>
public class BoolToVisibilityConverter : IValueConverter
{
    public static readonly BoolToVisibilityConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool boolValue)
            return boolValue;
        return false;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool boolValue)
            return boolValue;
        return false;
    }
}

/// <summary>
/// Converts inverted boolean to visibility
/// </summary>
public class InvertedBoolToVisibilityConverter : IValueConverter
{
    public static readonly InvertedBoolToVisibilityConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool boolValue)
            return !boolValue;
        return true;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
/// Converts boolean to Online/Offline text
/// </summary>
public class BoolToOnlineConverter : IValueConverter
{
    public static readonly BoolToOnlineConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool boolValue)
            return boolValue ? "Online" : "Offline";
        return "Offline";
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
/// Converts zero count to visibility (shows when zero)
/// </summary>
public class ZeroToVisibilityConverter : IValueConverter
{
    public static readonly ZeroToVisibilityConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is int intValue)
            return intValue == 0;
        return false;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
/// Converts non-zero count to visibility (shows when > 0)
/// </summary>
public class NonZeroToVisibilityConverter : IValueConverter
{
    public static readonly NonZeroToVisibilityConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is int intValue)
            return intValue > 0;
        return false;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
/// Converts null to boolean (true if not null)
/// Supports inversion via ConverterParameter="Invert"
/// </summary>
public class NullToBoolConverter : IValueConverter
{
    public static readonly NullToBoolConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var isNotNull = value != null;
        
        // Check if we should invert the result
        if (parameter is string param && param.Equals("Invert", StringComparison.OrdinalIgnoreCase))
        {
            return !isNotNull;
        }
        
        return isNotNull;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
/// Converts boolean to speed mode text (WiFi/High-Speed)
/// </summary>
public class BoolToSpeedModeConverter : IValueConverter
{
    public static readonly BoolToSpeedModeConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool isHighSpeed)
        {
            return isHighSpeed ? "?? Wired Mode" : "?? WiFi Mode";
        }
        return "?? WiFi Mode";
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
/// Converts string color to SolidColorBrush
/// </summary>
public class StringToColorBrushConverter : IValueConverter
{
    public static readonly StringToColorBrushConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is string colorString && !string.IsNullOrEmpty(colorString))
        {
            try
            {
                return new SolidColorBrush(Color.Parse(colorString));
            }
            catch
            {
                return new SolidColorBrush(Colors.Gray);
            }
        }
        return new SolidColorBrush(Colors.Gray);
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
/// Converts GamePlatform to icon path
/// </summary>
public class GamePlatformToIconConverter : IValueConverter
{
    public static readonly GamePlatformToIconConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is GamePlatform platform)
        {
            return platform switch
            {
                GamePlatform.Steam => "avares://GamesLocalShare/Icons/steam.png",
                GamePlatform.EpicGames => "avares://GamesLocalShare/Icons/epic.png",
                GamePlatform.Xbox => "avares://GamesLocalShare/Icons/xbox.png",
                _ => null
            };
        }
        return null;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
/// Converts boolean IsHidden to "Hide from network" or "Show on network" text
/// </summary>
public class BoolToHideShowTextConverter : IValueConverter
{
    public static readonly BoolToHideShowTextConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool isHidden)
        {
            return isHidden ? "Show on Network" : "Hide from Network";
        }
        return "Hide from Network";
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
/// Converts boolean IsHidden to eye icon (visible/hidden)
/// </summary>
public class BoolToHideShowIconConverter : IValueConverter
{
    public static readonly BoolToHideShowIconConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool isHidden)
        {
            return isHidden ? "??" : "?????";
        }
        return "?????";
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
/// Converts enum value to boolean based on parameter
/// Usage: {Binding Status, Converter={x:Static conv:EnumToBoolConverter.Instance}, ConverterParameter=Downloading}
/// </summary>
public class EnumToBoolConverter : IValueConverter
{
    public static readonly EnumToBoolConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value == null || parameter == null)
            return false;

        var enumValue = value.ToString();
        var targetValue = parameter.ToString();

        return string.Equals(enumValue, targetValue, StringComparison.OrdinalIgnoreCase);
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
