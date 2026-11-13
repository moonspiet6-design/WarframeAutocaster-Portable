using System.Text;
using System.Threading.Channels;
using Autocaster.Core;

namespace Autocaster.Infrastructure;

public sealed class FileRunLog : IDetailedRunLog, IAsyncDisposable
{
    private const long FileLimit = 2 * 1024 * 1024;
    private readonly string _folder;
    private readonly Channel<string> _messages = Channel.CreateBounded<string>(new BoundedChannelOptions(4096)
    {
        SingleReader = true,
        FullMode = BoundedChannelFullMode.DropOldest
    });
    private readonly Task _writer;
    private volatile bool _detailed;
    private volatile string? _error;

    public FileRunLog(string folder)
    {
        _folder = folder;
        _writer = Task.Run(Consume);
    }

    public bool Detailed { get => _detailed; set => _detailed = value; }
    public string? Error => _error;

    public void Write(string message, bool detailed = false)
    {
        if (!detailed || Detailed)
            _messages.Writer.TryWrite($"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz}  {message}");
    }

    private async Task Consume()
    {
        try
        {
            Directory.CreateDirectory(_folder);
            string active = Path.Combine(_folder, "autocaster.log");
            string old = Path.Combine(_folder, "autocaster.previous.log");
            var batch = new StringBuilder();
            while (await _messages.Reader.WaitToReadAsync().ConfigureAwait(false))
            {
                batch.Clear();
                for (int i = 0; i < 128 && _messages.Reader.TryRead(out var line); i++)
                    batch.AppendLine(line);
                if (File.Exists(active) && new FileInfo(active).Length >= FileLimit)
                    File.Move(active, old, overwrite: true);
                await File.AppendAllTextAsync(active, batch.ToString()).ConfigureAwait(false);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            _error = error.Message;
            _messages.Writer.TryComplete(error);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _messages.Writer.TryComplete();
        await _writer.ConfigureAwait(false);
    }
}
