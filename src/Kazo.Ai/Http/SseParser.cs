using System.Text;

namespace Kazo.Ai.Http;

/// <summary>
/// Reads a Server-Sent Events stream (<c>text/event-stream</c>) piece by piece: the pieces of the
/// network split lines anywhere, so the parser keeps the unfinished line until the next piece.
/// Only the <c>data:</c> fields matter to the chat APIs; an event is complete at an empty line.
/// Comments (<c>: keep-alive</c>, which OpenRouter sends while a model thinks) are skipped.
/// </summary>
public sealed class SseParser
{
    /// <summary>The longest event kept (a broken stream must not fill the memory).</summary>
    public const int MaxEventLength = 4 * 1024 * 1024;

    private readonly StringBuilder _line = new();
    private readonly StringBuilder _data = new();
    private bool _hasData;

    /// <summary>Adds a piece of the stream.</summary>
    /// <param name="chunk">The piece.</param>
    /// <returns>The data of every event completed by this piece.</returns>
    public IReadOnlyList<string> Push(string chunk)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        var events = new List<string>();
        foreach (char c in chunk)
        {
            if (c == '\n')
            {
                EndLine(events);
            }
            else if (c != '\r')
            {
                if (_line.Length < MaxEventLength)
                {
                    _line.Append(c);
                }
            }
        }

        return events;
    }

    /// <summary>Ends the stream: an event without its final empty line is still given.</summary>
    /// <returns>The data of the last event, if any.</returns>
    public IReadOnlyList<string> Complete()
    {
        var events = new List<string>();
        if (_line.Length > 0)
        {
            EndLine(events);
        }

        Flush(events);
        return events;
    }

    private void EndLine(List<string> events)
    {
        string line = _line.ToString();
        _line.Clear();
        if (line.Length == 0)
        {
            Flush(events);
            return;
        }

        if (line[0] == ':')
        {
            return;
        }

        int colon = line.IndexOf(':', StringComparison.Ordinal);
        string field = colon < 0 ? line : line[..colon];
        if (field != "data")
        {
            return;
        }

        string value = colon < 0 ? string.Empty : line[(colon + 1)..];
        if (value.StartsWith(' '))
        {
            value = value[1..];
        }

        if (_hasData)
        {
            _data.Append('\n');
        }

        if (_data.Length + value.Length <= MaxEventLength)
        {
            _data.Append(value);
        }

        _hasData = true;
    }

    private void Flush(List<string> events)
    {
        if (_hasData)
        {
            events.Add(_data.ToString());
        }

        _data.Clear();
        _hasData = false;
    }
}
