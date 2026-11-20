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
/// Roles, sys.indexes and server feature checks, converted by sql2pg and run on both engines
/// (WP 4.19): procedures that create a role when sys.database_principals does not list it, add
/// and remove a member through dynamic ALTER ROLE, and drop the role; sys.indexes for a table
/// with a primary key, a unique constraint and a filtered index, and for a heap; IFs on
/// SERVERPROPERTY values PostgreSQL answers with constants, whose other branch is not
/// translated. PostgreSQL roles belong to the server, so the role names are new for each run and
/// dropped at the end.
/// </summary>
[Category("Containers")]
public class RolesBothEnginesTests
{
    private const string Script = """
        CREATE TABLE dbo.Region (RegionId int NOT NULL CONSTRAINT PK_Region PRIMARY KEY, Code char(3) NOT NULL CONSTRAINT UQ_Region_Code UNIQUE, Name nvarchar(50) NULL);
        GO
        CREATE INDEX IX_Region_Name ON dbo.Region (Name) WHERE Name IS NOT NULL;
        GO
        CREATE TABLE dbo.Note (Body nvarchar(100) NOT NULL);
        GO
        CREATE INDEX IX_Note_Body ON dbo.Note (Body);
        GO
        CREATE TABLE dbo.Log (LogId int IDENTITY(1, 1) NOT NULL CONSTRAINT PK_Log PRIMARY KEY, Line nvarchar(200) NOT NULL);
        GO
        CREATE PROCEDURE dbo.EnsureRole @RoleName sysname AS
        BEGIN
            SET NOCOUNT ON;
            IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = @RoleName AND type = N'R')
            BEGIN
                DECLARE @SQL nvarchar(max) = N'CREATE ROLE ' + QUOTENAME(@RoleName) + N';';
                EXECUTE (@SQL);
                INSERT dbo.Log (Line) VALUES (N'created ' + @RoleName);
            END;
        END;
        GO
        CREATE PROCEDURE dbo.AddMember @RoleName sysname, @MemberName sysname AS
        BEGIN
            SET NOCOUNT ON;
            IF NOT EXISTS (SELECT 1 FROM sys.database_role_members AS drm
                           INNER JOIN sys.database_principals AS r ON drm.role_principal_id = r.principal_id AND r.type = N'R'
                           INNER JOIN sys.database_principals AS m ON drm.member_principal_id = m.principal_id
                           WHERE r.name = @RoleName AND m.name = @MemberName)
            BEGIN
                DECLARE @SQL nvarchar(max) = N'ALTER ROLE ' + QUOTENAME(@RoleName) + N' ADD MEMBER ' + QUOTENAME(@MemberName) + N';';
                EXECUTE (@SQL);
                INSERT dbo.Log (Line) VALUES (N'added ' + @MemberName);
            END;
        END;
        GO
        CREATE PROCEDURE dbo.RemoveMember @RoleName sysname, @MemberName sysname AS
        BEGIN
            DECLARE @SQL nvarchar(max) = N'ALTER ROLE ' + QUOTENAME(@RoleName) + N' DROP MEMBER ' + QUOTENAME(@MemberName) + N';';
            EXECUTE (@SQL);
        END;
        GO
        CREATE PROCEDURE dbo.DropRole @RoleName sysname AS
        BEGIN
            DECLARE @SQL nvarchar(max) = N'DROP ROLE IF EXISTS ' + QUOTENAME(@RoleName) + N';';
            EXECUTE (@SQL);
        END;
        GO
        CREATE PROCEDURE dbo.CheckFeatures AS
        BEGIN
            SET NOCOUNT ON;
            IF SERVERPROPERTY(N'IsHadrEnabled') = 0
                INSERT dbo.Log (Line) VALUES (N'no availability groups');
            ELSE
                EXEC sp_configure 'show advanced options', 1;
            IF CAST(SERVERPROPERTY(N'EngineEdition') AS int) = 5
            BEGIN
                EXEC sp_configure 'show advanced options', 1;
                RECONFIGURE;
            END
            ELSE
                INSERT dbo.Log (Line) VALUES (N'not Azure SQL Database');
        END;
        GO
        """;

    private static readonly ConversionOptions Options = new() { TargetVersion = Engines.TargetVersion, Extensions = new HashSet<string>() };
    private static readonly string Suffix = Guid.NewGuid().ToString("N")[..8];
    private static readonly string Role = $"Sql2pg Sales {Suffix}";
    private static readonly string Member = $"sql2pg_member_{Suffix}";
    private SqlServerDatabase _source = null!;
    private PostgresDatabase _target = null!;
    private SemanticModel _model = null!;
    private IReadOnlyList<ConvertedObject> _objects = null!;

    [OneTimeSetUp]
    public async Task Migrate()
    {
        _source = await SqlServerDatabase.CreateAsync(Script, "roles");
        var db = (await SqlServerCatalogReader.ReadAsync(_source.ConnectionString)).Database;
        _model = SemanticModel.Build(db);
        _target = await PostgresDatabase.CreateAsync("roles");
        var schema = SchemaConverter.Convert(db, Options);
        _objects = schema.Objects;
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
        {
            await _target.DisposeAsync();
            await using var conn = new NpgsqlConnection(await Engines.PostgresAsync());
            await conn.OpenAsync();
            foreach (string role in new[] { Role, Member })
            {
                await using var cmd = new NpgsqlCommand($"DROP ROLE IF EXISTS \"{role}\"", conn);
                await cmd.ExecuteNonQueryAsync();
            }
        }
    }

