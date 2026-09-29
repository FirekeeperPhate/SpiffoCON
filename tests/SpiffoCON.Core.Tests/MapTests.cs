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
    }
}
