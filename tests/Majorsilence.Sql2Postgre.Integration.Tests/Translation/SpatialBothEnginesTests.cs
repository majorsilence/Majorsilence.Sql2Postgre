using System.Globalization;
using Majorsilence.Sql2Postgre.Data;
using Majorsilence.Sql2Postgre.Integration.Tests.Infrastructure;
using Majorsilence.Sql2Postgre.Model;
using Majorsilence.Sql2Postgre.Postgres;
using Majorsilence.Sql2Postgre.SqlServer;
using Majorsilence.Sql2Postgre.Translation.Semantics;

namespace Majorsilence.Sql2Postgre.Integration.Tests.Translation;

/// <summary>
/// Spatial methods with PostGIS on both engines: a grid of points, lines and polygons as geography
/// (WGS 84) and geometry, migrated to PostGIS columns through the EWKB data copy and verified, then
/// each method compared row for row. Numbers agree to 1e-6 relative: SQL Server's and PostGIS's
/// geodesic computations on the WGS 84 ellipsoid differ by about 1e-7 (BACKLOG).
/// </summary>
[Category("Containers")]
public class SpatialBothEnginesTests
{
    private const string Script = """
        CREATE TABLE dbo.Geo (Id int NOT NULL PRIMARY KEY, Name nvarchar(20) NOT NULL, G geography NULL, P geometry NULL);
        GO
        INSERT INTO dbo.Geo VALUES
            (1, N'seattle', geography::Point(47.6062, -122.3321, 4326), geometry::STGeomFromText('POINT(3 4)', 0)),
            (2, N'portland', geography::Point(45.5152, -122.6784, 4326), geometry::STGeomFromText('LINESTRING(0 0, 3 4, 6 0)', 0)),
            (3, N'route', geography::STGeomFromText('LINESTRING(-122.3321 47.6062, -122.6784 45.5152, -74.006 40.7128)', 4326),
                geometry::STGeomFromText('POLYGON((0 0, 10 0, 10 10, 0 10, 0 0))', 0)),
            (4, N'box', geography::STGeomFromText('POLYGON((-123 45, -121 45, -121 48, -123 48, -123 45))', 4326),
                geometry::STGeomFromText('POLYGON((0 0, 10 0, 10 10, 0 10, 0 0), (2 2, 2 3, 3 3, 2 2))', 0)),
            (5, N'nyc', geography::STGeomFromText('POINT(-74.006 40.7128)', 4326), geometry::STGeomFromText('MULTIPOINT((1 2), (30 40))', 0)),
            (6, N'empty', NULL, NULL);
        GO
        CREATE SPATIAL INDEX SIX_Geo_G ON dbo.Geo (G);
        GO
        CREATE PROCEDURE dbo.Nearby @lat float, @long float, @metres float AS
        BEGIN
            DECLARE @here geography = geography::Point(@lat, @long, 4326);
            SELECT Id, Name, G.STDistance(@here) AS Metres
            FROM dbo.Geo
            WHERE G.STDistance(@here) <= @metres
            ORDER BY Metres, Id;
        END;
        GO
        """;

    private static readonly ConversionOptions Options = new() { Extensions = new HashSet<string> { PermittedExtensions.PostGis } };
    private SqlServerDatabase _source = null!;
    private PostgresDatabase _target = null!;
    private SemanticModel _model = null!;

    [OneTimeSetUp]
    public async Task Migrate()
    {
        _source = await SqlServerDatabase.CreateAsync(Script, "spatial");
        var db = (await SqlServerCatalogReader.ReadAsync(_source.ConnectionString)).Database;
        _model = SemanticModel.Build(db);
        _target = await PostgresDatabase.CreateAsync("spatial", await Engines.PostGisAsync());
        var schema = SchemaConverter.Convert(db, Options);
        Assert.That(schema.Objects.Select(o => $"{o.Source} {o.Status}"), Has.None.EndsWith(" Manual"),
            string.Join("\n", schema.Objects.SelectMany(o => o.Diagnostics).Select(d => d.ToString())));
        var before = await PgApplier.ApplyAsync(schema.Objects.Where(o => o.Phase <= Phase.Tables), _target.ConnectionString);
        Assert.That(before.Failed, Is.Zero, string.Join("\n", before.Outcomes.Where(o => o.Status == ApplyStatus.Failed).Select(o => $"{o.TargetName}: {o.Message}")));
        var copy = await DataCopier.CopyAsync(db, Options, _source.ConnectionString, _target.ConnectionString);
        Assert.That(copy.Succeeded, Is.True, string.Join("\n", copy.Tables.Where(t => t.Error is not null).Select(t => $"{t.Table}: {t.Error}")));
        var after = await PgApplier.ApplyAsync(schema.Objects.Where(o => o.Phase > Phase.Tables), _target.ConnectionString);
        Assert.That(after.Failed, Is.Zero, string.Join("\n", after.Outcomes.Where(o => o.Status == ApplyStatus.Failed).Select(o => $"{o.TargetName}: {o.Message}")));
        var verify = await DataVerifier.VerifyAsync(db, Options, _source.ConnectionString, _target.ConnectionString, VerifyMode.Checksum);
        Assert.That(verify.Passed, Is.True, string.Join("\n", verify.Tables.Where(t => !t.Passed)
            .Select(t => $"{t.Table}: {t.SourceRows}/{t.TargetRows} rows, checksum {t.ChecksumMatches}; {string.Join("; ", t.Diagnostics.Select(d => d.Message))}")));
    }

