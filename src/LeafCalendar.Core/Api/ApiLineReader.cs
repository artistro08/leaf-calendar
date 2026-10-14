using System.Buffers;
using System.Text;

namespace LeafCalendar.Core.Api;

/// <summary>A line longer than the local API allows; the connection closes.</summary>
public sealed class ApiLineTooLongException : Exception
{
    /// <summary>Creates the exception.</summary>
    public ApiLineTooLongException()
        : base("The message is too long.")
    {
    }

    /// <summary>Creates the exception with a message.</summary>
    public ApiLineTooLongException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a message and its cause.</summary>
    public ApiLineTooLongException(string message, Exception inner)
        : base(message, inner)
    {
    }
}

/// <summary>
/// Reads UTF-8 lines from a stream (a pipe connection), never holding more than <c>maxBytes</c> of one line.
/// </summary>
/// <remarks>
/// <see cref="StreamReader.ReadLineAsync()"/> has no length limit, so a client that never sends a newline could make it
/// buffer without end. Lines end at <c>\n</c>; a <c>\r</c> before it is dropped. The last line may end without one.
/// </remarks>
public sealed class ApiLineReader(Stream stream, int maxBytes = ApiMessage.MaxLineBytes)
{
    private readonly byte[] _buffer = new byte[4096];
    private readonly ArrayBufferWriter<byte> _line = new();
    private int _start;
    private int _end;

    /// <summary>The next line, or null at the end of the stream.</summary>
    /// <exception cref="ApiLineTooLongException">The line is longer than the limit.</exception>
    public async Task<string?> ReadLineAsync(CancellationToken ct)
    {
        while (true)
        {
            // A Newline In What's Buffered Ends The Line
            var newline = Array.IndexOf(_buffer, (byte)'\n', _start, _end - _start);
            if (newline >= 0)
            {
                Append(_start, newline - _start);
                _start = newline + 1;
                return Take();
            }

            Append(_start, _end - _start);
            _start = _end = 0;

            var read = await stream.ReadAsync(_buffer, ct);
            if (read == 0)
            {
                return _line.WrittenCount > 0 ? Take() : null;
            }

            _end = read;
        }
    }

    private void Append(int offset, int count)
    {
        if (_line.WrittenCount + count > maxBytes)
        {
            throw new ApiLineTooLongException();
        }

        _line.Write(_buffer.AsSpan(offset, count));
    }

    private string Take()
    {
        var bytes = _line.WrittenSpan;
        if (bytes.Length > 0 && bytes[^1] == (byte)'\r')
        {
            bytes = bytes[..^1];
        }

        var text = Encoding.UTF8.GetString(bytes);
        _line.ResetWrittenCount();
        return text;
    }
}
