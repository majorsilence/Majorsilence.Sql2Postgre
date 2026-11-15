using Majorsilence.Sql2Postgre.Integration.Tests.Infrastructure;
using Majorsilence.Sql2Postgre.Model;
using Majorsilence.Sql2Postgre.SqlServer;

namespace Majorsilence.Sql2Postgre.Integration.Tests.SqlServer;

[Category("Containers")]
public class CatalogReaderTests
{
    private SqlServerDatabase _db = null!;
    private SourceReadResult _read = null!;
    private SourceDatabase Db => _read.Database;

    [OneTimeSetUp]
    public async Task ReadFixture()
    {
        _db = await SqlServerDatabase.CreateAsync(SqlServerDatabase.Fixture("shop.sql"), "shop");
        _read = await SqlServerCatalogReader.ReadAsync(_db.ConnectionString);
    }

    [OneTimeTearDown]
    public async Task Drop()
    {
        if (_db is not null)
            await _db.DisposeAsync();
    }

    private Table T(string name) => Db.Tables.Single(t => t.Name == new ObjectName("shop", name));
    private static Column C(Table t, string name) => t.Columns.Single(c => c.Name == name);

    [Test]
    public void DatabaseProperties()
    {
        Assert.Multiple(() =>
        {
            Assert.That(Db.Name, Is.EqualTo(_db.Name));
            Assert.That(Db.DefaultCollation, Is.Not.Empty);
            Assert.That(Db.CompatibilityLevel, Is.GreaterThanOrEqualTo(160));
            Assert.That(Db.Schemas, Does.Contain("dbo").And.Contain("shop"));
            Assert.That(Db.Schemas, Does.Not.Contain("sys").And.Not.Contain("db_owner"));
        });
    }

    [Test]
    public void TablesAreSortedAndComplete() =>
        Assert.That(Db.Tables.Select(t => t.Name.Name),
            Is.EqualTo(new[] { "all_types", "customer", "order", "order_line", "product", "sales_fact" }));

    [TestCase("c_bit", "bit", null, null, null)]
    [TestCase("c_tinyint", "tinyint", null, null, null)]
    [TestCase("c_decimal", "decimal", null, 18, 4)]
    [TestCase("c_numeric", "numeric", null, 10, 2)]
    [TestCase("c_money", "money", null, null, null)]
    [TestCase("c_float", "float", null, 53, null)]
    [TestCase("c_float24", "real", null, 24, null)]
    [TestCase("c_real", "real", null, 24, null)]
    [TestCase("c_char", "char", 10, null, null)]
    [TestCase("c_varchar", "varchar", 50, null, null)]
    [TestCase("c_varchar_max", "varchar", -1, null, null)]
    [TestCase("c_nchar", "nchar", 10, null, null)]
    [TestCase("c_nvarchar", "nvarchar", 50, null, null)]
    [TestCase("c_nvarchar_max", "nvarchar", -1, null, null)]
    [TestCase("c_text", "text", null, null, null)]
    [TestCase("c_ntext", "ntext", null, null, null)]
    [TestCase("c_binary", "binary", 8, null, null)]
    [TestCase("c_varbinary_max", "varbinary", -1, null, null)]
    [TestCase("c_image", "image", null, null, null)]
    [TestCase("c_date", "date", null, null, null)]
    [TestCase("c_time", "time", null, null, 7)]
    [TestCase("c_time3", "time", null, null, 3)]
    [TestCase("c_datetime", "datetime", null, null, null)]
    [TestCase("c_smalldatetime", "smalldatetime", null, null, null)]
    [TestCase("c_datetime2", "datetime2", null, null, 7)]
    [TestCase("c_datetime2_3", "datetime2", null, null, 3)]
    [TestCase("c_datetimeoffset", "datetimeoffset", null, null, 7)]
    [TestCase("c_uniqueidentifier", "uniqueidentifier", null, null, null)]
    [TestCase("c_xml", "xml", null, null, null)]
    [TestCase("c_sysname", "sysname", null, null, null)]
    [TestCase("c_rowversion", "timestamp", null, null, null)]
    [TestCase("c_sql_variant", "sql_variant", null, null, null)]
    [TestCase("c_hierarchyid", "hierarchyid", null, null, null)]
    [TestCase("c_geography", "geography", null, null, null)]
    [TestCase("c_geometry", "geometry", null, null, null)]
    public void ColumnTypes(string column, string type, int? length, int? precision, int? scale)
    {
        var t = C(T("all_types"), column).Type;
        Assert.Multiple(() =>
        {
            Assert.That(t.Name, Is.EqualTo(type));
            Assert.That(t.Length, Is.EqualTo(length), "length");
            Assert.That(t.Precision, Is.EqualTo(precision), "precision");
            Assert.That(t.Scale, Is.EqualTo(scale), "scale");
            Assert.That(t.UserTypeName, Is.Null);
        });
    }

