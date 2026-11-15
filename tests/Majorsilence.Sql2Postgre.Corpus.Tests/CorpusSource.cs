using Majorsilence.Sql2Postgre.Integration.Tests.Infrastructure;
using Majorsilence.Sql2Postgre.SqlServer;
using Microsoft.Data.SqlClient;

namespace Majorsilence.Sql2Postgre.Corpus.Tests;

/// <summary>One public sample database: a backup restored into the SQL Server container, or an install script run there.</summary>
public sealed record CorpusEntry(string Database, string File)
{
    public override string ToString() => Database;

    public static readonly IReadOnlyList<CorpusEntry> All =
    [
        new("AdventureWorks2022", "AdventureWorks2022.bak"),
        new("AdventureWorksDW2022", "AdventureWorksDW2022.bak"),
        new("WideWorldImporters", "WideWorldImporters-Full.bak"),
        new("WideWorldImportersDW", "WideWorldImportersDW-Full.bak"),
        new("Northwind", "instnwnd.sql"),
        new("pubs", "instpubs.sql"),
    ];
}

/// <summary>
/// Makes a corpus database available on the shared SQL Server container. The files live in
/// <c>tests/corpus/</c> (see <c>scripts/download-corpus.sh</c>); a test whose file is missing is
/// ignored with a note saying how to get it.
/// </summary>
public static class CorpusSource
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly HashSet<string> Loaded = new(StringComparer.OrdinalIgnoreCase);

    public static string Directory
    {
        get
        {
            string? dir = TestContext.CurrentContext.TestDirectory;
            while (dir is not null && !System.IO.File.Exists(Path.Combine(dir, "Majorsilence.Sql2Postgre.slnx")))
                dir = Path.GetDirectoryName(dir);
            return Path.Combine(dir ?? ".", "tests", "corpus");
        }
    }

    /// <summary>A connection string to the restored database (restored once per test run).</summary>
    public static async Task<string> ConnectionStringAsync(CorpusEntry entry)
    {
        var file = new FileInfo(Path.Combine(Directory, entry.File));
        if (!file.Exists)
            Assert.Ignore($"{entry.File} is not in tests/corpus; run: bash scripts/download-corpus.sh");

        string master = await Engines.SqlServerAsync();
        await Gate.WaitAsync();
        try
        {
            if (!Loaded.Contains(entry.Database))
            {
                if (file.Extension == ".bak")
                    await RestoreAsync(file, entry.Database, master);
                else
                    await RunInstallScriptAsync(file, entry.Database, master);
                Loaded.Add(entry.Database);
            }
        }
        finally
        {
            Gate.Release();
        }
        return new SqlConnectionStringBuilder(master) { InitialCatalog = entry.Database, CommandTimeout = 0 }.ConnectionString;
    }

    private static async Task RestoreAsync(FileInfo backup, string database, string master)
    {
        const string BackupDir = "/var/opt/mssql/backup";
        var container = await Engines.SqlServerContainerAsync();
        await container.ExecAsync(["mkdir", "-p", BackupDir]);
        await container.CopyAsync(backup, BackupDir);
        string path = $"{BackupDir}/{backup.Name}";

        await using var conn = new SqlConnection(master);
        await conn.OpenAsync();
        var moves = new List<string>();
        await using (var list = new SqlCommand($"RESTORE FILELISTONLY FROM DISK = N'{path}'", conn) { CommandTimeout = 0 })
        await using (var reader = await list.ExecuteReaderAsync())
        {
            int i = 0;
            while (await reader.ReadAsync())
            {
                string logical = reader.GetString(reader.GetOrdinal("LogicalName"));
                string type = reader.GetString(reader.GetOrdinal("Type"));
                // D data, L log, S a FILESTREAM or memory-optimized container (a directory).
                string target = type switch
                {
                    "L" => $"/var/opt/mssql/data/{database}_{i}.ldf",
                    "S" => $"/var/opt/mssql/data/{database}_{i}_container",
                    _ => $"/var/opt/mssql/data/{database}_{i}.mdf",
                };
                moves.Add($"MOVE N'{logical.Replace("'", "''")}' TO N'{target}'");
                i++;
            }
        }
        await using var restore = new SqlCommand(
            $"RESTORE DATABASE [{database}] FROM DISK = N'{path}' WITH {string.Join(", ", moves)}, REPLACE", conn) { CommandTimeout = 0 };
        await restore.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// pubs's install script creates its own database and USEs it; Northwind's says to run it in
    /// the database it should fill, so that one is created first.
    /// </summary>
    private static async Task RunInstallScriptAsync(FileInfo script, string database, string master)
    {
        string text = await System.IO.File.ReadAllTextAsync(script.FullName);
        string connection = master;
        if (!text.Contains("CREATE DATABASE", StringComparison.OrdinalIgnoreCase))
        {
            await using (var create = new SqlConnection(master))
            {
                await create.OpenAsync();
                await using var cmd = new SqlCommand($"IF DB_ID(N'{database}') IS NULL CREATE DATABASE [{database}]", create);
                await cmd.ExecuteNonQueryAsync();
            }
            connection = new SqlConnectionStringBuilder(master) { InitialCatalog = database }.ConnectionString;
        }
        await using var conn = new SqlConnection(connection);
        await conn.OpenAsync();
        foreach (string batch in BatchSplitter.Split(text))
        {
            await using var cmd = new SqlCommand(batch, conn) { CommandTimeout = 0 };
            await cmd.ExecuteNonQueryAsync();
        }
    }
}
