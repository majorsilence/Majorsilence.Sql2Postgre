using Majorsilence.Sql2Postgre.Data;
using Majorsilence.Sql2Postgre.Integration.Tests.Infrastructure;
using Majorsilence.Sql2Postgre.Model;
using Majorsilence.Sql2Postgre.Postgres;
using Majorsilence.Sql2Postgre.SqlServer;

namespace Majorsilence.Sql2Postgre.Corpus.Tests;

/// <summary>A corpus database migrated into PostgreSQL: what was read, converted, applied, copied and verified.</summary>
public sealed record MigratedDatabase(CorpusEntry Entry, string SourceConnection, string TargetConnection,
    SourceReadResult Read, ConversionResult Conversion, ApplyReport Before, ApplyReport After,
    DataCopyResult Copy, VerificationResult Verify);

/// <summary>
/// Migrates a corpus database (schema up to the tables, data, the rest of the schema, checksum
/// verification). With the default options the migration is made once per test run and shared by
/// the compile scan and the result-agreement harness; its database is dropped with the container.
/// </summary>
public static class CorpusMigration
{
    public static readonly ConversionOptions DefaultOptions = new() { TargetVersion = Engines.TargetVersion, Extensions = new HashSet<string>() };

    private static readonly Dictionary<string, Task<MigratedDatabase>> Shared = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Lock SharedLock = new();

    /// <summary>The shared migration with <see cref="DefaultOptions"/>.</summary>
    public static Task<MigratedDatabase> DefaultAsync(CorpusEntry entry)
    {
        lock (SharedLock)
        {
            if (!Shared.TryGetValue(entry.Database, out var task) || task.IsFaulted || task.IsCanceled)
                Shared[entry.Database] = task = Task.Run(async () =>
                {
                    var target = await PostgresDatabase.CreateAsync(entry.Database.ToLowerInvariant());
                    return await MigrateAsync(entry, DefaultOptions, target.ConnectionString);
                });
            return task;
        }
    }

    public static async Task<MigratedDatabase> MigrateAsync(CorpusEntry entry, ConversionOptions options, string target)
    {
        string source = await CorpusSource.ConnectionStringAsync(entry);
        var read = await SqlServerCatalogReader.ReadAsync(source);
        var conversion = SchemaConverter.Convert(read.Database, options);
        var before = await PgApplier.ApplyAsync(conversion.Objects.Where(o => o.Phase <= Phase.Tables), target);
        var copy = await DataCopier.CopyAsync(read.Database, options, source, target);
        var after = await PgApplier.ApplyAsync(conversion.Objects.Where(o => o.Phase > Phase.Tables), target);
        var verify = await DataVerifier.VerifyAsync(read.Database, options, source, target, VerifyMode.Checksum);
        return new MigratedDatabase(entry, source, target, read, conversion, before, after, copy, verify);
    }
}