    [OneTimeTearDown]
    public async Task Drop()
    {
        if (_source is not null)
            await _source.DisposeAsync();
        if (_target is not null)
            await _target.DisposeAsync();
    }

    // geography
    [TestCase("SELECT Id, G.STAsText() AS wkt, G.ToString() AS s, G.STSrid AS srid, G.STGeometryType() AS kind, G.STNumPoints() AS n FROM dbo.Geo ORDER BY Id")]
    [TestCase("SELECT Id, G.Lat AS lat, G.Long AS long FROM dbo.Geo WHERE G.STGeometryType() = 'Point' ORDER BY Id")]
    [TestCase("SELECT a.Id AS a, b.Id AS b, a.G.STDistance(b.G) AS metres FROM dbo.Geo AS a JOIN dbo.Geo AS b ON a.Id < b.Id ORDER BY a.Id, b.Id")]
    [TestCase("SELECT Id, G.STLength() AS len, G.STArea() AS area FROM dbo.Geo ORDER BY Id")]
    [TestCase("SELECT a.Id AS a, b.Id AS b, a.G.STIntersects(b.G) AS i, a.G.STDisjoint(b.G) AS d FROM dbo.Geo AS a JOIN dbo.Geo AS b ON a.Id <> b.Id WHERE a.Id < 6 AND b.Id < 6 ORDER BY a.Id, b.Id")]
    // ST_Covers/ST_CoveredBy count a point on the boundary as inside (S2P3031): the route's end points are left out.
    [TestCase("SELECT a.Id AS a, b.Id AS b, a.G.STContains(b.G) AS c, a.G.STWithin(b.G) AS w FROM dbo.Geo AS a JOIN dbo.Geo AS b ON a.Id <> b.Id WHERE a.Id IN (1, 2, 4, 5) AND b.Id IN (1, 2, 4, 5) ORDER BY a.Id, b.Id")]
    [TestCase("SELECT Id FROM dbo.Geo WHERE G.STIntersects(geography::STGeomFromText('POLYGON((-123 45, -121 45, -121 48, -123 48, -123 45))', 4326)) = 1 ORDER BY Id")]
    [TestCase("SELECT Id, G.STDistance(geography::Point(47.6062, -122.3321, 4326)) AS d FROM dbo.Geo ORDER BY Id")]
    [TestCase("SELECT Id, G.STStartPoint().STAsText() AS s, G.STEndPoint().STAsText() AS e, G.STPointN(2).Lat AS lat2 FROM dbo.Geo WHERE Id = 3")]
    [TestCase("SELECT Id, G.STAsBinary() AS wkb FROM dbo.Geo WHERE Id IN (1, 2, 5) ORDER BY Id")]
    [TestCase("SELECT geography::Parse('POINT(-122.3321 47.6062)').STSrid AS srid, geography::Point(1, 2, 4326).Long AS x, geography::STPointFromText('POINT(1 2)', 4326).Lat AS y")]
    // geometry
    [TestCase("SELECT Id, P.STAsText() AS wkt, P.STGeometryType() AS kind, P.STNumPoints() AS n, P.STDimension() AS dim, P.STIsValid() AS valid, P.STSrid AS srid FROM dbo.Geo ORDER BY Id")]
    [TestCase("SELECT Id, P.STArea() AS area, P.STLength() AS len FROM dbo.Geo ORDER BY Id")]
    [TestCase("SELECT Id, P.STX AS x, P.STY AS y FROM dbo.Geo WHERE P.STGeometryType() = 'Point'")]
    [TestCase("SELECT a.Id AS a, b.Id AS b, a.P.STDistance(b.P) AS d, a.P.STIntersects(b.P) AS i, a.P.STContains(b.P) AS c, a.P.STWithin(b.P) AS w, a.P.STTouches(b.P) AS t, a.P.STEquals(b.P) AS e FROM dbo.Geo AS a JOIN dbo.Geo AS b ON a.Id <> b.Id WHERE a.Id < 6 AND b.Id < 6 ORDER BY a.Id, b.Id")]
    [TestCase("SELECT Id, P.STEnvelope().STAsText() AS box, P.STCentroid().STX AS cx, P.STCentroid().STY AS cy, P.STConvexHull().STArea() AS hull FROM dbo.Geo WHERE Id IN (2, 3, 4) ORDER BY Id")]
    [TestCase("SELECT Id, P.STIntersection(geometry::STGeomFromText('POLYGON((5 5, 15 5, 15 15, 5 15, 5 5))', 0)).STArea() AS i, P.STUnion(geometry::STGeomFromText('POLYGON((5 5, 15 5, 15 15, 5 15, 5 5))', 0)).STArea() AS u, P.STDifference(geometry::STGeomFromText('POLYGON((5 5, 15 5, 15 15, 5 15, 5 5))', 0)).STArea() AS d FROM dbo.Geo WHERE Id IN (3, 4) ORDER BY Id")]
    [TestCase("SELECT Id, P.STBuffer(1).STArea() AS area, P.STBuffer(1).STNumPoints() AS n FROM dbo.Geo WHERE Id = 1")]
    [TestCase("SELECT Id, P.STStartPoint().ToString() AS s, P.STEndPoint().ToString() AS e, P.STPointN(2).ToString() AS p2, P.STIsClosed() AS closed FROM dbo.Geo WHERE Id = 2")]
    [TestCase("SELECT Id FROM dbo.Geo WHERE P.STContains(geometry::STGeomFromText('POINT(1 1)', 0)) = 1 OR P.STIntersects(geometry::Parse('LINESTRING(20 20, 40 40)')) = 1 ORDER BY Id")]
    [TestCase("SELECT Id, P.STAsBinary() AS wkb FROM dbo.Geo WHERE Id IN (1, 2) ORDER BY Id")]
    [TestCase("SELECT geometry::Point(3, 4, 0).STDistance(geometry::Point(0, 0, 0)) AS d, geometry::Parse('POINT(1 2)').STSrid AS srid, geometry::STGeomFromText('MULTIPOINT((1 2), (3 4))', 0).STNumGeometries() AS n")]
    [TestCase("EXEC dbo.Nearby 47.6, -122.3, 300000")]
    public async Task SameResults(string tsql)
    {
        var translation = ScriptTranslator.Translate(tsql, _model, Options);
        Assert.That(translation.Diagnostics.Where(d => d.Severity == Severity.Error), Is.Empty, translation.Sql);
        var expected = await BothEngines.RunSqlServerAsync(tsql, connectionString: _source.ConnectionString);
        var actual = await BothEngines.RunPostgresAsync(translation.Sql, connectionString: _target.ConnectionString);
        Assert.That(actual.Rows, Has.Count.EqualTo(expected.Rows.Count), translation.Sql);
        for (int r = 0; r < expected.Rows.Count; r++)
            for (int col = 0; col < expected.Rows[r].Count; col++)
                Assert.That(Close(actual.Rows[r][col], expected.Rows[r][col]), Is.True,
                    $"row {r}, column {expected.Columns[col]}: PostgreSQL {actual.Rows[r][col]}, SQL Server {expected.Rows[r][col]}\n{translation.Sql}");
    }

    /// <summary>The same value, or two numbers within 1e-6 of each other relative to the larger.</summary>
    private static bool Close(string actual, string expected)
    {
        if (actual == expected)
            return true;
        if (!double.TryParse(actual, NumberStyles.Float, CultureInfo.InvariantCulture, out double a)
            || !double.TryParse(expected, NumberStyles.Float, CultureInfo.InvariantCulture, out double e))
            return false;
        return Math.Abs(a - e) <= Math.Max(1e-9, 1e-6 * Math.Max(Math.Abs(a), Math.Abs(e)));
    }
}
