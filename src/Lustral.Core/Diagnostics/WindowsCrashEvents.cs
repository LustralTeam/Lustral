using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace Lustral.Core.Diagnostics;

/// <summary>
/// The records of a crashed process in the Windows Application event log: "Application Error" (the faulting module,
/// exception code and offset, for any crash) and ".NET Runtime" (the managed stack, if the .NET runtime terminated the
/// process, as with a native crash within a Harmony patch on IL2CPP, which Unity's crash handler does not see).
/// </summary>
/// <remarks>
/// Read with <c>wevtutil</c> as XML, because event text is localized but XML fields are not. Parsed with regular
/// expressions, as Unity's Mono may not include System.Xml.Linq. Only read after a session has ended abruptly.
/// </remarks>
public static class WindowsCrashEvents
{
    /// <param name="Fault">"module version faulted with exception code at offset", and the module's path.</param>
    /// <param name="ManagedCrash">The .NET runtime's report: description and stack.</param>
    public sealed record Records(string? Fault, string? FaultingModulePath, string? ManagedCrash);

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    public static Records Find(int processId, string executable, DateTime sinceUtc)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return new Records(null, null, null);
        var since = sinceUtc.AddSeconds(-5).ToString("yyyy-MM-ddTHH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
        var query = $"*[System[(Provider[@Name='Application Error'] or Provider[@Name='.NET Runtime']) and TimeCreated[@SystemTime>='{since}']]]";
        var xml = Run("wevtutil", $"qe Application \"/q:{query}\" /f:xml /rd:true /c:50");
        return xml is null ? new Records(null, null, null) : Parse(xml, processId, executable);
    }

    internal static Records Parse(string xml, int processId, string executable)
    {
        string? fault = null, modulePath = null, managed = null;
        DateTime? faultTime = null;
        var managedCandidates = new List<(DateTime Time, string Text)>();
        foreach (Match match in Regex.Matches(xml, "<Event[ >].*?</Event>", RegexOptions.Singleline))
        {
            var e = match.Value;
            var provider = Regex.Match(e, "<Provider Name='([^']*)'").Groups[1].Value;
            var id = Regex.Match(e, "<EventID[^>]*>(\\d+)</EventID>").Groups[1].Value;
            DateTime.TryParse(Regex.Match(e, "<TimeCreated SystemTime='([^']*)'").Groups[1].Value, CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var time);

            if (provider == "Application Error" && id == "1000")
            {
                var data = NamedData(e);
                if (!data.TryGetValue("ProcessId", out var pid) || !TryParseHex(pid, out var parsed) || parsed != processId)
                    continue;
                if (data.TryGetValue("AppName", out var app) && !string.Equals(app, executable, StringComparison.OrdinalIgnoreCase))
                    continue;
                data.TryGetValue("ModuleName", out var module);
                data.TryGetValue("ModuleVersion", out var version);
                data.TryGetValue("ExceptionCode", out var code);
                data.TryGetValue("FaultingOffset", out var offset);
                data.TryGetValue("ModulePath", out modulePath);
                fault = $"{module} {version} faulted with exception 0x{code} at offset 0x{offset}";
                faultTime = time;
            }
            else if (provider == ".NET Runtime" && id == "1026")
            {
                var text = Decode(Regex.Match(e, "<Data>(.*?)</Data>", RegexOptions.Singleline).Groups[1].Value);
                if (text.StartsWith($"Application: {executable}", StringComparison.OrdinalIgnoreCase))
                    managedCandidates.Add((time, text.Trim()));
            }
        }

        // The .NET runtime logs its report just before Windows logs the fault; without a fault record, the newest is used.
        managed = managedCandidates
            .Where(c => faultTime is null || (c.Time <= faultTime.Value.AddSeconds(5) && c.Time >= faultTime.Value.AddMinutes(-1)))
            .OrderByDescending(c => c.Time)
            .Select(c => c.Text)
            .FirstOrDefault();
        return new Records(fault, modulePath, managed);
    }

    private static Dictionary<string, string> NamedData(string e)
    {
        var data = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match match in Regex.Matches(e, "<Data Name='([^']*)'>(.*?)</Data>", RegexOptions.Singleline))
            data[match.Groups[1].Value] = Decode(match.Groups[2].Value).Trim();
        return data;
    }

    private static bool TryParseHex(string text, out long value) =>
        long.TryParse(text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? text.Substring(2) : text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);

    private static string Decode(string text) =>
        Regex.Replace(text, "&(lt|gt|amp|quot|apos|#x[0-9A-Fa-f]+|#\\d+);", m => m.Groups[1].Value switch
        {
            "lt" => "<",
            "gt" => ">",
            "amp" => "&",
            "quot" => "\"",
            "apos" => "'",
            var n when n.StartsWith("#x", StringComparison.Ordinal) => ((char)int.Parse(n.Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture)).ToString(),
            var n => ((char)int.Parse(n.Substring(1), CultureInfo.InvariantCulture)).ToString(),
        });

    private static string? Run(string program, string arguments)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(program, arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            if (process is null)
                return null;
            var output = process.StandardOutput.ReadToEndAsync();
            process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit((int)Timeout.TotalMilliseconds))
            {
                process.Kill();
                return null;
            }
            return process.ExitCode == 0 ? output.Result : null;
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
        {
            return null;
        }
    }
}
