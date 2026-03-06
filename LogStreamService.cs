using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using BepInEx.Logging;

namespace ZSlayerHeadlessTelemetry;

public class LogStreamService : ILogListener
{
    private readonly ConcurrentQueue<ConsoleEntryPayload> _buffer = new();
    private const int MaxBuffer = 200;

    public void LogEvent(object sender, LogEventArgs eventArgs)
    {
        var entry = new ConsoleEntryPayload
        {
            Level = NormalizeLevel(eventArgs.Level),
            Source = eventArgs.Source?.SourceName ?? "",
            Message = eventArgs.Data?.ToString() ?? ""
        };
        _buffer.Enqueue(entry);
        while (_buffer.Count > MaxBuffer) _buffer.TryDequeue(out _);
    }

    public List<ConsoleEntryPayload> DrainBuffer()
    {
        var entries = new List<ConsoleEntryPayload>();
        while (_buffer.TryDequeue(out var e)) entries.Add(e);
        return entries;
    }

    private static string NormalizeLevel(LogLevel level)
    {
        if ((level & LogLevel.Fatal) != 0) return "error";
        if ((level & LogLevel.Error) != 0) return "error";
        if ((level & LogLevel.Warning) != 0) return "warning";
        if ((level & LogLevel.Message) != 0) return "info";
        if ((level & LogLevel.Info) != 0) return "info";
        if ((level & LogLevel.Debug) != 0) return "debug";
        return "info";
    }

    public void Dispose() { }
}

public class ConsoleEntryPayload
{
    public string Level = "info";
    public string Source = "";
    public string Message = "";
}
