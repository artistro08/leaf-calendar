using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using LeafCalendar.Core.Api;
using LeafCalendar.Core.Diagnostics;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Threading;

namespace LeafCalendar.App.Api;

/// <summary>
/// The local API's named pipe (local API spec 3.3): locked to the current Windows user, never shared with another program
/// that took the name first, at most <see cref="MaxConnections"/> apps at once.
/// </summary>
/// <remarks>
/// Each connection reads lines (<see cref="ApiLineReader"/>, 1 MB each), answers them in order through the
/// <see cref="ApiDispatcher"/>, and closes after 10 minutes without a request. Before a request runs, the connecting
/// program (its file path, from Windows) must be approved; a new one is asked once per connection, with its icon and return link from the first line
/// (<see cref="ApiHello"/>), and one removed in
/// Settings is refused at its next request. Nothing escapes: a failing connection is logged by its error type and closed.
/// </remarks>
internal sealed class ApiPipeServer(string name, ApiDispatcher dispatcher, Func<string, string?, string?, bool> isApproved, Func<ApiCaller, CancellationToken, Task<bool>> approve, AppLog log) : IAsyncDisposable
{
    /// <summary>The most apps connected at once.</summary>
    public const int MaxConnections = 8;

    private static readonly TimeSpan IdleLimit = TimeSpan.FromMinutes(10);

    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _slots = new(MaxConnections, MaxConnections);
    private readonly List<Task> _serving = [];
    private Task? _accepting;

    /// <summary>Opens the pipe; false when another program already holds the name (the API stays off).</summary>
    public bool Start()
    {
        NamedPipeServerStream first;
        try
        {
            first = Create(firstInstance: true);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            log.Info("api.name-taken", $"error={ex.GetType().Name}");
            return false;
        }

        _accepting = Task.Run(() => AcceptAsync(first, _stop.Token));
        log.Info("api.started");
        return true;
    }

    /// <summary>Closes the pipe and every connection; it completes once no instance of the name is left open.</summary>
    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        if (_accepting is { } accepting)
        {
            await accepting;
        }

        // Each connection holds an instance of the name, so reopening before they close would read as "name taken"
        Task[] serving;
        lock (_serving)
        {
            serving = [.. _serving];
        }

        await Task.WhenAll(serving);

