namespace PaymentGateway.Helpers;

public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly string _logDirectory;
    private readonly LogLevel _minLevel;
    private readonly ConcurrentDictionary<string, FileLogger> _loggers = new();
    private readonly Channel<QueuedLogEntry> _channel;
    private readonly Task _processorTask;
    private long _writtenSequence;
    private bool _disposed;

    public FileLoggerProvider(string logDirectory, LogLevel minLevel)
    {
        _logDirectory = string.IsNullOrWhiteSpace(logDirectory)
            ? Path.Combine(AppContext.BaseDirectory, "logs")
            : logDirectory;
        _minLevel = minLevel;
        Directory.CreateDirectory(_logDirectory);

        _channel = Channel.CreateUnbounded<QueuedLogEntry>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });
        _processorTask = Task.Run(ProcessQueueAsync);
    }

    public ILogger CreateLogger(string categoryName)
    {
        return _loggers.GetOrAdd(categoryName, category => new FileLogger(category, _minLevel, TryQueueLog));
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _channel.Writer.TryComplete();
        try
        {
            _processorTask.GetAwaiter().GetResult();
        }
        catch
        {
            // Logger disposal should not throw.
        }
    }

    private bool TryQueueLog(QueuedLogEntry entry)
    {
        if (_disposed)
        {
            return false;
        }

        return _channel.Writer.TryWrite(entry);
    }

    private async Task ProcessQueueAsync()
    {
        StreamWriter? writer = null;
        string? currentDate = null;

        try
        {
            await foreach (var entry in _channel.Reader.ReadAllAsync())
            {
                var entryDate = entry.TimestampUtc.ToString("yyyy-MM-dd");
                if (!string.Equals(currentDate, entryDate, StringComparison.Ordinal))
                {
                    if (writer is not null)
                    {
                        await writer.FlushAsync();
                        writer.Dispose();
                    }

                    var filePath = Path.Combine(_logDirectory, $"app-{entryDate}.log");
                    writer = new StreamWriter(new FileStream(filePath, FileMode.Append, FileAccess.Write, FileShare.Read))
                    {
                        AutoFlush = false
                    };
                    currentDate = entryDate;
                }

                var sequence = Interlocked.Increment(ref _writtenSequence);
                await writer!.WriteLineAsync($"{sequence:D12} | {entry.Line}");
                await writer.FlushAsync();
            }
        }
        finally
        {
            if (writer is not null)
            {
                await writer.FlushAsync();
                writer.Dispose();
            }
        }
    }
}

public sealed class FileLogger : ILogger
{
    private readonly string _categoryName;
    private readonly LogLevel _minLevel;
    private readonly Func<QueuedLogEntry, bool> _tryQueueLog;

    public FileLogger(string categoryName, LogLevel minLevel, Func<QueuedLogEntry, bool> tryQueueLog)
    {
        _categoryName = categoryName;
        _minLevel = minLevel;
        _tryQueueLog = tryQueueLog;
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull
    {
        return null;
    }

    public bool IsEnabled(LogLevel logLevel)
    {
        return logLevel >= _minLevel && logLevel != LogLevel.None;
    }

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel))
        {
            return;
        }

        var message = formatter(state, exception);
        if (string.IsNullOrWhiteSpace(message) && exception is null)
        {
            return;
        }

        var timestampUtc = DateTime.UtcNow;
        var exceptionText = exception is null ? string.Empty : $" | Exception: {exception}";
        var line = $"{timestampUtc:O} [{logLevel}] {_categoryName} | {message}{exceptionText}";
        _tryQueueLog(new QueuedLogEntry(timestampUtc, line));
    }
}

public readonly record struct QueuedLogEntry(DateTime TimestampUtc, string Line);
