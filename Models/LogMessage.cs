namespace GamesLocalShare.Models;

/// <summary>
/// Represents a log message entry
/// </summary>
public class LogMessage
{
    /// <summary>
    /// When the message was created
    /// </summary>
    public DateTime Timestamp { get; set; } = DateTime.Now;

    /// <summary>
    /// The log message text
    /// </summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>
    /// Type of log message
    /// </summary>
    public LogMessageType Type { get; set; } = LogMessageType.Info;

    /// <summary>
    /// Formatted timestamp for display
    /// </summary>
    public string FormattedTime => Timestamp.ToString("HH:mm:ss");

    /// <summary>
    /// Color based on message type
    /// </summary>
    public string TypeColor => Type switch
    {
        LogMessageType.Success => "#22C55E",  // Green
        LogMessageType.Error => "#EF4444",    // Red
        LogMessageType.Warning => "#F59E0B",  // Orange
        LogMessageType.Transfer => "#8B5CF6", // Purple
        LogMessageType.Network => "#3B82F6",  // Blue
        _ => "#9CA3AF"                        // Gray
    };

    /// <summary>
    /// Icon based on message type (using emoji characters)
    /// </summary>
    public string TypeIcon => Type switch
    {
        LogMessageType.Success => "?",      // Checkmark
        LogMessageType.Error => "?",        // X mark
        LogMessageType.Warning => "?",      // Warning sign
        LogMessageType.Transfer => "?",     // Down arrow
        LogMessageType.Network => "??",     // Satellite antenna
        _ => "?"                            // Info symbol
    };

    public LogMessage() { }

    public LogMessage(string message, LogMessageType type = LogMessageType.Info)
    {
        Message = message;
        Type = type;
        Timestamp = DateTime.Now;
    }
}

public enum LogMessageType
{
    Info,
    Success,
    Error,
    Warning,
    Transfer,
    Network
}
