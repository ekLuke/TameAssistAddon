using System;
using System.IO;
using System.Text;
using BepInEx;
using BepInEx.Logging;

namespace TheLair.TameAssistAddon.Game;

/// <summary>
/// A private, UNREGISTERED BepInEx ManualLogSource. Its Info/Debug/Warning
/// events are written exclusively to config/TameAssistAddon/TameAssistAddon.log.
/// Only Error/Fatal events are forwarded to the ordinary plugin console log.
/// This does not change the logging behavior of unrelated Valheim mods.
/// </summary>
internal sealed class PrivateFileLog : IDisposable
{
    private const long MaxBytes = 10L * 1024 * 1024;
    private readonly object _gate = new object();
    private readonly ManualLogSource _console;
    private readonly StreamWriter _file;
    private bool _writeFailed;
    private bool _disposed;

    internal ManualLogSource Source { get; }
    internal string FilePath { get; }

    internal PrivateFileLog(ManualLogSource console)
    {
        _console = console;
        string folder = Path.Combine(Paths.ConfigPath, "TameAssistAddon");
        Directory.CreateDirectory(folder);
        FilePath = Path.Combine(folder, "TameAssistAddon.log");

        // Retain one previous file on rotation, to avoid unbounded size on
        // worlds with many animals and 60-second diagnostic sweeps.
        if (File.Exists(FilePath) && new FileInfo(FilePath).Length >= MaxBytes)
        {
            string previous = FilePath + ".old";
            if (File.Exists(previous)) File.Delete(previous);
            File.Move(FilePath, previous);
        }

        _file = new StreamWriter(new FileStream(FilePath, FileMode.Append,
            FileAccess.Write, FileShare.ReadWrite, 4096, FileOptions.WriteThrough),
            new UTF8Encoding(false))
        {
            AutoFlush = true
        };
        Source = new ManualLogSource("TameAssistAddon.File");
        // Crucially, DO NOT register this source in BepInEx.Logger.Sources:
        // it should not feed ConsoleLogListener or the ordinary BepInEx log.
        Source.LogEvent += OnLog;
        _file.WriteLine("===== TameAssistAddon session " +
            DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss") + " UTC =====");
    }

    private void OnLog(object sender, LogEventArgs args)
    {
        string message = args.Data?.ToString() ?? "(null)";
        lock (_gate)
        {
            if (!_disposed && !_writeFailed)
            {
                try
                {
                    _file.WriteLine(DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.fff") +
                        " UTC [" + args.Level + "] " + message);
                }
                catch (Exception ex)
                {
                    _writeFailed = true;
                    _console.LogError("TameAssistAddon file logging failed: " + ex);
                }
            }
        }

        if ((args.Level & (LogLevel.Error | LogLevel.Fatal)) != 0)
            _console.LogError(message);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            Source.LogEvent -= OnLog;
            _file.Dispose();
        }
    }
}
