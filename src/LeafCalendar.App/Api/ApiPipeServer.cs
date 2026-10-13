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
/// program (its file path, from Windows) must be approved; a new one is asked once per connection, and one removed in
/// Settings is refused at its next request. Nothing escapes: a failing connection is logged by its error type and closed.
/// </remarks>
internal sealed class ApiPipeServer(string name, ApiDispatcher dispatcher, Func<string, bool> isApproved, Func<string, CancellationToken, Task<bool>> approve, AppLog log) : IAsyncDisposable
{
    /// <summary>The most apps connected at once.</summary>
    public const int MaxConnections = 8;

    private static readonly TimeSpan IdleLimit = TimeSpan.FromMinutes(10);

    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _slots = new(MaxConnections, MaxConnections);
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

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        if (_accepting is { } accepting)
        {
            await accepting;
        }

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
                _ = Task.Run(() => ServeAsync(connected, ct), CancellationToken.None);

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
                if (ClientPath(pipe) is not { } path)
                {
                    log.Info("api.client.unknown");
                    return;
                }

                var file = Path.GetFileName(path);
                log.Info("api.connected", $"client={file}");
                var reader = new ApiLineReader(pipe);
                await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true, NewLine = "\n" };
                var asked = false;
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

                    // Approved Now, Or Asked Once On This Connection (an app removed in Settings is refused here)
                    if (!isApproved(path))
                    {
                        if (asked || !await approve(path, ct))
                        {
                            log.Info("api.refused", $"client={file}");
                            await writer.WriteLineAsync(ApiMessage.NotApproved(line));
                            return;
                        }

                        asked = true;
                    }

                    if (await dispatcher.HandleAsync(line, ct) is { } reply)
                    {
                        await writer.WriteLineAsync(reply);
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

    // The connecting program's full path, from Windows (null when it can't be read, such as a process that already ended)
    private static unsafe string? ClientPath(NamedPipeServerStream pipe)
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
                ? new string(buffer, 0, (int)size)
                : null;
        }
        finally
        {
            PInvoke.CloseHandle(process);
        }
    }
}
