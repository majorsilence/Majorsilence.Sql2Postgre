using System.Text;
using Majorsilence.Sql2Postgre.Compat;
using Majorsilence.Sql2Postgre.Integration.Tests.Infrastructure;
using Majorsilence.Sql2Postgre.Model;
using Majorsilence.Sql2Postgre.Postgres;
using Majorsilence.Sql2Postgre.Translation.Names;
using Majorsilence.Sql2Postgre.Translation.Semantics;
using Majorsilence.Sql2Postgre.Translation.Sql;
using Majorsilence.Sql2Postgre.Translation.Types;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace Majorsilence.Sql2Postgre.Integration.Tests.Translation;

/// <summary>
/// What each translated built-in returns on PostgreSQL equals what SQL Server returns, over a grid
/// of values chosen to reach the edges: NULLs, empty and padded strings, negative and zero
/// numbers, month ends, a leap day, year boundaries. The mssql_compat helpers are created on the
/// PostgreSQL side for each query (inside the rolled-back transaction). Strings are lower case:
/// the test SQL Server's collation is case-insensitive, which these functions do not translate.
/// </summary>
[Category("Containers")]
public class FunctionBothEnginesTests
{
    private static readonly (string Name, string SqlServer, string Postgres)[] GridColumns =
    [
        ("id", "int", "integer"), ("s", "nvarchar(50)", "varchar(50)"), ("s2", "nvarchar(50)", "varchar(50)"),
        ("sn", "varchar(10)", "varchar(10)"), ("n", "int", "integer"), ("x", "decimal(10,3)", "numeric(10,3)"),
        ("f", "float", "double precision"), ("d", "datetime2(3)", "timestamp(3)"), ("d2", "datetime2(3)", "timestamp(3)"),
        ("dt", "date", "date"), ("b", "bit", "boolean"), ("j", "nvarchar(200)", "varchar(200)"),
    ];

    private static readonly string?[][] GridRows =
    [
        ["1", "abc", "b", "5", "1", "12.345", "2.5", "2024-01-31 10:15:30.123", "2024-03-01 00:00:00.000", "2024-01-31", "1", "{\"a\":1,\"b\":[1,2]}"],
        ["2", "hello world", "o", "10", "2", "-12.345", "-2.5", "2024-02-29 23:59:59.997", "2023-02-28 01:02:03.000", "2024-02-29", "0", "{\"a\":\"x\"}"],
        ["3", "  pad  ", "", " 7 ", "3", "0.000", "0", "2023-12-31 23:00:00.000", "2024-01-01 01:00:00.000", "2023-12-31", "1", "[1,2,3]"],
        ["4", "", "a", "x1", "-4", "99.999", "10000000000", "2024-06-15 12:00:00.500", "2024-06-16 11:59:59.500", "2024-06-15", "0", "not json"],
        ["5", null, null, null, null, null, null, null, null, null, null, null],
        ["6", "cab", "ab", "0", "0", "5.555", "0.1", "2000-01-01 00:00:00.000", "2099-12-31 23:59:59.999", "2000-01-01", "1", "{}"],
    ];

    private static string GridSetup(bool postgres)
    {
        var sb = new StringBuilder();
        if (postgres)
        {
            sb.Append("CREATE SCHEMA mssql_compat;\n");
            foreach (string name in CompatScripts.CoreNames.Order(StringComparer.Ordinal))
                sb.Append(CompatScripts.Create(name, "mssql_compat")).Append('\n');
        }
        sb.Append($"CREATE TABLE grid ({string.Join(", ", GridColumns.Select(c => $"{c.Name} {(postgres ? c.Postgres : c.SqlServer)}"))});\n");
        foreach (var row in GridRows)
        {
            var values = row.Select((v, i) => v is null ? "NULL"
                : GridColumns[i].Name is "id" or "n" or "x" or "f" ? v
                : GridColumns[i].Name == "b" ? (postgres ? (v == "1" ? "true" : "false") : v)
                : (postgres ? "" : "N") + "'" + v.Replace("'", "''") + "'");
            sb.Append($"INSERT INTO grid VALUES ({string.Join(", ", values)});\n");
        }
        return sb.ToString();
    }

