using System.Globalization;
using Microsoft.Data.Sqlite;

namespace SpiffoCON.Core.Files;

/// <summary>An account in Zomboid/db/&lt;server&gt;.db (whitelist table; the password hash is never read).</summary>
public sealed record AccountInfo(string Username, string? DisplayName, string Role, DateTime? LastConnection, string? SteamId, string? OwnerId)
{
    public bool Banned => Role.Equals("banned", StringComparison.OrdinalIgnoreCase);
}

/// <summary>A character in Saves/Multiplayer/&lt;server&gt;/players.db (networkPlayers table).</summary>
public sealed record CharacterInfo(string Username, string? Name, int X, int Y, int Z, bool Dead, int PlayerIndex);

/// <summary>A kick, ban or warning in the userlog table.</summary>
public sealed record UserLogEntry(string Username, string Type, string? Text, string? IssuedBy, int Amount, DateTime? When);

public sealed record BannedIp(string Ip, string? Username, string? Reason);

public sealed record BannedSteamId(string SteamId, string? Reason);

public sealed record ServerDatabaseSnapshot(
    IReadOnlyList<AccountInfo> Accounts,
    IReadOnlyList<CharacterInfo> Characters,
    IReadOnlyList<UserLogEntry> UserLog,
    IReadOnlyList<BannedIp> BannedIps,
    IReadOnlyList<BannedSteamId> BannedSteamIds);

/// <summary>
/// Reads a copy of the server's SQLite databases (schema of the B42 dedicated server):
/// db/&lt;server&gt;.db has accounts, roles, bans and the kick/ban history; players.db has the
/// characters with their last saved position.
/// </summary>
public static class ServerDatabase
{
    public static ServerDatabaseSnapshot Read(string serverDb, string? playersDb)
    {
        var accounts = new List<AccountInfo>();
        var log = new List<UserLogEntry>();
        var ips = new List<BannedIp>();
        var ids = new List<BannedSteamId>();
        using (var db = Open(serverDb))
        {
            // roles are rows in B42 (banned, user, priority, observer, gm, moderator, admin)
            foreach (var r in Query(db, "SELECT w.username, w.displayName, COALESCE(r.name, CAST(w.role AS TEXT)), w.lastConnection, w.steamid, w.ownerid " +
                                        "FROM whitelist w LEFT JOIN role r ON r.id = w.role WHERE w.username IS NOT NULL ORDER BY w.username COLLATE NOCASE"))
                accounts.Add(new AccountInfo(Str(r[0])!, Str(r[1]), Str(r[2]) ?? "", Date(r[3]), Str(r[4]), Str(r[5])));
            if (HasTable(db, "userlog"))
                foreach (var r in Query(db, "SELECT username, type, text, issuedBy, amount, lastUpdate FROM userlog ORDER BY id"))
                    log.Add(new UserLogEntry(Str(r[0]) ?? "", Str(r[1]) ?? "", Str(r[2]), Str(r[3]), r[4] is long n ? (int)n : 0, Date(r[5])));
            if (HasTable(db, "bannedip"))
                foreach (var r in Query(db, "SELECT ip, username, reason FROM bannedip"))
                    ips.Add(new BannedIp(Str(r[0]) ?? "", Str(r[1]), Str(r[2])));
            if (HasTable(db, "bannedid"))
                foreach (var r in Query(db, "SELECT steamid, reason FROM bannedid"))
                    ids.Add(new BannedSteamId(Str(r[0]) ?? "", Str(r[1])));
        }

        var characters = new List<CharacterInfo>();
        if (playersDb is not null && File.Exists(playersDb))
        {
            using var db = Open(playersDb);
            if (HasTable(db, "networkPlayers"))
                foreach (var r in Query(db, "SELECT username, name, x, y, z, isDead, playerIndex FROM networkPlayers"))
                    characters.Add(new CharacterInfo(Str(r[0]) ?? "", Str(r[1]), Int(r[2]), Int(r[3]), Int(r[4]), r[5] is long d && d != 0, Int(r[6])));
        }
        return new ServerDatabaseSnapshot(accounts, characters, log, ips, ids);
    }

    static SqliteConnection Open(string path)
    {
        // always a temporary copy, opened read-write: a journal copied along with it (a write was in
        // progress) must be rolled back, which SQLite refuses on a read-only connection. No pooling,
        // so the copy can be deleted afterwards. Only SELECTs run on it.
        var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString());
        db.Open();
        return db;
    }

    static bool HasTable(SqliteConnection db, string name)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND name=$n";
        cmd.Parameters.AddWithValue("$n", name);
        return cmd.ExecuteScalar() is not null;
    }

    static IEnumerable<object?[]> Query(SqliteConnection db, string sql)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var row = new object?[reader.FieldCount];
            for (int i = 0; i < row.Length; i++)
                row[i] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            yield return row;
        }
    }

    static string? Str(object? v) => v?.ToString() is { Length: > 0 } s ? s : null;

    static int Int(object? v) => v switch
    {
        long l => (int)l,
        double d => (int)Math.Floor(d),
        string s when double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) => (int)Math.Floor(d),
        _ => 0,
    };

    /// <summary>"2026-09-28 12:27:55" (userlog) or other common formats.</summary>
    static DateTime? Date(object? v) =>
        v?.ToString() is { Length: > 0 } s && DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var d) ? d : null;
}

