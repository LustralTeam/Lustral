using System.Text;

namespace Lustral.Core.Logging;

/// <summary>
/// Writes <c>latest.log</c> and keeps the logs of previous sessions, up to <c>keep</c> files. Each line is flushed
/// immediately, so that a crash does not lose the lines that explain it.
/// </summary>
public sealed class FileLogSink : ILogSink
{
    private readonly StreamWriter _writer;

    public FileLogSink(string directory, int keep = 10)
    {
        Directory.CreateDirectory(directory);
        Path = System.IO.Path.Combine(directory, "latest.log");
        RotateHistory(directory, keep);

        var stream = new FileStream(Path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        _writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
    }

    public string Path { get; }

    /// <summary>The path to which the previous session's <c>latest.log</c> was moved, if there was one.</summary>
    public string? PreviousLogPath { get; private set; }

    public void Write(in LogEntry entry) => _writer.WriteLine(LogHub.Format(entry));

    public void Dispose() => _writer.Dispose();

    private void RotateHistory(string directory, int keep)
    {
        if (File.Exists(Path))
        {
            var stamp = File.GetLastWriteTime(Path).ToString("yyyy-MM-dd_HH-mm-ss");
            var archived = System.IO.Path.Combine(directory, $"{stamp}.log");
            try
            {
                File.Copy(Path, archived, overwrite: true);
                PreviousLogPath = archived;
            }
            catch (IOException)
            {
                // Another instance may still hold the file; losing one old log is acceptable.
            }
        }

        var history = new DirectoryInfo(directory)
            .GetFiles("????-??-??_??-??-??.log")
            .OrderByDescending(f => f.Name, StringComparer.Ordinal)
            .Skip(keep);
        foreach (var old in history)
        {
            try
            {
                old.Delete();
            }
            catch (IOException)
            {
            }
        }
    }
}