    private static string Translate(string tsql)
    {
        var fragment = new TSql160Parser(true).ParseExpression(new StringReader(tsql), out var errors);
        Assert.That(errors, Is.Empty, $"'{tsql}' does not parse");
        var options = new ConversionOptions();
        var names = new NameMapper(options);
        var model = SemanticModel.Build(new SourceDatabase("", "", 160, [], [], [], [], [], []));
        var translator = new ExpressionTranslator(options, names, new TypeMapper(options, names), model);
        var scope = new Scope();
        scope.Bind(new Binding("grid", new RelationInfo(new ObjectName("dbo", "grid"), ObjectKind.Table,
            GridColumns.Select(c => new ColumnInfo(c.Name, Majorsilence.Sql2Postgre.SqlServer.SqlServerScriptReader
                .Read([("c", $"CREATE TABLE t (c {c.SqlServer});")]).Database.Tables[0].Columns[0].Type)).ToList())));
        var context = new ExpressionContext(scope, "dbo", new ObjectName("dbo", "test"));
        return PgWriter.Expression(translator.Scalar(fragment, context));
    }

    private static Task SameOverGrid(string expression) =>
        BothEngines.AssertSameResultAsync($"SELECT id, {expression} AS v FROM grid", $"SELECT id, {Translate(expression)} AS v FROM grid",
            GridSetup(postgres: false), GridSetup(postgres: true));

    // ---- strings ----

    [TestCase("LEN(s)")]
    [TestCase("CHARINDEX('b', s)")]
    [TestCase("CHARINDEX('', s)")]
    [TestCase("CHARINDEX('a', s, 2)")]
    [TestCase("CHARINDEX('a', s, 0)")]
    [TestCase("CHARINDEX(s2, s)")]
    [TestCase("SUBSTRING(s, 2, 3)")]
    [TestCase("SUBSTRING(s, 0, 2)")]
    [TestCase("SUBSTRING(s, -1, 3)")]
    [TestCase("LEFT(s, 2)")]
    [TestCase("RIGHT(s, 2)")]
    [TestCase("REPLACE(s, 'a', 'xy')")]
    [TestCase("REVERSE(s)")]
    [TestCase("REPLICATE(s2, 3)")]
    [TestCase("STUFF(s, 2, 1, 'xy')")]
    [TestCase("UPPER(s)")]
    [TestCase("LOWER(UPPER(s))")]
    [TestCase("LTRIM(s)")]
    [TestCase("RTRIM(s)")]
    [TestCase("TRIM(s)")]
    [TestCase("CONCAT(s, n, s2)")]
    [TestCase("CONCAT_WS('-', s, s2)")]
    [TestCase("s + s2")]
    [TestCase("s + N'!'")]
    [TestCase("ASCII(s)")]
    [TestCase("CHAR(n + 64)")]
    [TestCase("QUOTENAME(s)")]
    [TestCase("SPACE(ABS(n))")]
    public Task Strings(string expression) => SameOverGrid(expression);

    // ---- nulls, logic, conversion ----

    [TestCase("ISNULL(s, 'z')")]
    [TestCase("COALESCE(s, s2, 'z')")]
    [TestCase("NULLIF(s, '')")]
    [TestCase("IIF(n > 0, 'pos', 'not')")]
    [TestCase("CHOOSE(n, 'one', 'two', 'three')")]
    [TestCase("CASE WHEN b = 1 THEN 'yes' WHEN b = 0 THEN 'no' END")]
    [TestCase("ISNULL(b, 0)")]
    [TestCase("CASE WHEN n > 0 THEN b ELSE 2 END")]
    [TestCase("COALESCE(b, 5)")]
    [TestCase("CAST(b AS varchar(5))")]
    [TestCase("CASE WHEN id = 4 THEN -1 WHEN sn = 5 THEN 1 ELSE 0 END")]
    [TestCase("CASE WHEN s LIKE '[a-c]%' THEN 1 ELSE 0 END")]
    [TestCase("CASE WHEN s NOT LIKE '%[^a-z]%' THEN 1 ELSE 0 END")]
    [TestCase("CAST(x AS int)")]
    [TestCase("CAST(f AS bigint)")]
    [TestCase("CAST(n AS varchar(10))")]
    [TestCase("CAST(b AS int)")]
    [TestCase("CAST(n AS bit)")]
    [TestCase("TRY_CAST(sn AS int)")]
    [TestCase("TRY_CONVERT(int, sn)")]
    [TestCase("TRY_CAST(sn AS decimal(10,2))")]
    [TestCase("TRY_CAST(s AS date)")]
    [TestCase("TRY_CAST(s2 AS int)")]
    [TestCase("TRY_CAST(s AS datetime)")]
    public Task Conversions(string expression) => SameOverGrid(expression);

    // ---- numbers ----

