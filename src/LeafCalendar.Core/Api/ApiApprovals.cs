using System.Security.Cryptography;

namespace LeafCalendar.Core.Api;

/// <summary>
/// A program asking to connect: its full path and name (from its file), its process (to bring its window back after
/// Allow), and what it sent with its first <c>leaf.hello</c>. <see cref="AppProgram"/> is set when the program runs inside
/// its own app package's folder (such as Raycast's copy of Node): only that app runs it, so <see cref="Name"/> is the app's
/// and the app's main program stands for it (its icon over any icon the caller sent); <see cref="PackageKey"/> is what its
/// approval is kept under then.
/// </summary>
public sealed record ApiCaller(string Path, string Name, int ProcessId, ApiHello Hello, string? AppProgram = null, string? PackageKey = null)
{
    /// <summary>True when the caller runs inside its own app package's folder (<see cref="AppProgram"/> is set).</summary>
    public bool Packaged => AppProgram is not null;
}

/// <summary>An approval waiting on your answer: its random token, and the program asking.</summary>
public sealed record PendingApproval(string Token, ApiCaller Caller);

/// <summary>
/// The one-time question "Allow {app} to connect?" (local API spec 3.5).
/// </summary>
/// <remarks>
/// One question per program path at a time (case ignored): other connections from it wait on the same answer. No answer
/// within <see cref="Wait"/> is a no, but isn't remembered (nor is a wait the API's stop cut short); Don't allow is remembered until Leaf Calendar restarts, so a retrying
/// app can't keep prompting. Tokens are 128 random bits. Approved paths
/// are saved by the caller (in settings) before it calls <see cref="Answer"/>.
/// </remarks>
public sealed class ApiApprovals(TimeProvider time)
{
    /// <summary>How long a connection waits for your answer.</summary>
    public static readonly TimeSpan Wait = TimeSpan.FromSeconds(60);

    /// <summary>How long an app that asked to be asked from a link (<see cref="WaitForLink"/>) waits for that link.</summary>
    public static readonly TimeSpan LinkWait = TimeSpan.FromMinutes(5);

    private readonly Lock _gate = new();
    private readonly Dictionary<string, (PendingApproval Request, TaskCompletionSource<bool> Answer)> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _refused = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (ApiCaller Caller, DateTimeOffset Until)> _linked = new(StringComparer.Ordinal);

    /// <summary>
    /// Holds the question for <paramref name="caller"/> until <c>leaf-calendar://connect?code=<paramref name="code"/></c>
    /// opens, within <see cref="LinkWait"/>; a newer code from the same path replaces an older one. False (nothing held)
    /// when you chose Don't allow for this path since Leaf Calendar started.
    /// </summary>
    public bool WaitForLink(ApiCaller caller, string code)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentException.ThrowIfNullOrEmpty(code);

        lock (_gate)
        {
            if (_refused.Contains(caller.Path))
            {
                return false;
            }

            // Expired Codes And This Path's Older Code Go
            var now = time.GetUtcNow();
            foreach (var (old, waiting) in _linked)
            {
                if (waiting.Until <= now || string.Equals(waiting.Caller.Path, caller.Path, StringComparison.OrdinalIgnoreCase))
                {
                    _linked.Remove(old);
                }
            }

            _linked[code] = (caller, now + LinkWait);
            return true;
        }
    }

    /// <summary>The app a connect link's code belongs to, once (the code is used up), or null when unknown or expired.</summary>
    public ApiCaller? TakeLinked(string code)
    {
        lock (_gate)
        {
            if (!_linked.Remove(code, out var waiting))
            {
                return null;
            }

            return waiting.Until > time.GetUtcNow() && !_refused.Contains(waiting.Caller.Path) ? waiting.Caller : null;
        }
    }

    /// <summary>
    /// True while a connect link's code still waits: not opened yet, within <see cref="LinkWait"/>, and its path not
    /// refused, so the app's later lines are told to open the link rather than asked about at once.
    /// </summary>
    public bool IsWaitingForLink(string code)
    {
        lock (_gate)
        {
            return _linked.TryGetValue(code, out var waiting) && waiting.Until > time.GetUtcNow() && !_refused.Contains(waiting.Caller.Path);
        }
    }

    /// <summary>True when you chose Don't allow for this path since Leaf Calendar started.</summary>
    public bool IsRefused(string path)
    {
        lock (_gate)
        {
            return _refused.Contains(path);
        }
    }

    /// <summary>
    /// Asks once for the caller's path (calling <paramref name="prompt"/> to show the question) and waits for the
    /// answer; false when refused before, refused now, or unanswered within <see cref="Wait"/>.
    /// </summary>
    public async Task<bool> AskAsync(ApiCaller caller, Action<PendingApproval> prompt, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(prompt);

        var path = caller.Path;
        TaskCompletionSource<bool> answer;
        PendingApproval? asked = null;
        lock (_gate)
        {
            if (_refused.Contains(path))
            {
                return false;
            }

            if (_pending.TryGetValue(path, out var waiting))
            {
                answer = waiting.Answer;
            }
            else
            {
                asked = new PendingApproval(Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16)), caller);
                answer = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                _pending[path] = (asked, answer);
            }
        }

        if (asked is not null)
        {
            prompt(asked);
        }

        try
        {
            return await answer.Task.WaitAsync(Wait, time, ct);
        }
        catch (TimeoutException)
        {
            return false;
        }
        finally
        {
            // Unanswered (timed out, or the API stopped): the next connection asks again rather than waiting on a dead question
            lock (_gate)
            {
                if (_pending.TryGetValue(path, out var waiting) && waiting.Answer == answer)
                {
                    _pending.Remove(path);
                }
            }
        }
    }

    /// <summary>The question a notification's token belongs to, or null when it was answered or timed out.</summary>
    public PendingApproval? Find(string token)
    {
        lock (_gate)
        {
            return _pending.Values.FirstOrDefault(p => p.Request.Token == token).Request;
        }
    }

    /// <summary>Answers a question; a no is remembered until Leaf Calendar restarts. An unknown token does nothing.</summary>
    public void Answer(string token, bool allow)
    {
        TaskCompletionSource<bool>? answer = null;
        lock (_gate)
        {
            foreach (var (path, waiting) in _pending)
            {
                if (waiting.Request.Token == token)
                {
                    answer = waiting.Answer;
                    _pending.Remove(path);
                    if (!allow)
                    {
                        _refused.Add(path);
                    }

                    break;
                }
            }
        }

        answer?.TrySetResult(allow);
    }
}
