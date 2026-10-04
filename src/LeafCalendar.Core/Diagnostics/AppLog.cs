using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace LeafCalendar.Core.Diagnostics;

/// <summary>
/// Small rolling log file for troubleshooting.
/// </summary>
/// <remarks>
/// Callers log an event name plus internal IDs, never event titles, descriptions, guests, or
/// locations. As a second line of defense, every detail passes through <see cref="Redact"/>,
/// which masks Google tokens, auth codes, client secrets, JWTs, and email addresses. The file
/// rolls to <c>leaf.log.1</c> and <c>leaf.log.2</c> after 1 MB each, so at most about 3 MB is kept.
/// With <see cref="Detailed"/> on (Settings › About › Detailed logging), <see cref="Trace"/> adds a breadcrumb trail
/// of navigation and commands, and crash dumps may be written next to the log (at most <see cref="MaxDumps"/>).
/// </remarks>
public sealed partial class AppLog(string directory, TimeProvider time)
{
    private const long MaxBytes = 1_000_000;
    private const int Generations = 2;

    /// <summary>Most crash dumps kept in the log folder (the oldest goes first).</summary>
    public const int MaxDumps = 2;

    private readonly Lock _gate = new();

    /// <summary>Current log file.</summary>
    public string FilePath => Path.Combine(directory, "leaf.log");

    /// <summary>The log folder.</summary>
    public string Directory => directory;

    /// <summary>Detailed logging: breadcrumbs (<see cref="Trace"/>) and crash dumps. Off by default.</summary>
    public bool Detailed { get; set; }

    /// <summary>Writes an informational line.</summary>
    public void Info(string eventName, string? detail = null) => Write("INFO", eventName, detail);

    /// <summary>Writes an error line with the exception type and redacted message (no stack trace).</summary>
    public void Error(string eventName, Exception exception) =>
        Write("ERROR", eventName, $"{exception.GetType().Name}: {exception.Message}");

    /// <summary>Writes a breadcrumb (a navigation or a command, internal names only) when <see cref="Detailed"/> is on.</summary>
    public void Trace(string eventName, string? detail = null)
    {
        if (Detailed)
        {
            Write("TRACE", eventName, detail);
        }
    }

    /// <summary>
    /// Writes a crash: the exception's type, HRESULT, and stack trace (with file and line where the build has them), and
    /// the same for each inner exception. Always written, whatever <see cref="Detailed"/> says. An exception's own message
    /// can carry event content, so it's left out; <paramref name="message"/> is for a framework's message (a XAML
    /// error's), and is redacted like every line.
    /// </summary>
    public void Crash(string eventName, Exception exception, string? message = null)
    {
        var detail = new StringBuilder(string.Create(CultureInfo.InvariantCulture, $"error={exception.GetType().FullName} hresult=0x{exception.HResult:X8}"));
        if (message is { Length: > 0 })
        {
            detail.Append(" message=").Append(message);
        }

        Write("CRASH", eventName, detail.ToString(), StackLines(exception));
    }

    /// <summary>
    /// A path for a new crash dump in the log folder, after removing the oldest dumps (Leaf's and Windows Error
    /// Reporting's) so at most <see cref="MaxDumps"/> remain once it's written.
    /// </summary>
    public string NextDumpPath()
    {
        lock (_gate)
        {
            System.IO.Directory.CreateDirectory(directory);
            foreach (var old in new DirectoryInfo(directory).GetFiles("*.dmp").OrderByDescending(f => f.LastWriteTimeUtc).Skip(MaxDumps - 1))
            {
                try
                {
                    old.Delete();
                }
                catch (IOException)
                {
                    // In use; the next crash tries again
                }
                catch (UnauthorizedAccessException)
                {
                    // Not ours to delete; the next crash tries again
                }
            }

            return Path.Combine(directory, string.Create(CultureInfo.InvariantCulture, $"crash-{time.GetUtcNow():yyyyMMdd-HHmmss-fff}.dmp"));
        }
    }

    /// <summary>Deletes every crash dump in the log folder (Detailed logging was turned off).</summary>
    public void DeleteDumps()
    {
        lock (_gate)
        {
            if (!System.IO.Directory.Exists(directory))
            {
                return;
            }

            foreach (var dump in new DirectoryInfo(directory).GetFiles("*.dmp"))
            {
                try
                {
                    dump.Delete();
                }
                catch (IOException)
                {
                    // In use; the next switch-off tries again
                }
                catch (UnauthorizedAccessException)
                {
                    // Not ours to delete
                }
            }
        }
    }

    /// <summary>Masks secrets and email addresses in <paramref name="text"/>.</summary>
    public static string Redact(string text) =>
        EmailPattern().Replace(TokenPattern().Replace(text, "[token]"), "[email]");

    // The stack of the exception and each inner one (every one of an AggregateException's), indented under the crash
    // line (frames come from the code, not the user)
    private static List<string> StackLines(Exception exception)
    {
        var lines = new List<string>();
        AddStack(lines, exception, top: true);
        return lines;
    }

    private static void AddStack(List<string> lines, Exception e, bool top)
    {
        if (!top)
        {
            lines.Add(string.Create(CultureInfo.InvariantCulture, $"  caused by {e.GetType().FullName} hresult=0x{e.HResult:X8}"));
        }

        foreach (var frame in (e.StackTrace ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            lines.Add("    " + frame);
        }

        IEnumerable<Exception> inner = e is AggregateException aggregate ? aggregate.InnerExceptions : e.InnerException is { } one ? [one] : [];
        foreach (var next in inner)
        {
            AddStack(lines, next, top: false);
        }
    }

    private void Write(string level, string eventName, string? detail, IReadOnlyList<string>? extra = null)
    {
        // Sanitize newlines to prevent log forging
        var sanitized = detail?.Replace("\r", " ").Replace("\n", " ");

        var text = new StringBuilder(sanitized is null
            ? $"{time.GetUtcNow():O} {level} {eventName}"
            : $"{time.GetUtcNow():O} {level} {eventName} {Redact(sanitized)}");
        text.Append(Environment.NewLine);
        foreach (var line in extra ?? [])
        {
            text.Append(Redact(line.Replace("\r", " ").Replace("\n", " "))).Append(Environment.NewLine);
        }

        try
        {
            lock (_gate)
            {
                System.IO.Directory.CreateDirectory(directory);

                // Roll At 1 MB (leaf.log -> .1 -> .2, the oldest dropped)
                var file = new FileInfo(FilePath);
                if (file.Exists && file.Length > MaxBytes)
                {
                    for (var g = Generations; g > 1; g--)
                    {
                        var older = $"{FilePath}.{g - 1}";
                        if (File.Exists(older))
                        {
                            File.Move(older, $"{FilePath}.{g}", overwrite: true);
                        }
                    }

                    File.Move(FilePath, FilePath + ".1", overwrite: true);
                }

                File.AppendAllText(FilePath, text.ToString());
            }
        }
        catch (IOException)
        {
            // File locked, full disk, or move race; silently drop the line to avoid masking caller's error.
        }
        catch (UnauthorizedAccessException)
        {
            // Permission denied; silently drop the line to avoid masking caller's error.
        }
    }

    [GeneratedRegex(@"(ya29\.[\w\-.]+|1(?:/|%2[Ff]){2}[\w\-.]+|4(?:/|%2[Ff])[\w\-.]+|eyJ[\w\-.]+|GOCSPX-[\w\-]+)")]
    private static partial Regex TokenPattern();

    [GeneratedRegex(@"[\w.+\-]+(?:@|%40)[\w\-]+(?:\.[\w\-]+)+")]
    private static partial Regex EmailPattern();
}
