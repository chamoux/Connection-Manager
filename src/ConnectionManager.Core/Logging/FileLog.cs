using System.Globalization;
using System.Text;

namespace ConnectionManager.Core.Logging;

public interface ILog
{
    void Info(string message);
    void Warn(string message);
    void Error(string message, Exception? ex = null);
}

/// Appends to one file per day (app-YYYYMMDD.log) and deletes files older than a week. Never throws. Each message is one
/// line: line breaks become spaces and other control characters '?', so text from elsewhere (a program's path, say)
/// can't forge a line. At most maxBytesPerDay a day, so no client can fill the disk: past it, one line says so and the
/// rest of the day is dropped.
public sealed class FileLog : ILog
{
    public const long MaxBytesPerDay = 20 * 1024 * 1024;

    private static readonly TimeSpan Keep = TimeSpan.FromDays(7);

    private readonly string _folder;
    private readonly TimeProvider _time;
    private readonly long _maxBytesPerDay;
    private readonly object _gate = new();
    private DateOnly _prunedOn;
    private DateOnly _day;  // the day _written counts
    private long _written;  // bytes in that day's file
    private bool _full;

    public FileLog(string folder, TimeProvider? time = null, long maxBytesPerDay = MaxBytesPerDay)
    {
        _folder = folder;
        _time = time ?? TimeProvider.System;
        _maxBytesPerDay = maxBytesPerDay;
        try
        {
            Directory.CreateDirectory(folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Logging must never stop the app; each write tries to create the folder again.
        }
        DeleteOldFiles();
    }

    public void Info(string message) => Write("INFO", message);
    public void Warn(string message) => Write("WARN", message);
    public void Error(string message, Exception? ex = null) => Write("ERROR", ex is null ? message : $"{message}: {ex}");

    private void Write(string level, string message)
    {
        var now = _time.GetLocalNow();
        var today = DateOnly.FromDateTime(now.DateTime);
        var line = Line(now, level, OneLine(message));
        var file = Path.Combine(_folder, string.Create(CultureInfo.InvariantCulture, $"app-{now:yyyyMMdd}.log"));
        lock (_gate)
        {
            if (today != _prunedOn) DeleteOldFiles(); // an app left running for weeks still prunes
            if (today != _day) (_day, _written, _full) = (today, SizeOf(file), false); // an earlier run's lines count too
            if (_full) return;
            if (_written + Encoding.UTF8.GetByteCount(line) > _maxBytesPerDay)
            {
                _full = true;
                line = Line(now, "WARN", "Log limit reached for today; nothing more is logged until tomorrow.");
            }
            try
            {
                Append(file, line);
                _written += Encoding.UTF8.GetByteCount(line);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Logging must never take the app down.
            }
        }
    }

    private static string Line(DateTimeOffset now, string level, string message) =>
        string.Create(CultureInfo.InvariantCulture, $"{now:yyyy-MM-dd HH:mm:ss} {level} {message}{Environment.NewLine}");

    private static string OneLine(string message)
    {
        var text = message.ReplaceLineEndings(" ");
        return string.Create(text.Length, text, (chars, source) =>
        {
            for (var i = 0; i < source.Length; i++) chars[i] = char.IsControl(source[i]) ? '?' : source[i];
        });
    }

    private static long SizeOf(string file)
    {
        try
        {
            return new FileInfo(file).Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0; // missing: nothing written yet
        }
    }

    private void Append(string file, string line)
    {
        try
        {
            File.AppendAllText(file, line);
        }
        catch (DirectoryNotFoundException)
        {
            Directory.CreateDirectory(_folder); // the folder was deleted while the app was running
            File.AppendAllText(file, line);
        }
    }

    private void DeleteOldFiles()
    {
        _prunedOn = DateOnly.FromDateTime(_time.GetLocalNow().DateTime);
        var cutoff = (_time.GetUtcNow() - Keep).UtcDateTime;
        try
        {
            foreach (var file in Directory.EnumerateFiles(_folder, "app-*.log"))
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(file) < cutoff) File.Delete(file);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // A locked old log is deleted next time.
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The folder is missing or unreadable; try again on the next date change.
        }
    }
}