    [Test]
    public void AliasTypeColumnKeepsBaseTypeAndAlias()
    {
        var t = C(T("all_types"), "c_phone").Type;
        Assert.That(t, Is.EqualTo(new SqlServerType("nvarchar", 20, null, null, "shop.phone", t.Collation)));
    }

    [Test]
    public void ColumnsAreInOrdinalOrder() =>
        Assert.That(T("all_types").Columns.Select(c => c.Ordinal), Is.Ordered.Ascending);

    [Test]
    public void Identity()
    {
        Assert.Multiple(() =>
        {
            Assert.That(C(T("customer"), "customer_id").Identity, Is.EqualTo(new IdentitySpec(100, 5)));
            Assert.That(C(T("order"), "order_id").Identity, Is.EqualTo(new IdentitySpec(1, 1)));
            Assert.That(C(T("customer"), "name").Identity, Is.Null);
        });
    }

    [Test]
    public void DefaultsNamedAndUnnamed()
    {
        var customer = T("customer");
        Assert.Multiple(() =>
        {
            Assert.That(C(customer, "created_at").DefaultExpression, Is.EqualTo("(sysdatetime())"));
            Assert.That(C(customer, "created_at").DefaultConstraintName, Is.EqualTo("df_customer_created"));
            Assert.That(C(customer, "is_active").DefaultExpression, Is.EqualTo("((1))"));
            Assert.That(C(customer, "is_active").DefaultConstraintName, Is.Null, "system-named default");
            Assert.That(C(T("order"), "total").DefaultExpression, Is.EqualTo("((0))"));
        });
    }

    [Test]
    public void ComputedColumns()
    {
        var line = T("order_line");
        Assert.Multiple(() =>
        {
            Assert.That(C(line, "line_total").Computed, Is.EqualTo(new ComputedSpec("([qty]*[unit_price])", true)));
            Assert.That(C(line, "line_tax").Computed!.IsPersisted, Is.False);
            Assert.That(C(line, "qty").Computed, Is.Null);
        });
    }

    [Test]
    public void ColumnFlagsAndCollation()
    {
        var customer = T("customer");
        Assert.Multiple(() =>
        {
            Assert.That(C(customer, "row_guid").IsRowGuidCol, Is.True);
            Assert.That(C(customer, "notes").IsSparse, Is.True);
            Assert.That(C(customer, "name").Type.Collation, Is.EqualTo("Latin1_General_CI_AS"));
            Assert.That(C(customer, "email").IsNullable, Is.True);
            Assert.That(C(customer, "name").IsNullable, Is.False);
        });
    }

    [Test]
    public void PrimaryKeys()
    {
        Assert.Multiple(() =>
        {
            Assert.That(T("all_types").PrimaryKey, Is.EqualTo(T("all_types").PrimaryKey! with { Name = "pk_all_types", IsClustered = true }));
            Assert.That(T("customer").PrimaryKey!.IsClustered, Is.False);
            Assert.That(T("customer").PrimaryKey!.Columns, Is.EqualTo(new[] { new IndexColumn("customer_id") }));
            Assert.That(T("order_line").PrimaryKey!.Columns.Select(c => c.Name), Is.EqualTo(new[] { "order_id", "line_no" }));
            Assert.That(T("order").PrimaryKey!.Name, Is.Null, "system-named");
            Assert.That(T("sales_fact").PrimaryKey, Is.Null);
        });
    }

    [Test]
    public void UniqueConstraints() =>
        Assert.That(T("customer").Uniques.Single(), Has.Property("Name").EqualTo("uq_customer_code")
            .And.Property("Columns").EqualTo(new[] { new IndexColumn("CustomerCode") }));

    [Test]
    public void ForeignKeys()
    {
        var line = T("order_line").ForeignKeys;
        var toOrder = line.Single(f => f.Name == "fk_line_order");
        var toProduct = line.Single(f => f.Name == "fk_line_product");
        Assert.Multiple(() =>
        {
            Assert.That(toOrder.Columns, Is.EqualTo(new[] { "order_id" }));
            Assert.That(toOrder.Referenced, Is.EqualTo(new ObjectName("shop", "order")));
            Assert.That(toOrder.ReferencedColumns, Is.EqualTo(new[] { "order_id" }));
            Assert.That(toOrder.OnDelete, Is.EqualTo(ReferentialAction.Cascade));
            Assert.That(toOrder.OnUpdate, Is.EqualTo(ReferentialAction.Cascade));
            Assert.That(toOrder.IsNotTrusted, Is.False);
            Assert.That(toProduct.OnDelete, Is.EqualTo(ReferentialAction.SetNull));
            Assert.That(toProduct.OnUpdate, Is.EqualTo(ReferentialAction.NoAction));
            Assert.That(toProduct.IsNotTrusted, Is.True, "WITH NOCHECK");
            Assert.That(T("product").ForeignKeys.Single().Referenced, Is.EqualTo(new ObjectName("shop", "product")));
        });
    }