/// <summary>Where the databases are: the server's Zomboid folder, over SFTP or on this PC.</summary>
public interface IServerDbSource
{
    string Description { get; }

    /// <summary>Server names that have a db/&lt;name&gt;.db.</summary>
    Task<IReadOnlyList<string>> ListServersAsync(CancellationToken ct = default);

    /// <summary>Copies both databases (and any journal next to them) into <paramref name="folder"/>.</summary>
    Task<(string ServerDb, string? PlayersDb)> CopyAsync(string server, string folder, CancellationToken ct = default);
}

public sealed class LocalServerDbSource(string zomboidFolder) : IServerDbSource
{
    public string Description => "Folder: " + zomboidFolder;

    public Task<IReadOnlyList<string>> ListServersAsync(CancellationToken ct = default)
    {
        var db = Path.Combine(zomboidFolder, "db");
        IReadOnlyList<string> names = Directory.Exists(db)
            ? Directory.EnumerateFiles(db, "*.db").Select(Path.GetFileNameWithoutExtension).OfType<string>().Order().ToList()
            : [];
        return Task.FromResult(names);
    }

    public Task<(string ServerDb, string? PlayersDb)> CopyAsync(string server, string folder, CancellationToken ct = default)
    {
        Directory.CreateDirectory(folder);
        string Copy(string source, string name)
        {
            // the server keeps the files open: copy through a shared read
            var target = Path.Combine(folder, name);
            using (var from = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var to = File.Create(target))
                from.CopyTo(to);
            return target;
        }
        // a journal left by a write in progress belongs with its database
        void CopyJournal(string db, string name)
        {
            try
            {
                if (File.Exists(db + "-journal"))
                    Copy(db + "-journal", name + "-journal");
            }
            catch (FileNotFoundException)
            {
                // gone in between: the write ended, the database alone is consistent
                File.Delete(Path.Combine(folder, name + "-journal"));
            }
        }
        var serverSource = Path.Combine(zomboidFolder, "db", server + ".db");
        var serverDb = Copy(serverSource, "server.db");
        CopyJournal(serverSource, "server.db");
        var players = Path.Combine(zomboidFolder, "Saves", "Multiplayer", server, "players.db");
        string? playersDb = null;
        if (File.Exists(players))
        {
            playersDb = Copy(players, "players.db");
            CopyJournal(players, "players.db");
        }
        return Task.FromResult((serverDb, playersDb));
    }
}

public sealed class SftpServerDbSource(SftpSettings settings, string zomboidFolder) : IServerDbSource
{
    public string Description => $"SFTP {settings.Host}: {zomboidFolder}";

    string Remote(params string[] parts) => zomboidFolder.TrimEnd('/') + "/" + string.Join('/', parts);

    public async Task<IReadOnlyList<string>> ListServersAsync(CancellationToken ct = default)
    {
        var (client, _) = await SftpProbe.ConnectAsync(settings, ct).ConfigureAwait(false);
        using (client)
        {
            var names = new List<string>();
            try
            {
                await foreach (var f in client.ListDirectoryAsync(Remote("db"), ct).ConfigureAwait(false))
                    if (f.IsRegularFile && f.Name.EndsWith(".db", StringComparison.OrdinalIgnoreCase))
                        names.Add(f.Name[..^3]);
            }
            catch (Renci.SshNet.Common.SftpPathNotFoundException) { }
            return names.Order().ToList();
        }
    }

    public async Task<(string ServerDb, string? PlayersDb)> CopyAsync(string server, string folder, CancellationToken ct = default)
    {
        Directory.CreateDirectory(folder);
        var (client, _) = await SftpProbe.ConnectAsync(settings, ct).ConfigureAwait(false);
        using (client)
        {
            async Task<string?> Download(string remote, string name)
            {
                if (!await client.ExistsAsync(remote, ct).ConfigureAwait(false))
                    return null;
                var target = Path.Combine(folder, name);
                try
                {
                    await using (var file = File.Create(target))
                        await client.DownloadFileAsync(remote, file, ct).ConfigureAwait(false);
                }
                catch (Renci.SshNet.Common.SftpPathNotFoundException)
                {
                    // gone in between (a journal is deleted when its write ends)
                    File.Delete(target);
                    return null;
                }
                return target;
            }
            var serverDb = await Download(Remote("db", server + ".db"), "server.db").ConfigureAwait(false)
                ?? throw new FileNotFoundException($"db/{server}.db not found on the server.");
            // a journal left by a write in progress belongs with the database
            await Download(Remote("db", server + ".db-journal"), "server.db-journal").ConfigureAwait(false);
            var players = await Download(Remote("Saves", "Multiplayer", server, "players.db"), "players.db").ConfigureAwait(false);
            if (players is not null)
                await Download(Remote("Saves", "Multiplayer", server, "players.db-journal"), "players.db-journal").ConfigureAwait(false);
            return (serverDb, players);
        }
    }
}
