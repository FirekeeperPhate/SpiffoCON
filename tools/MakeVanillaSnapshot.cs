// Regenerates src/SpiffoCON.Core/Data/vanilla-catalog.json.gz from a Project Zomboid install.
// The free dedicated server is enough (SteamCMD: +login anonymous +app_update 380870):
//   dotnet run tools/MakeVanillaSnapshot.cs -- "<pz or dedicated server folder>"
#:project ../src/SpiffoCON.Core/SpiffoCON.Core.csproj
#:property PublishAot=false

using System.IO.Compression;
using SpiffoCON.Core.Catalog;

if (args.Length < 1 || !Directory.Exists(Path.Combine(args[0], "media", "scripts")))
{
    Console.Error.WriteLine("Usage: MakeVanillaSnapshot <folder containing media/scripts>");
    return 1;
}

var builder = new CatalogBuilder();
builder.AddContentRoot(args[0], CatalogSource.Vanilla);
var entries = builder.Build();
var output = Path.GetFullPath(Path.Combine(AppContext.GetData("EntryPointFileDirectoryPath") as string ?? ".", "..", "src", "SpiffoCON.Core", "Data", "vanilla-catalog.json.gz"));
Directory.CreateDirectory(Path.GetDirectoryName(output)!);
using (var file = File.Create(output))
using (var gzip = new GZipStream(file, CompressionLevel.SmallestSize))
using (var writer = new StreamWriter(gzip))
    writer.Write(builder.ExportSnapshot());

Console.WriteLine($"{builder.ScriptFiles} script files -> {entries.Count(e => e.Kind == CatalogKind.Item)} items, {entries.Count(e => e.Kind == CatalogKind.Vehicle)} vehicles");
Console.WriteLine($"{output} ({new FileInfo(output).Length / 1024} KB)");
return 0;