    [Test]
    public async Task RoleCreatedGivenAMemberAndDropped()
    {
        await RunBothAsync($"EXEC dbo.EnsureRole @RoleName = N'{Role}'");
        await RunBothAsync($"EXEC dbo.EnsureRole @RoleName = N'{Role}'");
        await RunBothAsync($"EXEC dbo.EnsureRole @RoleName = N'{Member}'");
        await RunBothAsync($"EXEC dbo.AddMember @RoleName = N'{Role}', @MemberName = N'{Member}'");
        await RunBothAsync($"EXEC dbo.AddMember @RoleName = N'{Role}', @MemberName = N'{Member}'");
        await AssertSameAsync($"""
            SELECT r.name AS role_name, r.type AS role_type, m.name AS member_name, m.type AS member_type
            FROM sys.database_role_members AS drm
            JOIN sys.database_principals AS r ON r.principal_id = drm.role_principal_id
            JOIN sys.database_principals AS m ON m.principal_id = drm.member_principal_id
            WHERE r.name = N'{Role}'
            """);
        await RunBothAsync($"EXEC dbo.RemoveMember @RoleName = N'{Role}', @MemberName = N'{Member}'");
        await AssertSameAsync($"SELECT COUNT(*) AS members FROM sys.database_role_members AS drm JOIN sys.database_principals AS r ON r.principal_id = drm.role_principal_id WHERE r.name = N'{Role}'");
        await RunBothAsync($"EXEC dbo.DropRole @RoleName = N'{Role}'");
        await RunBothAsync($"EXEC dbo.DropRole @RoleName = N'{Role}'");
        await RunBothAsync($"EXEC dbo.DropRole @RoleName = N'{Member}'");
        await AssertSameAsync($"SELECT COUNT(*) AS left_over FROM sys.database_principals WHERE name IN (N'{Role}', N'{Member}')");
        await AssertSameAsync("SELECT Line FROM dbo.Log WHERE Line LIKE N'created%' OR Line LIKE N'added%' ORDER BY LogId");
        await AssertSameAsync("SELECT name, type, type_desc FROM sys.database_principals WHERE name = N'public'");
    }

    [Test]
    public async Task IndexesListedUnderTheirSqlServerNames()
    {
        await AssertSameAsync("""
            SELECT OBJECT_NAME(i.object_id) AS table_name, i.name, i.index_id, i.type, i.type_desc, i.is_unique, i.is_primary_key,
                   i.is_unique_constraint, i.is_disabled, i.has_filter
            FROM sys.indexes AS i JOIN sys.tables AS t ON t.object_id = i.object_id
            WHERE t.name IN (N'Region', N'Note')
            ORDER BY table_name, i.index_id
            """);
        await AssertSameAsync("SELECT CASE WHEN EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Region_Name') THEN 1 ELSE 0 END AS present, "
                              + "CASE WHEN EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'NCCX_Region') THEN 1 ELSE 0 END AS absent");
    }

    [Test]
    public async Task BranchesOnServerFeaturesPostgreSqlAnswersWithConstants()
    {
        var procedure = _objects.Single(o => o.Source.Name == "CheckFeatures");
        Assert.That(procedure.Diagnostics.Count(d => d.Code == DiagnosticCodes.FeatureBranchNotTaken), Is.EqualTo(2), procedure.Sql);
        Assert.That(procedure.Sql, Does.Contain("sql2pg:untranslated S2P3046"));
        await RunBothAsync("EXEC dbo.CheckFeatures");
        await AssertSameAsync("SELECT Line FROM dbo.Log WHERE Line NOT LIKE N'created%' AND Line NOT LIKE N'added%' ORDER BY LogId");
    }

    /// <summary>Runs the T-SQL on SQL Server and its translation on PostgreSQL.</summary>
    private async Task RunBothAsync(string tsql)
    {
        var translation = ScriptTranslator.Translate(tsql, _model, Options);
        Assert.That(translation.Diagnostics.Where(d => d.Severity == Severity.Error), Is.Empty, translation.Sql);
        await using (var conn = new SqlConnection(_source.ConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = new SqlCommand(tsql, conn);
            await cmd.ExecuteNonQueryAsync();
        }
        await using (var conn = new NpgsqlConnection(_target.ConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand(translation.Sql, conn);
            await cmd.ExecuteNonQueryAsync();
        }
    }

    /// <summary>The query and its translation return the same rows.</summary>
    private async Task AssertSameAsync(string tsql)
    {
        var translation = ScriptTranslator.Translate(tsql, _model, Options);
        Assert.That(translation.Diagnostics.Where(d => d.Severity == Severity.Error), Is.Empty, translation.Sql);
        var expected = await BothEngines.RunSqlServerAsync(tsql, connectionString: _source.ConnectionString);
        var actual = await BothEngines.RunPostgresAsync(translation.Sql, connectionString: _target.ConnectionString);
        Assert.That(expected.Rows, Is.Not.Empty);
        Assert.That(actual.Rows, Is.EqualTo(expected.Rows), translation.Sql);
    }
}
