using Majorsilence.Sql2Postgre.Data;
using Majorsilence.Sql2Postgre.Integration.Tests.Infrastructure;
using Majorsilence.Sql2Postgre.Model;
using Majorsilence.Sql2Postgre.Postgres;
using Majorsilence.Sql2Postgre.SqlServer;
using Majorsilence.Sql2Postgre.Translation.Semantics;
using Microsoft.Data.SqlClient;
using Npgsql;

namespace Majorsilence.Sql2Postgre.Integration.Tests.Translation;

/// <summary>
/// A system-versioned temporal table, migrated by sql2pg: the same updates and deletes run on both
/// engines, one transaction each, and then every FOR SYSTEM_TIME form returns the same rows. The
/// period values themselves differ (each engine stamps its own clock), so they are compared only
/// through the rows they select. A load procedure that switches versioning and the period off and
/// on again leaves the same rows and history on both.
/// </summary>
[Category("Containers")]
public class TemporalBothEnginesTests
{
    private const string Script = """
        CREATE TABLE dbo.CustomerHistory (CustomerId int NOT NULL, Name nvarchar(50) NOT NULL, Credit decimal(10, 2) NOT NULL,
            ValidFrom datetime2(7) NOT NULL, ValidTo datetime2(7) NOT NULL);
        GO
        CREATE TABLE dbo.Customer (CustomerId int NOT NULL CONSTRAINT PK_Customer PRIMARY KEY, Name nvarchar(50) NOT NULL, Credit decimal(10, 2) NOT NULL,
            ValidFrom datetime2(7) GENERATED ALWAYS AS ROW START NOT NULL,
            ValidTo datetime2(7) GENERATED ALWAYS AS ROW END NOT NULL,
            PERIOD FOR SYSTEM_TIME (ValidFrom, ValidTo))
        WITH (SYSTEM_VERSIONING = ON (HISTORY_TABLE = dbo.CustomerHistory));
        GO
        INSERT INTO dbo.Customer (CustomerId, Name, Credit) VALUES (1, N'Ann', 10.00), (2, N'Bob', 15.00), (3, N'Cat', 5.00);
        GO
        CREATE PROCEDURE dbo.StopVersioning AS
        BEGIN
            SET NOCOUNT ON;
            ALTER TABLE dbo.Customer SET (SYSTEM_VERSIONING = OFF);
            ALTER TABLE dbo.Customer DROP PERIOD FOR SYSTEM_TIME;
        END;
        GO
        CREATE PROCEDURE dbo.LoadCustomers AS
        BEGIN
            SET NOCOUNT ON;
            EXEC (N'UPDATE dbo.Customer SET Credit = Credit + 1, ValidFrom = (SELECT MAX(ValidTo) FROM dbo.CustomerHistory WHERE CustomerId = 1) WHERE CustomerId = 1');
        END;
        GO
        CREATE PROCEDURE dbo.StartVersioning AS
        BEGIN
            SET NOCOUNT ON;
            ALTER TABLE dbo.Customer ADD PERIOD FOR SYSTEM_TIME (ValidFrom, ValidTo);
            ALTER TABLE dbo.Customer SET (SYSTEM_VERSIONING = ON (HISTORY_TABLE = dbo.CustomerHistory, DATA_CONSISTENCY_CHECK = ON));
        END;
        GO
        """;

    private static readonly ConversionOptions Options = new() { Extensions = new HashSet<string>() };
    private SqlServerDatabase _source = null!;
    private PostgresDatabase _target = null!;
    private SemanticModel _model = null!;

    [OneTimeSetUp]
    public async Task Migrate()
    {
        _source = await SqlServerDatabase.CreateAsync(Script, "temporal");
        var db = (await SqlServerCatalogReader.ReadAsync(_source.ConnectionString)).Database;
        Assert.That(db.Tables.Single(t => t.Name.Name == "Customer").Period,
            Is.EqualTo(new SystemTimePeriod("ValidFrom", "ValidTo", new ObjectName("dbo", "CustomerHistory"))));
        _model = SemanticModel.Build(db);
        _target = await PostgresDatabase.CreateAsync("temporal");
        var schema = SchemaConverter.Convert(db, Options);
        Assert.That(schema.Objects.Select(o => $"{o.Source} {o.Status}"), Has.None.EndsWith(" Manual"),
            string.Join("\n", schema.Objects.SelectMany(o => o.Diagnostics).Select(d => d.ToString())));
        await PgApplier.ApplyAsync(schema.Objects.Where(o => o.Phase <= Phase.Tables), _target.ConnectionString);
        Assert.That((await DataCopier.CopyAsync(db, Options, _source.ConnectionString, _target.ConnectionString)).Succeeded, Is.True);
        var after = await PgApplier.ApplyAsync(schema.Objects.Where(o => o.Phase > Phase.Tables), _target.ConnectionString);
        Assert.That(after.Failed, Is.Zero, string.Join("\n", after.Outcomes.Where(o => o.Status == ApplyStatus.Failed).Select(o => $"{o.TargetName}: {o.Message}")));
    }

    [OneTimeTearDown]
    public async Task Drop()
    {
        if (_source is not null)
            await _source.DisposeAsync();
        if (_target is not null)
            await _target.DisposeAsync();
    }

