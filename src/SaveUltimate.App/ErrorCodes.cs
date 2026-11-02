using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace SaveUltimate.App;

// Stable codes users can quote in reports; the full exception goes to error.log beside the executable.
public static class ErrorCodes
{
    private static readonly string[] nativeLibraries = ["parc_validation.dll", "parc_reader.dll", "parc_index.dll", "Magick.Native-Q8-x64.dll"];
    public static string LogPath { get; set; } = Path.Combine(AppContext.BaseDirectory, "error.log");

    public static string Describe(Exception error)
    {
        var (code, message) = Classify(error);
        Log(code, error);
        return $"[{code}] {message}";
    }

    public static (string Code, string Message) Classify(Exception error)
    {
        string detail = error.Message;
        (string, string) Coded(string code) => (code, Tr.Format("error." + code, ("detail", detail), ("type", error.GetType().Name)));
        return error switch
        {
            DllNotFoundException or BadImageFormatException or EntryPointNotFoundException => DiagnoseNative(AppContext.BaseDirectory),
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

    // The runtime's DllNotFoundException names neither the file nor the reason, so load each library again to find both.
    public static (string Code, string Message) DiagnoseNative(string directory)
    {
        foreach (string name in nativeLibraries)
        {
            string path = Path.Combine(directory, name);
            if (!File.Exists(path)) return ("SU-101", Tr.Format("error.SU-101", ("file", name)));
            IntPtr handle = LoadLibraryExW(path, IntPtr.Zero, LoadWithAlteredSearchPath);
            if (handle != IntPtr.Zero) { FreeLibrary(handle); continue; }
            int win32 = Marshal.GetLastWin32Error();
            // 5 access denied; 225/226 blocked as a threat; 577/1260/4551 code integrity or policy; 193/216 not a valid x64 image.
            string code = win32 switch { 5 => "SU-102", 225 or 226 => "SU-103", 577 or 1260 or 4551 => "SU-104", 193 or 216 => "SU-105", _ => "SU-109" };
            return (code, Tr.Format("error." + code, ("file", name), ("win32", $"{win32}: {new Win32Exception(win32).Message}")));
        }
        return ("SU-109", Tr.Format("error.SU-109", ("file", "?"), ("win32", "-")));
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

    private const uint LoadWithAlteredSearchPath = 0x8;
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadLibraryExW(string path, IntPtr file, uint flags);
    [DllImport("kernel32.dll")]
    private static extern bool FreeLibrary(IntPtr module);
}
