using System.Text.Json;

namespace SaveUltimate.Core.Saves;

// Resources shipped inside this assembly. One options object: System.Text.Json caches type metadata per instance.
internal static class Embedded
{
    internal static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };
    internal static Stream Open(string name) => typeof(Embedded).Assembly.GetManifestResourceStream(name) ?? throw new InvalidDataException($"Missing resource {name}.");
    internal static T Load<T>(string name)
    {
        using var stream = Open(name);
        return JsonSerializer.Deserialize<T>(stream, Options)!;
    }
}