    [TestCase("ABS(n)")]
    [TestCase("ABS(x)")]
    [TestCase("CEILING(x)")]
    [TestCase("FLOOR(x)")]
    [TestCase("ROUND(x, 1)")]
    [TestCase("ROUND(x, 1, 1)")]
    [TestCase("ROUND(n, -1)")]
    [TestCase("SIGN(x)")]
    [TestCase("POWER(n, 2)")]
    [TestCase("POWER(2, n)")]
    [TestCase("SQUARE(n)")]
    [TestCase("SQRT(ABS(f))")]
    [TestCase("n % 3")]
    [TestCase("n / 2")]
    [TestCase("x * 2")]
    [TestCase("PI() * 2")]
    public Task Numbers(string expression) => SameOverGrid(expression);

    // ---- dates ----

    [TestCase("DATEADD(day, 1, d)")]
    [TestCase("DATEADD(month, 1, dt)")]
    [TestCase("DATEADD(month, -1, dt)")]
    [TestCase("DATEADD(year, -1, dt)")]
    [TestCase("DATEADD(quarter, 1, dt)")]
    [TestCase("DATEADD(week, 2, dt)")]
    [TestCase("DATEADD(hour, 5, d)")]
    [TestCase("DATEADD(minute, -30, d)")]
    [TestCase("DATEADD(second, 90, d)")]
    [TestCase("DATEADD(day, n, dt)")]
    [TestCase("DATEDIFF(day, d, d2)")]
    [TestCase("DATEDIFF(month, d, d2)")]
    [TestCase("DATEDIFF(year, d, d2)")]
    [TestCase("DATEDIFF(quarter, d, d2)")]
    [TestCase("DATEDIFF(week, d, d2)")]
    [TestCase("DATEDIFF(hour, d, d2)")]
    [TestCase("DATEDIFF(minute, d, d2)")]
    [TestCase("DATEDIFF_BIG(second, d, d2)")]
    [TestCase("DATEDIFF(day, dt, d2)")]
    [TestCase("DATEPART(year, d)")]
    [TestCase("DATEPART(quarter, d)")]
    [TestCase("DATEPART(month, d)")]
    [TestCase("DATEPART(dayofyear, d)")]
    [TestCase("DATEPART(day, d)")]
    [TestCase("DATEPART(weekday, d)")]
    [TestCase("DATEPART(week, d)")]
    [TestCase("DATEPART(hour, d)")]
    [TestCase("DATEPART(minute, d)")]
    [TestCase("DATEPART(second, d)")]
    [TestCase("DATEPART(millisecond, d)")]
    [TestCase("DATEPART(iso_week, dt)")]
    [TestCase("YEAR(dt)")]
    [TestCase("MONTH(dt)")]
    [TestCase("DAY(dt)")]
    [TestCase("DATENAME(month, d)")]
    [TestCase("DATENAME(weekday, d)")]
    [TestCase("DATENAME(year, d)")]
    [TestCase("EOMONTH(dt)")]
    [TestCase("EOMONTH(dt, 1)")]
    [TestCase("EOMONTH(dt, -1)")]
    [TestCase("DATEFROMPARTS(2024, ABS(n) + 1, 1)")]
    [TestCase("CONVERT(varchar(10), d, 120)")]
    [TestCase("CONVERT(varchar(19), d, 120)")]
    [TestCase("CONVERT(varchar(23), d, 121)")]
    [TestCase("CONVERT(varchar, d, 101)")]
    [TestCase("CONVERT(varchar, d, 103)")]
    [TestCase("CONVERT(varchar, d, 104)")]
    [TestCase("CONVERT(varchar, dt, 112)")]
    [TestCase("CONVERT(varchar, d, 108)")]
    [TestCase("CONVERT(varchar, d, 126)")]
    [TestCase("FORMAT(d, 'yyyy-MM-dd HH:mm')")]
    [TestCase("FORMAT(d, 'dd/MM/yyyy')")]
    public Task Dates(string expression) => SameOverGrid(expression);

    [TestCase("FORMAT(x, 'N2')")]
    [TestCase("FORMAT(f, 'N0')")]
    [TestCase("FORMAT(n, 'N0')")]
    public Task Formats(string expression) => SameOverGrid(expression);

    // ---- JSON and hashing ----

    [TestCase("CASE WHEN ISJSON(j) = 1 THEN JSON_VALUE(j, '$.a') END")]
    [TestCase("CASE WHEN ISJSON(j) = 1 THEN JSON_VALUE(j, '$.b[1]') END")]
    [TestCase("CASE WHEN ISJSON(j) = 1 THEN JSON_QUERY(j, '$.b') END")]
    [TestCase("ISJSON(j)")]
    [TestCase("HASHBYTES('SHA2_256', CAST(s AS varchar(50)))")]
    [TestCase("HASHBYTES('MD5', CAST(s AS varchar(50)))")]
    public Task JsonAndHashing(string expression) => SameOverGrid(expression);

