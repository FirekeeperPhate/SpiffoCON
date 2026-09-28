using Microsoft.Data.Sqlite;
using SpiffoCON.Core.Files;

namespace SpiffoCON.Core.Tests;

public sealed class ServerDatabaseTests : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("spiffocon-db").FullName;

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, recursive: true);
    }

    static void Exec(string path, params string[] sql)
    {
        using var db = new SqliteConnection($"Data Source={path};Pooling=False");
        db.Open();
        foreach (var s in sql)
        {
            using var cmd = db.CreateCommand();
            cmd.CommandText = s;
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>A Zomboid folder with the table layout of a B42 dedicated server.</summary>
    string MakeZomboid()
    {
        var zomboid = Path.Combine(_dir, "Zomboid");
        Directory.CreateDirectory(Path.Combine(zomboid, "db"));
        Directory.CreateDirectory(Path.Combine(zomboid, "Saves", "Multiplayer", "pzserver"));
        Exec(Path.Combine(zomboid, "db", "pzserver.db"),
            "CREATE TABLE [whitelist] ([id] INTEGER PRIMARY KEY AUTOINCREMENT NOT NULL,[world] TEXT DEFAULT '' NULL,[username] TEXT NULL, [password] TEXT NULL, [lastConnection] TEXT NULL, [role] INTEGER NOT NULL, [authType] INTEGER NULL DEFAULT 1, [googleKey] TEXT NULL, [steamid] TEXT NULL, [ownerid] TEXT NULL, [displayName] TEXT NULL)",
            "CREATE TABLE [role] ([id] INTEGER PRIMARY KEY AUTOINCREMENT NOT NULL, [name] TEXT NOT NULL,[description] TEXT NULL, [colorR] REAL NOT NULL, [colorG] REAL NOT NULL, [colorB] REAL NOT NULL, [readonly] BOOLEAN NULL DEFAULT false, [position] INTEGER NOT NULL DEFAULT -1)",
            "CREATE TABLE [bannedip] ([ip] TEXT NOT NULL,[username] TEXT NULL, [reason] TEXT NULL)",
            "CREATE TABLE [bannedid] ([steamid] TEXT NOT NULL, [reason] TEXT NULL)",
            "CREATE TABLE [userlog] ([id] INTEGER PRIMARY KEY AUTOINCREMENT NOT NULL,[username] TEXT NULL,[type] TEXT NULL, [text] TEXT NULL, [issuedBy] TEXT NULL, [amount] INTEGER NULL, [lastUpdate] TEXT NULL)",
            "INSERT INTO role (id, name, colorR, colorG, colorB, position) VALUES (1,'banned',0,0,0,1000),(2,'user',0,0,0,2000),(7,'admin',0,0,0,7000)",
            "INSERT INTO whitelist (username, password, role, steamid, lastConnection, displayName) VALUES ('rj','$2a$12$secret',7,'7656119','2026-09-27 21:10:00','RJ'),('griefer','$2a$12$x',1,NULL,NULL,NULL),('zed','',2,NULL,NULL,NULL)",
            "INSERT INTO userlog (username, type, text, issuedBy, amount, lastUpdate) VALUES ('griefer','Banned','burned the base','rj',1,'2026-09-28 12:27:55')",
            "INSERT INTO bannedip (ip, username, reason) VALUES ('10.0.0.9','griefer','burned the base')");
        Exec(Path.Combine(zomboid, "Saves", "Multiplayer", "pzserver", "players.db"),
            "CREATE TABLE networkPlayers (id INTEGER PRIMARY KEY NOT NULL,world TEXT,username TEXT,playerIndex INTEGER,name STRING,steamid STRING,x FLOAT,y FLOAT,z FLOAT,worldversion INTEGER,data BLOB,isDead BOOLEAN)",
            "INSERT INTO networkPlayers (world, username, playerIndex, name, x, y, z, isDead) VALUES ('pzserver','rj',0,'Rick Grimes',10612.4,9433.9,0,0),('pzserver','zed',0,'Old Zed',11000,9000,1,1)");
        return zomboid;
    }

    [Fact]
    public async Task Reads_accounts_characters_bans_and_history()
    {
        var source = new LocalServerDbSource(MakeZomboid());
        Assert.Equal(["pzserver"], await source.ListServersAsync());
        var (serverDb, playersDb) = await source.CopyAsync("pzserver", Path.Combine(_dir, "copy"));
        var snap = ServerDatabase.Read(serverDb, playersDb);

        Assert.Equal(["griefer", "rj", "zed"], snap.Accounts.Select(a => a.Username));
        var rj = snap.Accounts.Single(a => a.Username == "rj");
        Assert.Equal(("admin", "7656119", "RJ", false), (rj.Role, rj.SteamId, rj.DisplayName, rj.Banned));
        Assert.Equal(new DateTime(2026, 9, 27, 21, 10, 0), rj.LastConnection);
        Assert.True(snap.Accounts.Single(a => a.Username == "griefer").Banned);

        var rick = snap.Characters.Single(c => c.Username == "rj");
        Assert.Equal(("Rick Grimes", 10612, 9433, 0, false), (rick.Name, rick.X, rick.Y, rick.Z, rick.Dead));
        Assert.True(snap.Characters.Single(c => c.Username == "zed").Dead);

        var ban = Assert.Single(snap.UserLog);
        Assert.Equal(("Banned", "burned the base", "rj"), (ban.Type, ban.Text, ban.IssuedBy));
        Assert.Equal("10.0.0.9", Assert.Single(snap.BannedIps).Ip);
        Assert.Empty(snap.BannedSteamIds);
    }

    [Fact]
    public async Task Works_without_players_db()
    {
        var zomboid = MakeZomboid();
        File.Delete(Path.Combine(zomboid, "Saves", "Multiplayer", "pzserver", "players.db"));
        var (serverDb, playersDb) = await new LocalServerDbSource(zomboid).CopyAsync("pzserver", Path.Combine(_dir, "copy2"));
        Assert.Null(playersDb);
        Assert.Empty(ServerDatabase.Read(serverDb, playersDb).Characters);
    }
}
