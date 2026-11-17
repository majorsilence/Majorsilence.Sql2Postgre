using Majorsilence.Sql2Postgre.CodeConversion;
using Majorsilence.Sql2Postgre.Data;
using Majorsilence.Sql2Postgre.Integration.Tests.Infrastructure;
using Majorsilence.Sql2Postgre.Model;
using Majorsilence.Sql2Postgre.Postgres;
using Majorsilence.Sql2Postgre.SqlServer;
using Majorsilence.Sql2Postgre.Translation.Semantics;

namespace Majorsilence.Sql2Postgre.Integration.Tests.CodeConversion;

/// <summary>
/// An EF Core DbContext after migrating the database and converting the code: UseNpgsql, the
/// model named as the converted schema (lower case), column types, a database default, LINQ
/// queries, inserts, updates, deletes and FromSqlRaw give the same results.
/// </summary>
[Category("Containers")]
public class EfCoreBothEnginesTests
{
    private const string Schema = """
        CREATE TABLE dbo.Product (ProductId int IDENTITY(1, 1) NOT NULL PRIMARY KEY, Name nvarchar(50) NOT NULL, Price decimal(10, 2) NOT NULL,
            InStock bit NOT NULL, AddedAt datetime2 NOT NULL DEFAULT SYSDATETIME());
        GO
        INSERT INTO dbo.Product (Name, Price, InStock) VALUES (N'apple', 1.20, 1), (N'banana', 0.80, 0), (N'cheese', 12.00, 1);
        GO
        """;

    private const string Program = """
        using System;
        using System.Globalization;
        using System.Linq;
        using System.Text;
        using Microsoft.EntityFrameworkCore;

        public sealed class Product
        {
            public int ProductId { get; set; }
            public string Name { get; set; } = "";
            public decimal Price { get; set; }
            public bool InStock { get; set; }
            public DateTime AddedAt { get; set; }
        }

        public sealed class ShopContext(string connectionString) : DbContext
        {
            public DbSet<Product> Products => Set<Product>();

            protected override void OnConfiguring(DbContextOptionsBuilder options) =>
                options.UseSqlServer(connectionString, sql => sql.EnableRetryOnFailure());

            protected override void OnModelCreating(ModelBuilder modelBuilder)
            {
                modelBuilder.Entity<Product>(product =>
                {
                    product.ToTable("Product", "dbo");
                    product.HasKey(p => p.ProductId).IsClustered();
                    product.Property(p => p.Name).HasColumnType("nvarchar(50)");
                    product.Property(p => p.Price).HasColumnType("decimal(10, 2)");
                    product.Property(p => p.AddedAt).HasDefaultValueSql("SYSDATETIME()");
                });
            }
        }

        public static class Sample
        {
            public static string Run(string connectionString)
            {
                var output = new StringBuilder();
                using (var db = new ShopContext(connectionString))
                {
                    foreach (var p in db.Products.Where(p => p.InStock).OrderBy(p => p.Price))
                        output.Append(p.Name).Append(';');
                    db.Products.Add(new Product { Name = "kiwi", Price = 2.50m, InStock = true });
                    output.Append("saved ").Append(db.SaveChanges()).Append(';');
                }
                using (var db = new ShopContext(connectionString))
                {
                    var kiwi = db.Products.Single(p => p.Name == "kiwi");
                    output.Append("kiwi ").Append(kiwi.ProductId).Append(' ').Append(kiwi.AddedAt.Year > 2000).Append(';');
                    kiwi.Price = 3.00m;
                    db.SaveChanges();
                    output.Append("cheap ").Append(db.Products.FromSqlRaw("SELECT * FROM dbo.Product WHERE Price < {0}", 3.5m).Count()).Append(';');
                    output.Append("total ").Append(db.Products.Sum(p => p.Price).ToString("0.00", CultureInfo.InvariantCulture)).Append(';');
                    db.Products.Remove(kiwi);
                    output.Append("removed ").Append(db.SaveChanges()).Append(';');
                }
                return output.ToString();
            }
        }
        """;

    [Test]
    public async Task SameOutputAfterConvertingDatabaseAndCode()
    {
        var options = new ConversionOptions { Extensions = new HashSet<string>() };
        await using var source = await SqlServerDatabase.CreateAsync(Schema, "efcore");
        await using var target = await PostgresDatabase.CreateAsync("efcore");
        var db = (await SqlServerCatalogReader.ReadAsync(source.ConnectionString)).Database;
        var schema = SchemaConverter.Convert(db, options);
        await PgApplier.ApplyAsync(schema.Objects.Where(o => o.Phase <= Phase.Tables), target.ConnectionString);
        Assert.That((await DataCopier.CopyAsync(db, options, source.ConnectionString, target.ConnectionString)).Succeeded, Is.True);
        Assert.That((await PgApplier.ApplyAsync(schema.Objects.Where(o => o.Phase > Phase.Tables), target.ConnectionString)).Failed, Is.Zero);

        var converted = new CodeConverter(new CodeConversionOptions(options, SemanticModel.Build(db))).ConvertFile("Sample.cs", Program);
        Assert.That(converted.Diagnostics.Where(d => d.Severity != Severity.Info), Is.Empty, string.Join("\n", converted.Diagnostics));

        var references = new[]
        {
            typeof(Microsoft.EntityFrameworkCore.DbContext).Assembly,
            typeof(Microsoft.EntityFrameworkCore.RelationalDatabaseFacadeExtensions).Assembly,
            typeof(Microsoft.EntityFrameworkCore.SqlServerDbContextOptionsExtensions).Assembly,
            typeof(Microsoft.EntityFrameworkCore.NpgsqlDbContextOptionsBuilderExtensions).Assembly,
        };
        string before = SampleProgram.Run(Program, "C#", source.ConnectionString, references);
        string after = SampleProgram.Run(converted.NewText, "C#", target.ConnectionString, references);
        Assert.Multiple(() =>
        {
            Assert.That(before, Is.EqualTo("apple;cheese;saved 1;kiwi 4 True;cheap 3;total 17.00;removed 1;"));
            Assert.That(after, Is.EqualTo(before), converted.NewText);
        });
    }
}
