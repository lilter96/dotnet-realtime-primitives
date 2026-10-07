using System.Text;

namespace ShadowPrompter.Core.Observability.Flight;

/// <summary>
/// The safety net: one JSON object per line in a file next to the session journal. It needs no network, no container and no exporter, and an
/// <c>--analyze</c>-style reader (<c>scripts/analyze_session.py</c>) needs nothing else. Append-only, flushed every second and at the end;
/// a file that grows past <see cref="MaxBytes"/> stops (and says so once), so a runaway session cannot fill a disk.
/// </summary>
public sealed class JsonlFlightSink : IFlightSink
{
    public const long DefaultMaxBytes = 256L * 1024 * 1024;

    private readonly FileStream _stream;
    private readonly long _maxBytes;
    private long _written;
    private bool _capped;

    public JsonlFlightSink(string path, long maxBytes = DefaultMaxBytes)
    {
        Path = path;
        _maxBytes = maxBytes;
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))!);
        _stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete, 64 * 1024);
    }

    public string Path { get; }

    public string Name => "file";

    public void Write(in FlightRecord record)
    {
        if (_capped) return;
        var length = record.Json.Length + 1;
        if (_written + length > _maxBytes)
        {
            _capped = true;
            var note = Encoding.UTF8.GetBytes($"{{\"k\":\"flight.capped\",\"max_bytes\":{_maxBytes}}}\n");
            _stream.Write(note);
            return;
        }

        _stream.Write(record.Json.Span);
        _stream.WriteByte((byte)'\n');
        _written += length;
    }

    public void Flush() => _stream.Flush();

    public void Dispose() => _stream.Dispose();
}
