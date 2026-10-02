using System.IO.Compression;
using System.Text;
using SpiffoCON.Core.Map;

namespace SpiffoCON.Core.Tests;

public sealed class MapTests : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("spiffocon-map").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    const string Xml = """
        <?xml version="1.0" encoding="UTF-8"?>
        <world version="1.0">
         <cell x="2" y="3">
          <feature>
           <geometry type="Polygon">
            <coordinates>
             <point x="10" y="20"/><point x="30" y="20"/><point x="30" y="40"/>
            </coordinates>
            <coordinates>
             <point x="15" y="25"/><point x="20" y="25"/><point x="20" y="30"/>
            </coordinates>
           </geometry>
           <properties><property name="building" value="Medical"/></properties>
          </feature>
          <feature>
           <geometry type="LineString">
            <coordinates><point x="0" y="0"/><point x="100" y="0"/></coordinates>
           </geometry>
           <properties><property name="highway" value="trail"/><property name="width" value="8"/></properties>
          </feature>
          <feature>
           <geometry type="Polygon">
            <coordinates><point x="0" y="0"/><point x="1" y="0"/><point x="1" y="1"/></coordinates>
           </geometry>
           <properties><property name="unknown" value="x"/></properties>
          </feature>
         </cell>
         <cell x="4" y="5"/>
        </world>
        """;

    [Fact]
    public void Features_are_read_in_world_squares()
    {
        var map = new WorldMap();
        map.ReadXml(new MemoryStream(Encoding.UTF8.GetBytes(Xml)));
        Assert.Equal(2, map.Features.Count);
        var building = map.Features[0];
        Assert.Equal((MapLayer.BuildingMedical, false, 2), (building.Layer, building.IsLine, building.Rings.Count));
        // cell 2,3 of 300 squares
        Assert.Equal([610f, 920f, 630f, 920f, 630f, 940f], building.Rings[0]);
        var trail = map.Features[1];
        Assert.Equal((MapLayer.RoadTrail, true, 8f), (trail.Layer, trail.IsLine, trail.Width));
        Assert.Equal((600, 900, 1500, 1800), map.Bounds);
    }

    [Fact]
    public void Place_names_come_from_the_annotations_and_their_translation()
    {
        var map = new WorldMap();
        var lua = """
            symbol = symbolsAPI:addUntranslatedText("MapLabel_Muldraugh", "text-town", 10640, 9600)
            symbol = symbolsAPI:addUntranslatedText("MapLabel_SaltRiver", "text-water-nofade", 12511, 6734)
            symbol = symbolsAPI:addTranslatedText("MapLabel_DixieTrailerPark", "text-place", 11600.5, 8800)
            symbol = symbolsAPI:addUntranslatedText("MapLabel_Muldraugh", "text-town", 10000, 9000)
            """;
        map.ReadAnnotations(lua, WorldMap.ReadLabelTexts("""{ "MapLabel_Muldraugh": "MULDRAUGH", }"""));
        Assert.Equal(
            [new MapLabel("MULDRAUGH", 10640, 9600, true), new MapLabel("Dixie Trailer Park", 11600.5f, 8800, false)],
            map.Labels);
    }

    [Fact]
    public void The_map_files_are_found_from_a_game_folder()
    {
        var mapFolder = Directory.CreateDirectory(Path.Combine(_dir, "game", "media", "maps", MapFiles.VanillaMap)).FullName;
        foreach (var f in MapFiles.Required)
            File.WriteAllText(Path.Combine(mapFolder, f), "x");
        Assert.Null(MapFiles.FromFolder(Path.Combine(_dir, "nothing")));
        var files = MapFiles.FromFolder(Path.Combine(_dir, "game"));
        Assert.Equal(mapFolder, files?.MapFolder);
        Assert.Null(files!.LabelsJson);
        Assert.Equal(mapFolder, MapFiles.FromFolder(mapFolder)?.MapFolder);

        Assert.Equal("/home/container", MapFiles.RemoteRootFromWorkshop("/home/container/steamapps/workshop/content/108600"));
        Assert.Null(MapFiles.RemoteRootFromWorkshop("/somewhere/else"));
    }

    [Fact]
    public async Task The_map_is_copied_from_the_server_once()
    {
        var server = Path.Combine(_dir, "server").Replace('\\', '/');
        var mapFolder = Directory.CreateDirectory(Path.Combine(server, "media", "maps", MapFiles.VanillaMap)).FullName;
        foreach (var f in MapFiles.Required.Append(MapFiles.Pyramid))
            File.WriteAllText(Path.Combine(mapFolder, f), f);
        var translate = Directory.CreateDirectory(Path.Combine(server, "media", "lua", "shared", "Translate", "EN")).FullName;
        File.WriteAllText(Path.Combine(translate, MapFiles.LabelsFile), "{}");

        var counters = new LocalRemoteFileSystem.Counters();
        var local = Path.Combine(_dir, "cache");
        Assert.True(await MapFiles.DownloadAsync(new LocalRemoteFileSystem(counters), server, local, satellite: false, null, default));
        Assert.Equal(4, counters.Downloads);
        Assert.False(File.Exists(Path.Combine(local, MapFiles.Pyramid)));
        var files = MapFiles.FromFolder(local);
        Assert.Equal(Path.Combine(local, MapFiles.LabelsFile), files?.LabelsJson);

        // again, with the satellite view: only that file is new
        Assert.True(await MapFiles.DownloadAsync(new LocalRemoteFileSystem(counters), server, local, satellite: true, null, default));
        Assert.Equal(5, counters.Downloads);
        Assert.True(MapFiles.FromFolder(local)!.HasSatellite);

        Assert.False(await MapFiles.DownloadAsync(new LocalRemoteFileSystem(counters), Path.Combine(_dir, "empty"), local, false, null, default));
    }

    static string Cell(int x, int y, string property, string value) =>
        $"""<cell x="{x}" y="{y}"><feature><geometry type="Polygon"><coordinates><point x="0" y="0"/><point x="9" y="0"/><point x="9" y="9"/></coordinates></geometry><properties><property name="{property}" value="{value}"/></properties></feature></cell>""";

    string MapFolder(string name, string worldmap, string? forest = null)
    {
        var folder = Directory.CreateDirectory(Path.Combine(_dir, name)).FullName;
        File.WriteAllText(Path.Combine(folder, "worldmap.xml"), $"<world>{worldmap}</world>");
        if (forest is not null)
            File.WriteAllText(Path.Combine(folder, "worldmap-forest.xml"), $"<world>{forest}</world>");
        return folder;
    }

    [Fact]
    public void The_first_map_folder_with_data_in_a_cell_draws_it()
    {
        // the mod redraws cell 1,1 and adds 5,5 (only forest there); the base game has 1,1 and 2,2
        var mod = MapFolder("mod", Cell(1, 1, "building", "Medical"), Cell(5, 5, "natural", "forest"));
        var vanilla = MapFolder("vanilla", Cell(1, 1, "building", "Industrial") + Cell(2, 2, "water", "river"),
            Cell(2, 2, "natural", "forest"));
        var map = WorldMap.LoadLayered([new MapSource("Mod", mod, IsMod: true), new MapSource("Muldraugh, KY", vanilla, IsMod: false)]);

        // the base game's forest comes from its images, not its (big) worldmap-forest.xml
        Assert.Equal(
            [(MapLayer.Forest, 5, 5), (MapLayer.Water, 2, 2), (MapLayer.BuildingMedical, 1, 1)],
            map.Features.Select(f => (f.Layer, f.CellX, f.CellY)).Order());
        Assert.Equal([(1, 1), (5, 5)], map.ModCells.Order());
        Assert.Equal((300, 300, 1800, 1800), map.Bounds);
    }

    [Fact]
    public void A_mod_map_is_found_in_its_version_folder_before_common()
    {
        var item = Path.Combine(_dir, "workshop", "123", "mods", "Town");
        foreach (var media in new[] { "42", "common" })
        {
            var folder = Directory.CreateDirectory(Path.Combine(item, media, "media", "maps", "My Town")).FullName;
            File.WriteAllText(Path.Combine(folder, "worldmap.xml"), media);
        }
        var workshop = Path.Combine(_dir, "workshop");
        var found = ModMaps.FindLocal([Path.Combine(_dir, "none"), workshop], [], "My Town");
        Assert.Equal(Path.GetFullPath(Path.Combine(item, "42", "media", "maps", "My Town")), found);
        Assert.Null(ModMaps.FindLocal([workshop], ["999"], "My Town"));
        Assert.Null(ModMaps.FindLocal([workshop], [], "Other Town"));
    }

    [Fact]
    public async Task Mod_maps_are_copied_from_the_server_workshop()
    {
        var workshop = Path.Combine(_dir, "server", "content").Replace('\\', '/');
        var folder = Directory.CreateDirectory(Path.Combine(workshop, "456", "mods", "Big Map", "common", "media", "maps", "Big Town")).FullName;
        File.WriteAllText(Path.Combine(folder, "worldmap.xml"), "<world/>");
        File.WriteAllText(Path.Combine(folder, "worldmap-annotations.lua"), "");
        File.WriteAllText(Path.Combine(folder, "town_1_1.lotheader"), "not needed");
        Directory.CreateDirectory(Path.Combine(workshop, "789", "mods", "Other"));

        var counters = new LocalRemoteFileSystem.Counters();
        var cache = Path.Combine(_dir, "cache");
        var found = await ModMaps.DownloadAsync(new LocalRemoteFileSystem(counters), workshop, ["789", "456"], ["Big Town", "Nowhere"], cache, null, default);
        var local = Assert.Single(found).Value;
        Assert.Equal(ModMaps.CacheFolder(cache, "Big Town"), local);
        Assert.Equal(["worldmap-annotations.lua", "worldmap.xml"], Directory.GetFiles(local).Select(Path.GetFileName).Order());
        Assert.Equal(2, counters.Downloads);
        Assert.Equal(local, ModMaps.FromCache(cache, "Big Town"));
    }

    [Fact]
    public void Map_folders_keep_their_commas()
    {
        var options = SpiffoCON.Core.Commands.ServerOptions.Parse("* Map=Daisy County;Muldraugh, KY\n* Mods=a;b");
        Assert.Equal(["Daisy County", "Muldraugh, KY"], options.Maps);
    }

    [Fact]
    public void Pyramid_tiles_are_read_by_level_and_position()
    {
        var path = Path.Combine(_dir, "forest.pyramid.zip");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            zip.CreateEntry("0/");
            using (var w = new StreamWriter(zip.CreateEntry("pyramid.txt").Open()))
                w.Write("VERSION=1\nbounds=0 0 600 300\nimageSize=600 300\n");
            foreach (var name in new[] { "0/tile0x0.png", "0/tile2x1.png", "1/tile0x0.png" })
                using (var s = zip.CreateEntry(name).Open())
                    s.Write(Encoding.ASCII.GetBytes(name));
        }
        using var pyramid = MapPyramid.Open(path)!;
        Assert.Equal((0, 0, 600, 300, 2), (pyramid.MinX, pyramid.MinY, pyramid.MaxX, pyramid.MaxY, pyramid.Levels));
        Assert.Equal("0/tile2x1.png", Encoding.ASCII.GetString(pyramid.ReadTile(0, 2, 1)!));
        Assert.Null(pyramid.ReadTile(0, 1, 1));
        Assert.Equal(512, MapPyramid.TileSquares(1));

        // a newer copy downloaded while this one is drawn takes its place (MapFiles.DownloadAsync)
        var part = path + ".part";
        File.WriteAllText(part, "newer");
        MapFiles.Replace(part, path);
        Assert.Equal("newer", File.ReadAllText(path));
        Assert.Equal("0/tile2x1.png", Encoding.ASCII.GetString(pyramid.ReadTile(0, 2, 1)!));
    }
}
