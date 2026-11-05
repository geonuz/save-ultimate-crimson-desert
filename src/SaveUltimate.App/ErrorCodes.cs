using System.Diagnostics;
using System.IO;

namespace SaveUltimate.App;

// Stable codes users can quote in reports; the full exception goes to error.log beside the executable.
public static class ErrorCodes
{
    public static string LogPath { get; set; } = Path.Combine(AppContext.BaseDirectory, "error.log");

    public static string Describe(Exception error)
    {
        var (code, message) = Classify(error);
        Log(code, error);
        return $"[{code}] {message}";
    }

    public static (string Code, string Message) Classify(Exception error)
    {
        // Core messages are English; a locale entry keyed by the message translates it, others stay as they are.
        string detail = Tr.Text(error.Message);
        (string, string) Coded(string code) => (code, Tr.Format("error." + code, ("detail", detail), ("type", error.GetType().Name)));
        return error switch
        {
            FileNotFoundException or DirectoryNotFoundException => Coded("SU-201"),
            UnauthorizedAccessException => Coded("SU-202"),
            PathTooLongException => Coded("SU-205"),
            // Win32 codes in the HRESULT: 32/33 sharing or lock violation, 39/112 disk full.
            IOException when (error.HResult & 0xFFFF) is 32 or 33 => Coded("SU-203"),
            IOException when (error.HResult & 0xFFFF) is 39 or 112 => Coded("SU-204"),
            IOException => Coded("SU-209"),
            InvalidDataException when Frames(error).Any(ns => ns is "SaveUltimate.Core.Catalogs" or "SaveUltimate.Core.Archives") => Coded("SU-302"),
            InvalidDataException => Coded("SU-301"),
            InvalidOperationException when error.TargetSite?.DeclaringType?.Namespace?.StartsWith("SaveUltimate", StringComparison.Ordinal) == true => Coded("SU-303"),
            OutOfMemoryException => Coded("SU-401"),
            _ => Coded("SU-901"),
        };
    }

    private static IEnumerable<string?> Frames(Exception error) =>
        (new StackTrace(error, false).GetFrames() ?? []).Select(f => f.GetMethod()?.DeclaringType?.Namespace);

    private static void Log(string code, Exception error)
    {
        try
        {
            var file = new FileInfo(LogPath);
            if (file.Exists && file.Length > 1 << 20) File.Move(LogPath, LogPath + ".old", true);
            string version = typeof(ErrorCodes).Assembly.GetName().Version!.ToString(3);
            File.AppendAllText(LogPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} v{version} {Environment.OSVersion.VersionString} {code}{Environment.NewLine}{error}{Environment.NewLine}{Environment.NewLine}");
        }
        // The log is a diagnostic aid; failing to write it must not replace the error being reported.
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
