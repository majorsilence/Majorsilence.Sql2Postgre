using System.Text.Json;
using Majorsilence.Sql2Postgre.Data;
using Majorsilence.Sql2Postgre.Integration.Tests.Infrastructure;
using Majorsilence.Sql2Postgre.Model;
using Majorsilence.Sql2Postgre.Postgres;
using Majorsilence.Sql2Postgre.SqlServer;
using Microsoft.Data.SqlClient;

namespace Majorsilence.Sql2Postgre.Integration.Tests.Data;

/// <summary>
/// Incremental sync (data --since): a full first pass, rows changed on SQL Server, then a second
/// pass that brings each table up to date by its rowversion, its change tracking, or a whole copy,
/// verified by checksum; a third pass with nothing changed moves no rows.
/// </summary>
[Category("Containers")]
public class IncrementalSyncTests
{
    private const string Script = """
        ALTER DATABASE CURRENT SET CHANGE_TRACKING = ON (CHANGE_RETENTION = 2 DAYS, AUTO_CLEANUP = ON);
        GO
        CREATE TABLE dbo.Versioned (Id int NOT NULL PRIMARY KEY, Name nvarchar(40) NOT NULL, Rv rowversion);
        CREATE TABLE dbo.Tracked (Id int NOT NULL PRIMARY KEY, Qty int NOT NULL, Note nvarchar(20) NULL);
        CREATE TABLE dbo.Plain (Id int IDENTITY NOT NULL PRIMARY KEY, Note nvarchar(20) NULL);
        GO
        ALTER TABLE dbo.Tracked ENABLE CHANGE_TRACKING;
        GO
        INSERT INTO dbo.Versioned (Id, Name) VALUES (1, N'one'), (2, N'two'), (3, N'three');
        INSERT INTO dbo.Tracked (Id, Qty) VALUES (1, 10), (2, 20), (3, 30);
        INSERT INTO dbo.Plain (Note) VALUES (N'a'), (N'b');
        GO
        """;

    [Test]
    public async Task ChangedRowsAreCopiedByEachTablesMeans()
    {
        await using var source = await SqlServerDatabase.CreateAsync(Script, "sync");
        await using var target = await PostgresDatabase.CreateAsync("sync");
        string statePath = Path.Combine(Path.GetTempPath(), $"sql2pg-sync-{Guid.NewGuid():N}.json");
        try
        {
            var db = (await SqlServerCatalogReader.ReadAsync(source.ConnectionString)).Database;
            var options = new ConversionOptions { TargetVersion = Engines.TargetVersion, Extensions = new HashSet<string>() };
            var schema = SchemaConverter.Convert(db, options);
            await PgApplier.ApplyAsync(schema.Objects.Where(o => o.Phase <= Phase.Tables), target.ConnectionString);
            var first = await IncrementalCopier.SyncAsync(db, options, source.ConnectionString, target.ConnectionString, statePath);
            Assert.That(first.Succeeded, Is.True, string.Join("\n", first.Diagnostics));
            var applied = await PgApplier.ApplyAsync(schema.Objects.Where(o => o.Phase > Phase.Tables), target.ConnectionString);
            Assert.That(applied.Failed, Is.Zero);

            await using (var sql = new SqlConnection(source.ConnectionString))
            {
                await sql.OpenAsync();
                await using var change = new SqlCommand("""
                    UPDATE dbo.Versioned SET Name = N'uno' WHERE Id = 1;
                    INSERT INTO dbo.Versioned (Id, Name) VALUES (4, N'four');
                    UPDATE dbo.Tracked SET Qty = 11, Note = N'changed' WHERE Id = 1;
                    INSERT INTO dbo.Tracked (Id, Qty) VALUES (4, 40);
                    DELETE FROM dbo.Tracked WHERE Id = 2;
                    INSERT INTO dbo.Plain (Note) VALUES (N'c');
                    UPDATE dbo.Plain SET Note = N'B' WHERE Id = 2;
                    """, sql);
                await change.ExecuteNonQueryAsync();
            }

            var second = await IncrementalCopier.SyncAsync(db, options, source.ConnectionString, target.ConnectionString, statePath);
            Assert.That(second.Succeeded, Is.True, string.Join("\n", second.Diagnostics));
            var rows = second.Tables.ToDictionary(t => t.Table.Name, t => t.Rows);
            Assert.That(rows, Is.EqualTo(new Dictionary<string, long> { ["Plain"] = 3, ["Tracked"] = 3, ["Versioned"] = 2 }));

            var state = JsonSerializer.Deserialize<Dictionary<string, SyncTableState>>(await File.ReadAllTextAsync(statePath))!;
            Assert.That(state.ToDictionary(s => s.Key, s => s.Value.Mode), Is.EqualTo(new Dictionary<string, string>
            {
                ["dbo.Plain"] = IncrementalCopier.FullMode,
                ["dbo.Tracked"] = IncrementalCopier.ChangeTrackingMode,
                ["dbo.Versioned"] = IncrementalCopier.RowVersionMode,
            }));

            var verify = await DataVerifier.VerifyAsync(db, options, source.ConnectionString, target.ConnectionString, VerifyMode.Checksum);
            Assert.That(verify.Passed, Is.True, string.Join("\n", verify.Tables.Where(t => !t.Passed).Select(t => $"{t.Table}: {t.SourceRows}/{t.TargetRows}, checksum {t.ChecksumMatches}")));

            // Nothing changed: the tracked tables move no rows.
            var third = await IncrementalCopier.SyncAsync(db, options, source.ConnectionString, target.ConnectionString, statePath);
            Assert.That(third.Tables.Where(t => t.Table.Name != "Plain").Select(t => t.Rows), Has.All.Zero);
        }
        finally
        {
            File.Delete(statePath);
        }
    }
}
