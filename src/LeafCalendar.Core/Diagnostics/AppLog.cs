using System.Text.RegularExpressions;

namespace LeafCalendar.Core.Diagnostics;

/// <summary>
/// Small rolling log file for troubleshooting.
/// </summary>
/// <remarks>
/// Callers log an event name plus internal IDs, never event titles, descriptions, guests, or
/// locations. As a second line of defense, every detail passes through <see cref="Redact"/>,
/// which masks Google tokens, auth codes, client secrets, JWTs, and email addresses. The file
/// rolls to <c>leaf.log.1</c> after 1 MB, so at most about 2 MB is kept.
/// </remarks>
public sealed partial class AppLog(string directory, TimeProvider time)
{
    const long MaxBytes = 1_000_000;

    readonly Lock _gate = new();

    /// <summary>Current log file.</summary>
    public string FilePath => Path.Combine(directory, "leaf.log");

    /// <summary>Writes an informational line.</summary>
    public void Info(string eventName, string? detail = null) => Write("INFO", eventName, detail);

    /// <summary>Writes an error line with the exception type and redacted message (no stack trace).</summary>
    public void Error(string eventName, Exception exception) =>
        Write("ERROR", eventName, $"{exception.GetType().Name}: {exception.Message}");

    /// <summary>Masks secrets and email addresses in <paramref name="text"/>.</summary>
    public static string Redact(string text) =>
        EmailPattern().Replace(TokenPattern().Replace(text, "[token]"), "[email]");

    void Write(string level, string eventName, string? detail)
    {
        var line = detail is null
            ? $"{time.GetUtcNow():O} {level} {eventName}"
            : $"{time.GetUtcNow():O} {level} {eventName} {Redact(detail)}";

        lock (_gate)
        {
            Directory.CreateDirectory(directory);

            // Roll At 1 MB
            var file = new FileInfo(FilePath);
            if (file.Exists && file.Length > MaxBytes)
            {
                File.Move(FilePath, FilePath + ".1", overwrite: true);
            }

            File.AppendAllText(FilePath, line + Environment.NewLine);
        }
    }

    [GeneratedRegex(@"(ya29\.[\w\-.]+|1//[\w\-.]+|4/[\w\-.]+|eyJ[\w\-.]+|GOCSPX-[\w\-]+)")]
    private static partial Regex TokenPattern();

    [GeneratedRegex(@"[\w.+\-]+@[\w\-]+(\.[\w\-]+)+")]
    private static partial Regex EmailPattern();
}
