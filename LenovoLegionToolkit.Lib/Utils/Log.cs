using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;

namespace LenovoLegionToolkit.Lib.Utils;

public class Log
{
    private static Log? _instance;
    public static Log Instance
    {
        get
        {
            _instance ??= new Log();
            return _instance;
        }
    }

    private readonly object _lock = new();
    private readonly string _folderPath;
    private readonly string _logPath;
    private readonly bool _persistToFile;

    public bool IsTraceEnabled { get; set; }

    public string LogPath => _logPath;

    private Log()
    {
        // An isolated QA profile is the explicitly permitted destination for diagnostic output.
        // A plain --read-only or --ui-preview run must not create files in the user's profile.
        _persistToFile = !Folders.IsReadOnlySession || Folders.IsIsolatedProfile;
        _folderPath = _persistToFile ? Path.Combine(Folders.AppData, "log") : string.Empty;
        _logPath = _persistToFile ? Path.Combine(_folderPath, $"log_{DateTime.UtcNow:yyyy_MM_dd_HH_mm_ss}.txt") : string.Empty;

        if (_persistToFile)
            Directory.CreateDirectory(_folderPath);
    }

    public void ErrorReport(string header, Exception ex)
    {
        if (!_persistToFile)
            return;

        try
        {
            var errorReportPath = Path.Combine(_folderPath, $"error_{DateTime.UtcNow:yyyy_MM_dd_HH_mm_ss}.txt");
            Folders.EnsureParentDirectoryExists(errorReportPath);
            File.AppendAllLines(errorReportPath, [header, Serialize(ex)]);
        }
        catch
        {
            // Ignored
        }
    }

    public void Trace(FormattableString message,
        Exception? ex = null,
        [CallerFilePath] string? file = null,
        [CallerLineNumber] int lineNumber = -1,
        [CallerMemberName] string? caller = null)
    {
        if (!_persistToFile || !IsTraceEnabled)
        {
            return;
        }

        LogInternal(_logPath, message, ex, file, lineNumber, caller);
    }

    private void LogInternal(string path,
        FormattableString message,
        Exception? ex,
        string? file,
        int lineNumber,
        string? caller)
    {
        lock (_lock)
        {
            var lines = new List<string>
            {
                $"[{DateTime.Now:dd/MM/yyyy HH:mm:ss.fff}] [Thread Id: {Environment.CurrentManagedThreadId}] [{Path.GetFileName(file)}#{lineNumber}:{caller}] {message}"
            };
            if (ex is not null)
            {
                lines.Add(Serialize(ex));
            }

#if DEBUG
            foreach (var line in lines)
            {
                Debug.WriteLine(line);
            }
#endif

            try
            {
                File.AppendAllLines(path, lines);
            }
            catch (DirectoryNotFoundException)
            {
                try
                {
                    Directory.CreateDirectory(_folderPath);
                    File.AppendAllLines(path, lines);
                }
                catch
                {
                    // Ignored
                }
            }
            catch
            {
                // Ignored
            }
        }
    }

    private static string Serialize(Exception ex) => new StringBuilder()
        .AppendLine("=== Exception ===")
        .AppendLine(ex.ToString())
        .AppendLine()
        .AppendLine("=== Exception demystified ===")
        .AppendLine(ex.ToStringDemystified())
        .ToString();
}