    [Test]
    public void CheckConstraints()
    {
        var email = T("customer").Checks.Single();
        var qty = T("order_line").Checks.Single();
        Assert.Multiple(() =>
        {
            Assert.That(email.Name, Is.EqualTo("ck_customer_email"));
            Assert.That(email.Expression, Is.EqualTo("([email] like '%@%')"));
            Assert.That(email.IsDisabled, Is.True);
            Assert.That(email.IsNotTrusted, Is.True);
            Assert.That(qty.Expression, Is.EqualTo("([qty]>(0))"));
            Assert.That(qty.IsDisabled, Is.False);
        });
    }

    [Test]
    public void Indexes()
    {
        var customer = T("customer").Indexes;
        var clustered = customer.Single(i => i.Name == "ix_customer_name");
        var filtered = customer.Single(i => i.Name == "ix_customer_email_active");
        var columnstore = T("sales_fact").Indexes.Single();
        Assert.Multiple(() =>
        {
            Assert.That(customer, Has.Count.EqualTo(2), "PK and unique constraint indexes are not listed");
            Assert.That(clustered.IsClustered, Is.True);
            Assert.That(clustered.Keys, Is.EqualTo(new[] { new IndexColumn("name") }));
            Assert.That(filtered.Keys, Is.EqualTo(new[] { new IndexColumn("email", Descending: true) }));
            Assert.That(filtered.Included, Is.EqualTo(new[] { "name" }));
            Assert.That(filtered.FilterExpression, Is.EqualTo("([is_active]=(1))"));
            Assert.That(filtered.IsClustered, Is.False);
            Assert.That(columnstore.Type, Is.EqualTo("columnstore"));
            Assert.That(columnstore.Keys.Select(k => k.Name), Is.EqualTo(new[] { "sale_id", "amount" }));
        });
    }

    [Test]
    public void Sequences()
    {
        var s = Db.Sequences.Single(x => x.Name.Name == "invoice_no");
        Assert.That(s, Is.EqualTo(new Sequence(new ObjectName("shop", "invoice_no"), new SqlServerType("bigint"),
            1000, 10, 1000, 999999, false, 1000)));
        // A decimal(38,0) sequence ranges past bigint; its bounds are clamped to bigint's.
        var big = Db.Sequences.Single(x => x.Name.Name == "big_counter");
        Assert.That(big.Max, Is.EqualTo(long.MaxValue));
        Assert.That(big.Type.Name, Is.EqualTo("decimal"));
    }

    [Test]
    public void UserTypes()
    {
        var phone = Db.UserTypes.Single(u => u.Name.Name == "phone");
        var lines = Db.UserTypes.Single(u => u.Name.Name == "line_list");
        Assert.Multiple(() =>
        {
            Assert.That(phone.IsTableType, Is.False);
            Assert.That(phone.BaseType.Name, Is.EqualTo("nvarchar"));
            Assert.That(phone.BaseType.Length, Is.EqualTo(20));
            Assert.That(phone.IsNullable, Is.True);
            Assert.That(lines.IsTableType, Is.True);
            Assert.That(lines.TableColumns.Select(c => c.Name), Is.EqualTo(new[] { "product_id", "qty" }));
            Assert.That(lines.TableColumns.All(c => !c.IsNullable), Is.True);
        });
    }

    [Test]
    public void Modules()
    {
        var byName = Db.Modules.ToDictionary(m => m.Name.Name);
        Assert.Multiple(() =>
        {
            Assert.That(byName["active_customer"].Kind, Is.EqualTo(ObjectKind.View));
            Assert.That(byName["active_customer"].Definition, Does.StartWith("CREATE VIEW shop.active_customer"));
            Assert.That(byName["line_amount"].Kind, Is.EqualTo(ObjectKind.Function));
            Assert.That(byName["customer_orders"].Kind, Is.EqualTo(ObjectKind.Function));
            Assert.That(byName["add_order"].Kind, Is.EqualTo(ObjectKind.Procedure));
            Assert.That(byName["trg_order_line_total"].Kind, Is.EqualTo(ObjectKind.Trigger));
            Assert.That(byName["trg_order_line_total"].ParentTable, Is.EqualTo(new ObjectName("shop", "order_line")));
            Assert.That(byName["secret_proc"].Definition, Is.Empty);
        });
    }

    [Test]
    public void EncryptedModuleIsReported() =>
        Assert.That(_read.Diagnostics.Single(), Has.Property("Code").EqualTo(DiagnosticCodes.EncryptedModule)
            .And.Property("Object").EqualTo(new ObjectName("shop", "secret_proc"))
            .And.Property("Severity").EqualTo(Severity.Error));

    [Test]
    public void Dependencies()
    {
        Assert.That(Db.Dependencies, Does.Contain(new ObjectDependency(new ObjectName("shop", "active_customer"), new ObjectName("shop", "customer"))));
        Assert.That(Db.Dependencies, Does.Contain(new ObjectDependency(new ObjectName("shop", "customer_orders"), new ObjectName("shop", "order"))));
    }

    [Test]
    public void RowCounts() => Assert.That(T("customer").ApproximateRowCount, Is.EqualTo(0));
}
