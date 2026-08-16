using System;
using Microsoft.Extensions.Logging;

namespace Klydis.Core.Diagnostics;

/// <summary>
/// Minimal <see cref="ILoggerProvider"/> that mirrors every app log message (Trace and up)
/// into the rotating <c>app.log</c> next to <c>llama_native.log</c>.
///
/// Previously the app's ILogger pipeline was console-only: LogWarning/LogError entries from
/// the inference engine, chat engine and services vanished when the app was launched from a
/// shortcut, leaving chat_debug.log (written by hand-picked sites) as the only durable trace.
/// This provider makes ALL ILogger output durable without adding a package dependency.
/// </summary>
public sealed class KlydisLogFileLoggerProvider : ILoggerProvider
{
    /// <inheritdoc />
    public ILogger CreateLogger(string categoryName) => new FileLogger(categoryName);

    /// <inheritdoc />
    public void Dispose()
    {
    }

    private sealed class FileLogger : ILogger
    {
        private readonly string _category;

        public FileLogger(string category)
        {
            _category = category;
        }

        /// <inheritdoc />
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        /// <inheritdoc />
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Trace;

        /// <inheritdoc />
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            try
            {
                var message = formatter(state, exception);
                var line = $"[{DateTime.Now:HH:mm:ss.fff}] [{logLevel,-11}] [{ShortCategory}] {message}";
                if (exception != null)
                {
                    // Full exception chain (ex.ToString includes all inner exceptions).
                    line += Environment.NewLine + exception;
                }
                KlydisLog.AppendBounded(KlydisLog.AppLogPath, line + Environment.NewLine);
            }
            catch
            {
                // Logging must never throw.
            }
        }

        private string ShortCategory
        {
            get
            {
                var idx = _category.LastIndexOf('.');
                return idx >= 0 ? _category.Substring(idx + 1) : _category;
            }
        }
    }
}
