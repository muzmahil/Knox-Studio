// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 Egor Khindikaynen (Nota). See LICENSES/ for license terms.
//
// ILogSink implementation: bounded in-memory ring buffer + a daily rolling file
// under NotaPaths.DataDir/logs/. Same data directory as the other services
// (see SettingsService). Writes are best-effort — logging never throws into
// the caller.

using System.Text;
using Knox.Application;

namespace Knox.Infrastructure;

public sealed class LogSink : ILogSink
{
    private const int Capacity = 2000;

    private readonly object _gate = new();
    private readonly Queue<string> _ring = new(Capacity);
    private readonly string _dir;

    public LogSink()
    {
        _dir = KnoxPaths.SubDir("logs");

        Write(LogLevel.Info, $"--- session start · Knox Studio · {Environment.OSVersion} ---");
    }

    public void Write(LogLevel level, string message)
    {
        var line = $"{DateTime.UtcNow:yyyy-MM-ddTHH:mm:ss.fffZ} {Tag(level)} {message}";

        lock (_gate)
        {
            if (_ring.Count >= Capacity) _ring.Dequeue();
            _ring.Enqueue(line);

            try
            {
                var file = Path.Combine(_dir, $"knox-{DateTime.UtcNow:yyyyMMdd}.log");
                File.AppendAllText(file, line + Environment.NewLine, Encoding.UTF8);
            }
            catch { /* disk full / permissions — keep the in-memory copy */ }
        }
    }

    public IReadOnlyList<string> Recent()
    {
        lock (_gate) return _ring.ToArray();
    }

    public string Snapshot()
    {
        lock (_gate) return string.Join(Environment.NewLine, _ring);
    }

    private static string Tag(LogLevel level) => level switch
    {
        LogLevel.Warn => "WARN",
        LogLevel.Error => "ERROR",
        _ => "INFO",
    };
}
