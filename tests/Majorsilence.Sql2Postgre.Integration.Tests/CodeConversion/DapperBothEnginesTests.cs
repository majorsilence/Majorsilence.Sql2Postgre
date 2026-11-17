using Majorsilence.Sql2Postgre.CodeConversion;
using Majorsilence.Sql2Postgre.Data;
using Majorsilence.Sql2Postgre.Integration.Tests.Infrastructure;
using Majorsilence.Sql2Postgre.Model;
using Majorsilence.Sql2Postgre.Postgres;
using Majorsilence.Sql2Postgre.SqlServer;
using Majorsilence.Sql2Postgre.Translation.Semantics;

namespace Majorsilence.Sql2Postgre.Integration.Tests.CodeConversion;

/// <summary>
/// Dapper and Dapper.Contrib after migrating the database and converting the code: queries
/// mapped to classes, Execute with an anonymous object, a procedure converted to a function, and
/// Contrib's Get, GetAll, Insert, Update and Delete on a <c>[Table("dbo.…")]</c> class.
/// </summary>
[Category("Containers")]
public class DapperBothEnginesTests
{
    private const string Schema = """
        CREATE TABLE dbo.Product (ProductId int IDENTITY(1, 1) NOT NULL PRIMARY KEY, Name nvarchar(50) NOT NULL, Price decimal(10, 2) NOT NULL, InStock bit NOT NULL);
        GO
        INSERT INTO dbo.Product (Name, Price, InStock) VALUES (N'apple', 1.20, 1), (N'banana', 0.80, 0), (N'cheese', 12.00, 1);
        GO
        CREATE PROCEDURE dbo.ProductsUnder @MaxPrice decimal(10, 2) AS
            SELECT ProductId, Name, Price, InStock FROM dbo.Product WHERE Price <= @MaxPrice;
        GO
        """;

    private const string Program = """
        using System.Data;
        using System.Globalization;
        using System.Linq;
        using System.Text;
        using Dapper;
        using Dapper.Contrib.Extensions;
        using Microsoft.Data.SqlClient;

        [Table("dbo.Product")]
        public sealed class Product
        {
            [Key]
            public int ProductId { get; set; }
            public string Name { get; set; } = "";
            public decimal Price { get; set; }
            public bool InStock { get; set; }
        }

        // Property names equal to the converted (lower-case) columns: Contrib's Insert, Update and Delete work on both engines.
        [Table("dbo.Product")]
        public sealed class product_row
        {
            [Key]
            public int productid { get; set; }
            public string name { get; set; } = "";
            public decimal price { get; set; }
            public bool instock { get; set; }
        }

        public static class Sample
        {
            public static string Run(string connectionString)
            {
                var output = new StringBuilder();
                using var connection = new SqlConnection(connectionString);
                connection.Open();

                foreach (var p in connection.Query<Product>("SELECT TOP 2 * FROM dbo.Product WHERE InStock = 1 ORDER BY Price DESC"))
                    output.Append(p.Name).Append(';');
                output.Append("cheap ").Append(string.Join(",", connection.Query<Product>("dbo.ProductsUnder", new { MaxPrice = 2m }, commandType: CommandType.StoredProcedure)
                    .OrderBy(p => p.Name).Select(p => p.Name))).Append(';');
                output.Append("updated ").Append(connection.Execute("UPDATE dbo.Product SET InStock = 1 WHERE Name = @name", new { name = "banana" })).Append(';');

                long id = connection.Insert(new product_row { name = "kiwi", price = 2.50m, instock = true });
                var kiwi = connection.Get<Product>(id);
                output.Append("inserted ").Append(kiwi.Name).Append(' ').Append(kiwi.Price.ToString("0.00", CultureInfo.InvariantCulture)).Append(';');
                var row = connection.Get<product_row>(id);
                row.price = 3.00m;
                output.Append("update ").Append(connection.Update(row)).Append(';');
                output.Append("all ").Append(connection.GetAll<Product>().Count()).Append(';');
                output.Append("delete ").Append(connection.Delete(row)).Append(';');
                return output.ToString();
            }
        }
        """;

    [Test]
    public async Task SameOutputAfterConvertingDatabaseAndCode()
    {
        var options = new ConversionOptions { Extensions = new HashSet<string>() };
        await using var source = await SqlServerDatabase.CreateAsync(Schema, "dapper");
        await using var target = await PostgresDatabase.CreateAsync("dapper");
        var db = (await SqlServerCatalogReader.ReadAsync(source.ConnectionString)).Database;
        var schema = SchemaConverter.Convert(db, options);
        await PgApplier.ApplyAsync(schema.Objects.Where(o => o.Phase <= Phase.Tables), target.ConnectionString);
        Assert.That((await DataCopier.CopyAsync(db, options, source.ConnectionString, target.ConnectionString)).Succeeded, Is.True);
        Assert.That((await PgApplier.ApplyAsync(schema.Objects.Where(o => o.Phase > Phase.Tables), target.ConnectionString)).Failed, Is.Zero);

        var converted = new CodeConverter(new CodeConversionOptions(options, SemanticModel.Build(db))).ConvertFile("Sample.cs", Program);
        Assert.Multiple(() =>
        {
            Assert.That(converted.Diagnostics.Where(d => d.Severity == Severity.Error), Is.Empty, string.Join("\n", converted.Diagnostics));
            // Product's PascalCase properties are reported; product_row's lower-case ones are not.
            Assert.That(converted.Diagnostics.Where(d => d.Code == DiagnosticCodes.DapperContribColumns).Select(d => d.Span!.Line), Is.EqualTo(new[] { 10 }));
            Assert.That(converted.NewText, Does.Contain("[Table(\"public.product\")]"));
        });

        var references = new[] { typeof(Dapper.SqlMapper).Assembly, typeof(Dapper.Contrib.Extensions.SqlMapperExtensions).Assembly };
        string before = SampleProgram.Run(Program, "C#", source.ConnectionString, references);
        string after = SampleProgram.Run(converted.NewText, "C#", target.ConnectionString, references);
        Assert.Multiple(() =>
        {
            Assert.That(before, Is.EqualTo("cheese;apple;cheap apple,banana;updated 1;inserted kiwi 2.50;update True;all 4;delete True;"));
            Assert.That(after, Is.EqualTo(before), converted.NewText);
        });
    }
}