        _stop.Dispose();
        log.Info("api.stopped");
    }

    private NamedPipeServerStream Create(bool firstInstance) =>
        new(name, PipeDirection.InOut, MaxConnections, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly | (firstInstance ? PipeOptions.FirstPipeInstance : PipeOptions.None));

    // One instance waits at a time; a connected one is handed off and the next is made once a slot is free
    private async Task AcceptAsync(NamedPipeServerStream first, CancellationToken ct)
    {
        NamedPipeServerStream? waiting = first;
        try
        {
            await _slots.WaitAsync(ct);
            while (true)
            {
                await waiting.WaitForConnectionAsync(ct);
                var connected = waiting;
                waiting = null;
                var serving = Task.Run(() => ServeAsync(connected, ct), CancellationToken.None);
                lock (_serving)
                {
                    _serving.RemoveAll(t => t.IsCompleted);
                    _serving.Add(serving);
                }

                await _slots.WaitAsync(ct);
                waiting = Create(firstInstance: false);
            }
        }
        catch (OperationCanceledException)
        {
            // Stopped
        }
        catch (Exception ex)
        {
            log.Info("api.accept.failed", $"error={ex.GetType().Name}");
        }
        finally
        {
            if (waiting is not null)
            {
                await waiting.DisposeAsync();
            }
        }
    }

    private async Task ServeAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        try
        {
            await using (pipe)
            {
                if (Client(pipe) is not { } client)
                {
                    log.Info("api.client.unknown");
                    return;
                }

                var (processId, path, package) = client;
                var file = Path.GetFileName(path);
                var app = PackageApp(path, package);
                log.Info("api.connected", $"client={file}");
                var reader = new ApiLineReader(pipe);
                await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true, NewLine = "\n" };
                var asked = false;
                var first = true;
                while (true)
                {
                    string? line;
                    using (var idle = CancellationTokenSource.CreateLinkedTokenSource(ct))
                    {
                        idle.CancelAfter(IdleLimit);
                        line = await reader.ReadLineAsync(idle.Token);
                    }

                    if (line is null)
                    {
                        return;
                    }

                    if (line.Length == 0)
                    {
                        continue;
                    }

                    // What The App Sent With Its First Line (its icon and return link), For The Question
                    var hello = first ? ApiHello.Read(line) : ApiHello.None;
                    first = false;

                    // Approved Now, Or Asked Once On This Connection (an app removed in Settings is refused here)
                    if (!isApproved(path, app?.Key, app?.Program))
                    {
                        if (asked || !await approve(new ApiCaller(path, app?.Name ?? ClientName(path), processId, hello, app?.Program, app?.Key), ct))
                        {
                            log.Info("api.refused", $"client={file}");
                            await writer.WriteLineAsync(ApiMessage.NotApproved(line).AsMemory(), ct);
                            return;
                        }

                        asked = true;
                    }

                    // Cancelable, So An App That Stops Reading Can't Hold The Pipe Open When The API Stops
                    if (await dispatcher.HandleAsync(line, ct) is { } reply)
                    {
                        await writer.WriteLineAsync(reply.AsMemory(), ct);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Idle too long, or the API stopped
        }
        catch (ApiLineTooLongException)
        {
            log.Info("api.line.too-long");
        }
        catch (IOException)
        {
            // The app went away
        }
        catch (Exception ex)
        {
            log.Info("api.connection.failed", $"error={ex.GetType().Name}");
        }
        finally
        {
            _slots.Release();
        }
    }

    // The connecting program's process ID, full path and app package (null when it isn't packaged), from Windows (null when
    // they can't be read, such as a process that already ended)
    private static unsafe (int ProcessId, string Path, (string FullName, string Folder)? Package)? Client(NamedPipeServerStream pipe)
    {
        uint pid;
        if (!PInvoke.GetNamedPipeClientProcessId((HANDLE)pipe.SafePipeHandle.DangerousGetHandle(), &pid))
        {
            return null;
        }

        var process = PInvoke.OpenProcess(PROCESS_ACCESS_RIGHTS.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (process.IsNull)
        {
            return null;
        }

        try
        {
            var buffer = stackalloc char[1024];
            var size = 1024u;
            return PInvoke.QueryFullProcessImageName(process, PROCESS_NAME_FORMAT.PROCESS_NAME_WIN32, buffer, &size)
                ? ((int)pid, new string(buffer, 0, (int)size), Package(process))
                : null;
        }
        finally
        {
            PInvoke.CloseHandle(process);
        }
    }

    // The full name and install folder of the process's app package, from its package identity (null for a program that
    // isn't packaged)
    private static unsafe (string FullName, string Folder)? Package(HANDLE process)
    {
        // Full names are at most 127 characters, and the folder is under WindowsApps; both lengths count the closing null
        var name = stackalloc char[128];
        var nameLength = 128u;
        if (PInvoke.GetPackageFullName(process, &nameLength, name) != WIN32_ERROR.ERROR_SUCCESS)
        {
            return null;
        }

        var folder = stackalloc char[1024];
        var folderLength = 1024u;
        var fullName = new string(name, 0, (int)nameLength - 1);
        fixed (char* fullNamePointer = fullName)
        {
            return PInvoke.GetPackagePathByFullName(fullNamePointer, &folderLength, folder) == WIN32_ERROR.ERROR_SUCCESS
                ? (fullName, new string(folder, 0, (int)folderLength - 1))
                : null;
        }
    }

    // The app that owns a program inside its own package's folder (Raycast and Raycast.exe for Raycast's copy of Node, from
    // the package's manifest) and the key its approval is kept under; null when the program isn't packaged, sits outside
    // the folder, or the manifest lacks a plain name or a main program
    private static (string Name, string Program, string Key)? PackageApp(string path, (string FullName, string Folder)? package)
    {
        if (package is not var (fullName, packageFolder) || ApiClients.PackageKey(fullName, path, packageFolder) is not { } key)
        {
            return null;
        }

        try
        {
            var manifest = File.ReadAllText(Path.Combine(packageFolder, "AppxManifest.xml"));
            return ApiClients.ManifestDisplayName(manifest) is { } name && ApiClients.ManifestProgram(manifest, packageFolder) is { } program
                ? (name, program, key)
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    // The program's name from its file (ApiClients.DisplayName picks it)
    private static string ClientName(string path)
    {
        try
        {
            var info = FileVersionInfo.GetVersionInfo(path);
            return ApiClients.DisplayName(info.FileDescription, info.ProductName, path);
        }
        catch (Exception ex) when (ex is FileNotFoundException or IOException or UnauthorizedAccessException)
        {
            return ApiClients.DisplayName(null, null, path);
        }
    }
}
