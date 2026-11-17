using Majorsilence.Sql2Postgre.CodeConversion;
using Majorsilence.Sql2Postgre.Data;
using Majorsilence.Sql2Postgre.Integration.Tests.Infrastructure;
using Majorsilence.Sql2Postgre.Model;
using Majorsilence.Sql2Postgre.Postgres;
using Majorsilence.Sql2Postgre.SqlServer;
using Majorsilence.Sql2Postgre.Translation.Semantics;

namespace Majorsilence.Sql2Postgre.Integration.Tests.CodeConversion;

/// <summary>
/// A program whose SQL is T-SQL (TOP, ISNULL, GETDATE, bit = 1, SCOPE_IDENTITY, COUNT read as
/// int): the database is migrated by sql2pg, the program converted by <c>sql2pg code</c> with the
/// database as its source, and the converted program prints on PostgreSQL what the original
/// printed on SQL Server.
/// </summary>
[Category("Containers")]
public class SqlInCodeBothEnginesTests
{
    private const string Schema = """
        CREATE TABLE dbo.Customer (CustomerId int IDENTITY(1, 1) NOT NULL PRIMARY KEY, Name nvarchar(50) NOT NULL, Email nvarchar(100) NULL,
            IsActive bit NOT NULL DEFAULT 1, CreatedAt datetime2 NOT NULL DEFAULT SYSDATETIME());
        GO
        INSERT INTO dbo.Customer (Name, Email, IsActive) VALUES (N'Ann', N'ann@example.com', 1), (N'Bob', NULL, 1), (N'Cyd', N'cyd@example.com', 0), (N'Dee', N'', 1);
        GO
        """;

    private const string Program = """
        using System;
        using System.Data;
        using System.Text;
        using Microsoft.Data.SqlClient;

        public static class Sample
        {
            private const string Active = "SELECT TOP 2 Name FROM dbo.Customer WHERE IsActive = 1 ORDER BY Name DESC";

            public static string Run(string connectionString)
            {
                var output = new StringBuilder();
                using var connection = new SqlConnection(connectionString);
                connection.Open();

                using (var command = new SqlCommand(Active, connection))
                using (var reader = command.ExecuteReader())
                    while (reader.Read())
                        output.Append(reader.GetString(0)).Append(';');

                using (var count = new SqlCommand("SELECT COUNT(*) FROM dbo.Customer WHERE ISNULL(Email, '') <> ''", connection))
                    output.Append("with email ").Append((int)count.ExecuteScalar()).Append(';');

                var insert = connection.CreateCommand();
                insert.CommandText = @"
                    INSERT INTO dbo.Customer (Name, Email, CreatedAt) VALUES (@name, @email, GETDATE());
                    SELECT CAST(SCOPE_IDENTITY() AS int);";
                insert.Parameters.Add(new SqlParameter("@name", SqlDbType.NVarChar, 50) { Value = "Eve" });
                insert.Parameters.Add(new SqlParameter("@email", SqlDbType.NVarChar, 100) { Value = DBNull.Value });
                int id = (int)insert.ExecuteScalar();
                output.Append("new id ").Append(id).Append(';');

                using (var update = new SqlCommand("UPDATE dbo.Customer SET IsActive = 0, Email = LOWER(Name) + '@example.com' WHERE CustomerId = @id", connection))
                {
                    update.Parameters.AddWithValue("@id", id);
                    output.Append("updated ").Append(update.ExecuteNonQuery()).Append(';');
                }

                using (var check = new SqlCommand("SELECT Email, CASE WHEN IsActive = 1 THEN 'yes' ELSE 'no' END FROM dbo.Customer WHERE CustomerId = @id", connection))
                {
                    check.Parameters.AddWithValue("@id", id);
                    using var reader = check.ExecuteReader();
                    reader.Read();
                    output.Append(reader.GetString(0)).Append(' ').Append(reader.GetString(1)).Append(';');
                }
                return output.ToString();
            }
        }
        """;

    [Test]
    public async Task SameOutputAfterConvertingDatabaseAndCode()
    {
        var options = new ConversionOptions { Extensions = new HashSet<string>() };
        await using var source = await SqlServerDatabase.CreateAsync(Schema, "sqlincode");
        await using var target = await PostgresDatabase.CreateAsync("sqlincode");
        var db = (await SqlServerCatalogReader.ReadAsync(source.ConnectionString)).Database;
        var schema = SchemaConverter.Convert(db, options);
        await PgApplier.ApplyAsync(schema.Objects.Where(o => o.Phase <= Phase.Tables), target.ConnectionString);
        Assert.That((await DataCopier.CopyAsync(db, options, source.ConnectionString, target.ConnectionString)).Succeeded, Is.True);
        await PgApplier.ApplyAsync(schema.Objects.Where(o => o.Phase > Phase.Tables), target.ConnectionString);

        var converted = new CodeConverter(new CodeConversionOptions(options, SemanticModel.Build(db))).ConvertFile("Sample.cs", Program);
        Assert.That(converted.Diagnostics.Where(d => d.Severity == Severity.Error), Is.Empty, string.Join("\n", converted.Diagnostics));

        string before = SampleProgram.Run(Program, "C#", source.ConnectionString);
        string after = SampleProgram.Run(converted.NewText, "C#", target.ConnectionString);
        Assert.Multiple(() =>
        {
            Assert.That(before, Is.EqualTo("Dee;Bob;with email 2;new id 5;updated 1;eve@example.com no;"));
            Assert.That(after, Is.EqualTo(before), converted.NewText);
        });
    }
}