    [Test]
    public async Task HistoryAndEveryForSystemTimeForm()
    {
        await RunBothAsync("UPDATE dbo.Customer SET Credit = 20.00 WHERE CustomerId = 1");
        await RunBothAsync("DELETE FROM dbo.Customer WHERE CustomerId = 2");
        // A moment between the delete and the next change, on each engine's clock.
        var (sqlMoment, pgMoment) = (await SqlServerScalarAsync("SELECT CONVERT(varchar(27), SYSUTCDATETIME(), 121)"),
            await PostgresScalarAsync("SELECT to_char(now() AT TIME ZONE 'UTC', 'YYYY-MM-DD HH24:MI:SS.US')"));
        await Task.Delay(50);
        await RunBothAsync("UPDATE dbo.Customer SET Name = N'Cy' WHERE CustomerId = 3");
        await RunBothAsync("UPDATE dbo.Customer SET Credit = 30.00 WHERE CustomerId = 1");

        await AssertSameAsync("SELECT CustomerId, Name, Credit FROM dbo.Customer FOR SYSTEM_TIME ALL ORDER BY CustomerId, ValidFrom");
        await AssertSameAsync("SELECT CustomerId, COUNT(*) AS versions FROM dbo.CustomerHistory GROUP BY CustomerId ORDER BY CustomerId");
        await AssertSameAsync("SELECT CustomerId, Name, Credit FROM dbo.Customer FOR SYSTEM_TIME AS OF '{0}' AS c ORDER BY CustomerId", sqlMoment, pgMoment);
        await AssertSameAsync("SELECT COUNT(*) AS n FROM dbo.Customer FOR SYSTEM_TIME FROM '2000-01-01' TO '{0}'", sqlMoment, pgMoment);
        await AssertSameAsync("SELECT COUNT(*) AS n FROM dbo.Customer FOR SYSTEM_TIME BETWEEN '{0}' AND '9999-01-01'", sqlMoment, pgMoment);
        await AssertSameAsync("SELECT CustomerId, Credit FROM dbo.Customer FOR SYSTEM_TIME CONTAINED IN ('2000-01-01', '{0}') ORDER BY CustomerId, Credit", sqlMoment, pgMoment);
        await AssertSameAsync("SELECT c.Name FROM dbo.Customer FOR SYSTEM_TIME ALL AS c JOIN dbo.Customer AS now ON now.CustomerId = c.CustomerId WHERE c.Name <> now.Name");

        // Versioning and the period off around a load: no history, and the period start as written.
        await RunBothAsync("EXEC dbo.StopVersioning");
        await RunBothAsync("EXEC dbo.LoadCustomers");
        await RunBothAsync("EXEC dbo.StartVersioning");
        await AssertSameAsync("SELECT CustomerId, Credit, CASE WHEN ValidFrom = (SELECT MAX(ValidTo) FROM dbo.CustomerHistory) THEN 1 ELSE 0 END AS as_written FROM dbo.Customer WHERE CustomerId = 1");
        await AssertSameAsync("SELECT COUNT(*) AS versions FROM dbo.CustomerHistory");
        // Both on again: the next change is versioned.
        await RunBothAsync("UPDATE dbo.Customer SET Credit = 40.00 WHERE CustomerId = 1");
        await AssertSameAsync("SELECT CustomerId, Credit FROM dbo.CustomerHistory ORDER BY CustomerId, ValidFrom");
    }

    /// <summary>Runs the T-SQL on SQL Server and its translation on PostgreSQL.</summary>
    private async Task RunBothAsync(string tsql)
    {
        var translation = ScriptTranslator.Translate(tsql, _model, Options);
        Assert.That(translation.Diagnostics.Where(d => d.Severity == Severity.Error), Is.Empty, translation.Sql);
        await using (var conn = new SqlConnection(_source.ConnectionString))
        {
            await conn.OpenAsync();
            await new SqlCommand(tsql, conn).ExecuteNonQueryAsync();
        }
        await using (var conn = new NpgsqlConnection(_target.ConnectionString))
        {
            await conn.OpenAsync();
            await new NpgsqlCommand(translation.Sql, conn).ExecuteNonQueryAsync();
        }
    }

    /// <summary>A query run on both engines, with <c>{0}</c> replaced by each engine's own value.</summary>
    private async Task AssertSameAsync(string tsql, string? sqlValue = null, string? pgValue = null)
    {
        string forSqlServer = string.Format(System.Globalization.CultureInfo.InvariantCulture, tsql, sqlValue);
        var translation = ScriptTranslator.Translate(string.Format(System.Globalization.CultureInfo.InvariantCulture, tsql, pgValue), _model, Options);
        Assert.That(translation.Diagnostics.Where(d => d.Severity == Severity.Error), Is.Empty, translation.Sql);
        var expected = await BothEngines.RunSqlServerAsync(forSqlServer, connectionString: _source.ConnectionString);
        var actual = await BothEngines.RunPostgresAsync(translation.Sql, connectionString: _target.ConnectionString);
        Assert.That(actual.Rows, Is.EqualTo(expected.Rows), translation.Sql);
    }

    private async Task<string> SqlServerScalarAsync(string sql)
    {
        await using var conn = new SqlConnection(_source.ConnectionString);
        await conn.OpenAsync();
        return (string)(await new SqlCommand(sql, conn).ExecuteScalarAsync())!;
    }

    private async Task<string> PostgresScalarAsync(string sql)
    {
        await using var conn = new NpgsqlConnection(_target.ConnectionString);
        await conn.OpenAsync();
        return (string)(await new NpgsqlCommand(sql, conn).ExecuteScalarAsync())!;
    }
}
