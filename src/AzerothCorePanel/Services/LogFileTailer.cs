using System.IO;
using System.Text;

namespace AzerothCorePanel.Services;

/// <summary>
/// Follows a log file that another process is writing and reports each new line.
/// </summary>
internal sealed class LogFileTailer : IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(300);

    private readonly string _path;
    private readonly Action<string> _onLine;
    private readonly DateTime _notBeforeUtc;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _lock = new();
    private readonly Decoder _decoder = new UTF8Encoding(false).GetDecoder();
    private readonly StringBuilder _partial = new();
    private readonly byte[] _buffer = new byte[64 * 1024];
    private long _position;
    private bool _started;

    /// <param name="path">Log file to follow.</param>
    /// <param name="processStartUtc">When the server started.</param>
    /// <param name="truncatedOnStart">
    /// True when the server empties the file when it starts: the file is then read from the beginning,
    /// but only after the server wrote to it, so the previous run's lines are not shown again.
    /// Otherwise only lines added after now are read.
    /// </param>
    public LogFileTailer(string path, DateTime processStartUtc, bool truncatedOnStart, Action<string> onLine)
    {
        _path = path;
        _onLine = onLine;

        if (truncatedOnStart)
        {
            _notBeforeUtc = processStartUtc.AddSeconds(-1);
        }
        else
        {
            _notBeforeUtc = DateTime.MinValue;
            _position = File.Exists(path) ? new FileInfo(path).Length : 0;
        }

        _ = Task.Run(LoopAsync);
    }

    /// <summary>Reads what is left in the file and stops following it.</summary>
    public void Dispose()
    {
        _cts.Cancel();
        ReadNewLines();
    }

    private async Task LoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            ReadNewLines();

            try
            {
                await Task.Delay(PollInterval, _cts.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private void ReadNewLines()
    {
        lock (_lock)
        {
            try
            {
                if (!File.Exists(_path))
                {
                    return;
                }

                if (!_started)
                {
                    // Wait until the new run touched the file (truncating it), so old lines are skipped.
                    if (File.GetLastWriteTimeUtc(_path) < _notBeforeUtc)
                    {
                        return;
                    }

                    _started = true;
                }

                using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);

                if (stream.Length < _position)
                {
                    // The file was emptied: start over.
                    _position = 0;
                    _partial.Clear();
                    _decoder.Reset();
                }

                stream.Position = _position;

                int read;
                while ((read = stream.Read(_buffer, 0, _buffer.Length)) > 0)
                {
                    _position += read;
                    var chars = new char[_decoder.GetCharCount(_buffer, 0, read)];
                    _decoder.GetChars(_buffer, 0, read, chars, 0);

                    foreach (char c in chars)
                    {
                        if (c == '\n')
                        {
                            _onLine(_partial.ToString().TrimEnd('\r'));
                            _partial.Clear();
                        }
                        else
                        {
                            _partial.Append(c);
                        }
                    }
                }
            }
            catch (IOException)
            {
                // The file is busy or being replaced; try again on the next poll.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
