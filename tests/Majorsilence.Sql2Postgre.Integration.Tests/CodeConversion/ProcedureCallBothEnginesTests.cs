using Majorsilence.Sql2Postgre.CodeConversion;
using Majorsilence.Sql2Postgre.Data;
using Majorsilence.Sql2Postgre.Integration.Tests.Infrastructure;
using Majorsilence.Sql2Postgre.Model;
using Majorsilence.Sql2Postgre.Postgres;
using Majorsilence.Sql2Postgre.SqlServer;
using Majorsilence.Sql2Postgre.Translation.Semantics;

namespace Majorsilence.Sql2Postgre.Integration.Tests.CodeConversion;

/// <summary>
/// Stored procedure calls (CommandType.StoredProcedure) after migrating the database and
/// converting the code: a procedure that became a function is read with SELECT, one with an
/// OUTPUT parameter is CALLed with an INOUT parameter, in C# and VB.
/// </summary>
[Category("Containers")]
public class ProcedureCallBothEnginesTests
{
    private const string Schema = """
        CREATE TABLE dbo.Item (Id int IDENTITY(1, 1) NOT NULL PRIMARY KEY, Name nvarchar(50) NOT NULL, Price decimal(10, 2) NOT NULL);
        GO
        INSERT INTO dbo.Item (Name, Price) VALUES (N'apple', 1.20), (N'banana', 0.80), (N'cheese', 12.00);
        GO
        CREATE PROCEDURE dbo.GetCheapItems @MaxPrice decimal(10, 2) AS
            SELECT Name, Price FROM dbo.Item WHERE Price <= @MaxPrice ORDER BY Price;
        GO
        CREATE PROCEDURE dbo.AddItem @Name nvarchar(50), @Price decimal(10, 2), @Id int OUTPUT AS
        BEGIN
            INSERT INTO dbo.Item (Name, Price) VALUES (@Name, @Price);
            SET @Id = SCOPE_IDENTITY();
        END;
        GO
        """;

    private const string CSharp = """
        using System.Data;
        using System.Globalization;
        using System.Text;
        using Microsoft.Data.SqlClient;

        public static class Sample
        {
            public static string Run(string connectionString)
            {
                var output = new StringBuilder();
                using var connection = new SqlConnection(connectionString);
                connection.Open();

                var add = connection.CreateCommand();
                add.CommandText = "dbo.AddItem";
                add.CommandType = CommandType.StoredProcedure;
                add.Parameters.AddWithValue("@Name", "kiwi");
                add.Parameters.AddWithValue("@Price", 2.50m);
                var id = add.Parameters.Add("@Id", SqlDbType.Int);
                id.Direction = ParameterDirection.Output;
                add.ExecuteNonQuery();
                output.Append("id ").Append(id.Value).Append(';');

                using (var cheap = new SqlCommand("dbo.GetCheapItems", connection) { CommandType = CommandType.StoredProcedure })
                {
                    cheap.Parameters.AddWithValue("@MaxPrice", 5m);
                    using var reader = cheap.ExecuteReader();
                    while (reader.Read())
                        output.Append(reader.GetString(0)).Append('=').Append(reader.GetDecimal(1).ToString("0.00", CultureInfo.InvariantCulture)).Append(';');
                }
                return output.ToString();
            }
        }
        """;

    private const string VisualBasic = """
        Imports System.Data
        Imports System.Globalization
        Imports System.Text
        Imports Microsoft.Data.SqlClient

        Public Module Sample
            Public Function Run(connectionString As String) As String
                Dim output As New StringBuilder()
                Using connection As New SqlConnection(connectionString)
                    connection.Open()
                    Using add As New SqlCommand("dbo.AddItem", connection) With {.CommandType = CommandType.StoredProcedure}
                        add.Parameters.AddWithValue("@Name", "kiwi")
                        add.Parameters.AddWithValue("@Price", 2.5D)
                        Dim id = add.Parameters.Add("@Id", SqlDbType.Int)
                        id.Direction = ParameterDirection.Output
                        add.ExecuteNonQuery()
                        output.Append("id ").Append(id.Value).Append(";"c)
                    End Using
                    Using cheap As New SqlCommand("dbo.GetCheapItems", connection) With {.CommandType = CommandType.StoredProcedure}
                        cheap.Parameters.AddWithValue("@MaxPrice", 5D)
                        Using reader = cheap.ExecuteReader()
                            While reader.Read()
                                output.Append(reader.GetString(0)).Append("="c).Append(reader.GetDecimal(1).ToString("0.00", CultureInfo.InvariantCulture)).Append(";"c)
                            End While
                        End Using
                    End Using
                End Using
                Return output.ToString()
            End Function
        End Module
        """;

    [TestCase(CSharp, "Sample.cs")]
    [TestCase(VisualBasic, "Sample.vb")]
    public async Task SameOutputAfterConvertingDatabaseAndCode(string program, string file)
    {
        var options = new ConversionOptions { Extensions = new HashSet<string>() };
        await using var source = await SqlServerDatabase.CreateAsync(Schema, "proccalls");
        await using var target = await PostgresDatabase.CreateAsync("proccalls");
        var db = (await SqlServerCatalogReader.ReadAsync(source.ConnectionString)).Database;
        var schema = SchemaConverter.Convert(db, options);
        await PgApplier.ApplyAsync(schema.Objects.Where(o => o.Phase <= Phase.Tables), target.ConnectionString);
        Assert.That((await DataCopier.CopyAsync(db, options, source.ConnectionString, target.ConnectionString)).Succeeded, Is.True);
        var after = await PgApplier.ApplyAsync(schema.Objects.Where(o => o.Phase > Phase.Tables), target.ConnectionString);
        Assert.That(after.Failed, Is.Zero);

        var converted = new CodeConverter(new CodeConversionOptions(options, SemanticModel.Build(db))).ConvertFile(file, program);
        Assert.That(converted.Diagnostics.Where(d => d.Severity != Severity.Info), Is.Empty, string.Join("\n", converted.Diagnostics));

        string language = file.EndsWith(".vb", StringComparison.Ordinal) ? "VB" : "C#";
        string before = SampleProgram.Run(program, language, source.ConnectionString);
        string afterRun = SampleProgram.Run(converted.NewText, language, target.ConnectionString);
        Assert.Multiple(() =>
        {
            Assert.That(before, Is.EqualTo("id 4;banana=0.80;apple=1.20;kiwi=2.50;"));
            Assert.That(afterRun, Is.EqualTo(before), converted.NewText);
        });
    }
}