    // ---- aggregates and windows ----

    [TestCase("COUNT(*)")]
    [TestCase("COUNT(s)")]
    [TestCase("COUNT(DISTINCT b)")]
    [TestCase("SUM(n)")]
    [TestCase("AVG(n)")]
    [TestCase("AVG(x)")]
    [TestCase("MIN(s)")]
    [TestCase("MAX(d)")]
    [TestCase("STRING_AGG(s2, ',') WITHIN GROUP (ORDER BY id)")]
    [TestCase("STRING_AGG(n, ';') WITHIN GROUP (ORDER BY n DESC)")]
    public Task Aggregates(string expression) =>
        BothEngines.AssertSameResultAsync($"SELECT {expression} AS v FROM grid", $"SELECT {Translate(expression)} AS v FROM grid",
            GridSetup(postgres: false), GridSetup(postgres: true));

    [TestCase("ROW_NUMBER() OVER (ORDER BY n, id)")]
    [TestCase("RANK() OVER (ORDER BY b)")]
    [TestCase("DENSE_RANK() OVER (ORDER BY n)")]
    [TestCase("SUM(n) OVER (PARTITION BY b ORDER BY id)")]
    [TestCase("LAG(n) OVER (ORDER BY id)")]
    [TestCase("LEAD(s, 2, 'none') OVER (ORDER BY id)")]
    [TestCase("COUNT(*) OVER (PARTITION BY b)")]
    [TestCase("SUM(n) OVER (ORDER BY id ROWS BETWEEN 1 PRECEDING AND CURRENT ROW)")]
    public Task Windows(string expression) => SameOverGrid(expression);

    // ---- ISNUMERIC's quirks, as SQL Server answers them ----

    [TestCase("'123'")]
    [TestCase("'-12.5'")]
    [TestCase("'1e5'")]
    [TestCase("'1d2'")]
    [TestCase("'$12'")]
    [TestCase("'+'")]
    [TestCase("'-'")]
    [TestCase("'.'")]
    [TestCase("','")]
    [TestCase("',,1'")]
    [TestCase("'1,000'")]
    [TestCase("'12.'")]
    [TestCase("'.5'")]
    [TestCase("''")]
    [TestCase("' '")]
    [TestCase("'abc'")]
    [TestCase("'1e'")]
    [TestCase("'0x1A'")]
    [TestCase("'  12  '")]
    [TestCase("NULL")]
    public Task IsNumeric(string literal) =>
        BothEngines.AssertSameResultAsync($"SELECT ISNUMERIC({literal}) AS v", $"SELECT {Translate($"ISNUMERIC({literal})")} AS v",
            null, GridSetup(postgres: true));

    [TestCase("ISDATE('2024-02-29')")]
    [TestCase("ISDATE('2023-02-29')")]
    [TestCase("ISDATE('not a date')")]
    [TestCase("ISJSON('[1,2]')")]
    [TestCase("ISJSON('{\"a\":1}')")]
    [TestCase("ISJSON('1')")]
    [TestCase("ISJSON('{bad')")]
    [TestCase("DATEDIFF(year, '2024-12-31', '2025-01-01')")]
    [TestCase("DATEDIFF(day, '2024-03-10 23:59:59', '2024-03-11 00:00:00')")]
    [TestCase("DATEDIFF(week, '2024-11-16', '2024-11-17')")]
    [TestCase("DATEDIFF(millisecond, '2024-01-01 00:00:00.000', '2024-01-01 00:00:01.250')")]
    [TestCase("DATEADD(month, 1, '2024-01-31')")]
    [TestCase("CONVERT(date, '20240229', 112)")]
    [TestCase("CONVERT(date, '29/02/2024', 103)")]
    [TestCase("CONVERT(datetime, '2000-06-01', 101)")]
    [TestCase("CONVERT(datetime, '06/01/2000', 101)")]
    [TestCase("CONVERT(datetime, '2000-06-01 10:20:30', 103)")]
    [TestCase("CONVERT(smalldatetime, '2000-6-13', 110)")]
    [TestCase("CONVERT(date, '2000-06-01T10:20:30', 103)")]
    [TestCase("CONVERT(datetime2, '2000-06-01T10:20:30', 101)")]
    [TestCase("CONVERT(date, '20000601', 101)")]
    [TestCase("CAST(12.5 AS int)")]
    [TestCase("CAST($3.75 AS int)")]
    public Task Constants(string expression) =>
        BothEngines.AssertSameResultAsync($"SELECT {expression} AS v", $"SELECT {Translate(expression)} AS v", null, GridSetup(postgres: true));
}
