using System.CommandLine;
using System.Security.Cryptography;
using System.Text.Json;
using SaveUltimate.Core.Archives;
using SaveUltimate.Core.Images;
using SaveUltimate.Core.Saves;

var root = new RootCommand("SaveUltimate C# tools — local files only");
var inspect = new Command("inspect", "Verify a SAVE and print its block inventory");
var savePath = new Argument<string>("save");
inspect.Arguments.Add(savePath);
inspect.SetAction(result =>
{
    string path = result.GetValue(savePath)!;
    var native = NativeSave.ValidateFile(path);
    var save = SaveContainer.Decode(File.ReadAllBytes(path));
    var layout = ParcLayout.Parse(save.Blob);
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        save.Version, Bytes = save.Blob.Length, Sha256 = Convert.ToHexString(SHA256.HashData(save.Blob)),
        Native = native, Blocks = layout.Blocks
    }, new JsonSerializerOptions { WriteIndented = true }));
});
root.Subcommands.Add(inspect);
var extract = new Command("extract", "Extract one exact virtual path from a PAZ group");
var group = new Argument<string>("group");
var virtualPath = new Argument<string>("path");
var output = new Argument<string>("output");
extract.Arguments.Add(group); extract.Arguments.Add(virtualPath); extract.Arguments.Add(output);
extract.SetAction(result =>
{
    var bytes = new GameArchive(result.GetValue(group)!).Extract(result.GetValue(virtualPath)!);
    using var stream = new FileStream(result.GetValue(output)!, FileMode.CreateNew);
    stream.Write(bytes);
    Console.WriteLine($"{bytes.Length} bytes; SHA256 {Convert.ToHexString(SHA256.HashData(bytes))}");
});
root.Subcommands.Add(extract);
var texture = new Command("texture", "Convert DDS to PNG or lossless WebP (maximum 256 × 256)");
var inputImage = new Argument<string>("dds");
var outputImage = new Argument<string>("output");
texture.Arguments.Add(inputImage); texture.Arguments.Add(outputImage);
texture.SetAction(result =>
{
    string target = result.GetValue(outputImage)!;
    string extension = Path.GetExtension(target).ToLowerInvariant();
    if (extension is not ".webp" and not ".png") throw new ArgumentException("Output must be .png or .webp");
    var bytes = TextureImage.Convert(File.ReadAllBytes(result.GetValue(inputImage)!), 256, 256, extension == ".webp");
    using var stream = new FileStream(target, FileMode.CreateNew);
    stream.Write(bytes);
});
root.Subcommands.Add(texture);
return root.Parse(args).Invoke();
