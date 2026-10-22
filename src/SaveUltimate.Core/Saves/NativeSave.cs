using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]

namespace SaveUltimate.Core.Saves;

public static class NativeSave
{
    public static JsonElement ValidateFile(string path)
    {
        int code = parc_validate_file(NativePath(path), out var output, out var length);
        return Result(code, output, length);
    }

    public static JsonElement ValidateBlob(byte[] blob)
    {
        int code = parc_validate_blob(blob, checked((uint)blob.Length), out var output, out var length);
        return Result(code, output, length);
    }

    public static JsonElement Write(string source, byte[] blob, string destination)
    {
        destination = Path.GetFullPath(destination);
        string directory = Path.GetDirectoryName(destination)!;
        string temporary = Path.Combine(directory, $".saveultimate-{Guid.NewGuid():N}.save");
        try
        {
            int code = parc_write_validated_save(NativePath(source), blob, checked((uint)blob.Length),
                NativePath(temporary), out var output, out var length);
            var result = Result(code, output, length);
            File.Move(temporary, destination);
            return result;
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static byte[] NativePath(string path)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var encoding = Encoding.GetEncoding(checked((int)GetACP()), EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
        return encoding.GetBytes(Path.GetFullPath(path) + "\0");
    }

    private static JsonElement Result(int code, IntPtr pointer, uint length)
    {
        if (pointer == IntPtr.Zero) throw new InvalidDataException($"Native backend returned no result ({code}).");
        try
        {
            byte[] bytes = new byte[checked((int)length)];
            Marshal.Copy(pointer, bytes, 0, bytes.Length);
            using var json = JsonDocument.Parse(bytes);
            var result = json.RootElement;
            if (code != 0 || !result.TryGetProperty("ok", out var ok) || !ok.GetBoolean())
                throw new InvalidDataException(result.TryGetProperty("error", out var error) ? error.ToString() : result.ToString());
            return result.Clone();
        }
        finally { parc_free(pointer); }
    }

    [DllImport("kernel32.dll")] private static extern uint GetACP();
    [DllImport("parc_validation.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int parc_validate_file(byte[] path, out IntPtr output, out uint length);
    [DllImport("parc_validation.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int parc_validate_blob(byte[] blob, uint size, out IntPtr output, out uint length);
    [DllImport("parc_validation.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int parc_write_validated_save(byte[] source, byte[] blob, uint size, byte[] outputPath, out IntPtr output, out uint length);
    [DllImport("parc_validation.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern void parc_free(IntPtr pointer);
}
