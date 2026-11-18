using Majorsilence.Sql2Postgre.Model;
using Majorsilence.Sql2Postgre.Translation.PgAst;
using Majorsilence.Sql2Postgre.Translation.Semantics;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace Majorsilence.Sql2Postgre.Translation.Sql;

/// <summary>
/// geography and geometry methods as PostGIS functions, when the <c>postgis</c> extension is
/// enabled (the columns are then PostGIS types). Without it the methods stay S2P3030. A geography
/// method PostGIS has only for geometry (the type name, the points of a line) works on the value
/// cast to geometry, which keeps longitude and latitude as x and y. Geography methods whose result
/// would come from planar arithmetic on degrees (union, difference, touches) are not mapped.
/// </summary>
public sealed partial class ExpressionTranslator
{
    private PgExpr SpatialMethod(FunctionCall f, SpatialCall call, ExpressionContext c)
    {
        if (!options.AllowsExtension(PermittedExtensions.PostGis))
            throw MethodCall(f);
        c.Extensions.Add(PermittedExtensions.PostGis);
        var args = f.Parameters.Select(p => Scalar(p, c)).ToList();
        void Arity(params int[] allowed)
        {
            if (!allowed.Contains(args.Count))
                throw Untranslatable(f, $"{f.FunctionName.Value} with {args.Count} argument(s)", DiagnosticCodes.SpatialMethod);
        }
        string type = call.Type;
        PgExpr AsType(PgExpr geometry) => call.IsGeography ? new PgCast(geometry, "geography") : geometry;
        if (call.Target is null)
        {
            switch (call.Method)
            {
                case "point":
                    // geography::Point(lat, long, srid); geometry::Point(x, y, srid).
                    Arity(3);
                    var point = call.IsGeography ? Call("ST_MakePoint", args[1], args[0]) : Call("ST_MakePoint", args[0], args[1]);
                    return AsType(Call("ST_SetSRID", point, args[2]));
                case "stgeomfromtext" or "stpointfromtext" or "stlinefromtext" or "stpolyfromtext" or "stmpointfromtext"
                    or "stmlinefromtext" or "stmpolyfromtext" or "stgeomcollfromtext":
                    Arity(2);
                    return AsType(Call("ST_GeomFromText", Text(f.Parameters[0], c), args[1]));
                case "stgeomfromwkb" or "stpointfromwkb" or "stlinefromwkb" or "stpolyfromwkb":
                    Arity(2);
                    return AsType(Call("ST_GeomFromWKB", args[0], args[1]));
                case "parse":
                    // Parse takes WKT with no SRID: 4326 for geography, 0 for geometry.
                    Arity(1);
                    return AsType(Call("ST_GeomFromText", Text(f.Parameters[0], c), new PgLiteral(call.IsGeography ? "4326" : "0")));
                default:
                    throw MethodCall(f);
            }
        }

        var target = Scalar(call.Target, c);
        PgExpr Geometry(PgExpr value) => call.IsGeography ? new PgCast(value, "geometry") : value;
        PgExpr GeometryOnly(string function, params PgExpr[] rest)
        {
            if (call.IsGeography)
                throw Untranslatable(f, $"the geography method {f.FunctionName.Value}, which PostGIS has only for geometry", DiagnosticCodes.SpatialMethod);
            return Call(function, [target, .. rest]);
        }
        switch (call.Method)
        {
            case "stdistance":
                Arity(1);
                return Call("ST_Distance", target, args[0]);
            case "stintersects":
                Arity(1);
                return Call("ST_Intersects", target, args[0]);
            case "stdisjoint":
                Arity(1);
                return new PgUnary("NOT", Call("ST_Intersects", target, args[0]));
            case "stcontains" or "stwithin":
                Arity(1);
                if (call.IsGeography)
                {
                    c.Report(DiagnosticCodes.SpatialResultsDiffer, Severity.Info,
                        $"geography {f.FunctionName.Value} becomes {(call.Method == "stcontains" ? "ST_Covers" : "ST_CoveredBy")}: PostGIS has no geography ST_Contains/ST_Within, and a point on the boundary counts as inside.");
                    return Call(call.Method == "stcontains" ? "ST_Covers" : "ST_CoveredBy", target, args[0]);
                }
                return Call(call.Method == "stcontains" ? "ST_Contains" : "ST_Within", target, args[0]);
            case "stequals":
                Arity(1);
                return Call("ST_Equals", Geometry(target), Geometry(args[0]));
            case "sttouches" or "stoverlaps" or "stcrosses":
                Arity(1);
                return GeometryOnly(call.Method switch { "sttouches" => "ST_Touches", "stoverlaps" => "ST_Overlaps", _ => "ST_Crosses" }, args[0]);
            case "stbuffer":
                Arity(1);
                c.Report(DiagnosticCodes.SpatialResultsDiffer, Severity.Info,
                    "STBuffer becomes ST_Buffer with 32 segments a quarter circle, the 129-point circle SQL Server draws around a point. Buffers of lines and polygons, and geography buffers, place their vertices differently, so their areas differ slightly.");
                return Call("ST_Buffer", target, args[0], PgLiteral.String("quad_segs=32"));
            case "starea":
                Arity(0);
                return Call("ST_Area", target);
            case "stlength":
                // SQL Server's length of a polygon is its perimeter; ST_Length is 0 for one, ST_Perimeter 0 for a line.
                Arity(0);
                return new PgBinary("+", Call("ST_Length", target), Call("ST_Perimeter", target));
            case "stastext" or "tostring":
                Arity(0);
                return Compat("spatial_text", [Geometry(target)], c, f);
            case "stasbinary":
                Arity(0);
                return Call("ST_AsBinary", target);
            case "stgeometrytype":
                // 'ST_Polygon' → 'Polygon', SQL Server's name.
                Arity(0);
                return Call("substr", Call("ST_GeometryType", Geometry(target)), new PgLiteral("4"));
            case "stnumpoints":
                Arity(0);
                return Call("ST_NPoints", Geometry(target));
            case "stnumgeometries":
                Arity(0);
                return Call("ST_NumGeometries", Geometry(target));
            case "stdimension":
                Arity(0);
                return Call("ST_Dimension", Geometry(target));
            case "stisvalid":
                Arity(0);
                return Call("ST_IsValid", Geometry(target));
            case "stisempty":
                Arity(0);
                return Call("ST_IsEmpty", Geometry(target));
            case "stisclosed":
                Arity(0);
                return Call("ST_IsClosed", Geometry(target));
            case "ststartpoint" or "stendpoint":
                Arity(0);
                return AsType(Call(call.Method == "ststartpoint" ? "ST_StartPoint" : "ST_EndPoint", Geometry(target)));
            case "stpointn" or "stgeometryn":
                Arity(1);
                return AsType(Call(call.Method == "stpointn" ? "ST_PointN" : "ST_GeometryN", Geometry(target), args[0]));
            case "stenvelope":
                // ST_Envelope's ring runs clockwise; SQL Server's counter-clockwise, from the same corner.
                Arity(0);
                return Call("ST_ForcePolygonCCW", GeometryOnly("ST_Envelope"));
            case "stcentroid":
                // SQL Server has a centroid only for polygons; NULL for points and lines.
                Arity(0);
                return new PgCase(null, [new PgWhen(new PgBinary("=", GeometryOnly("ST_Dimension"), new PgLiteral("2")), Call("ST_Centroid", target))], null);
            case "stconvexhull":
                Arity(0);
                return GeometryOnly("ST_ConvexHull");
            case "stboundary":
                Arity(0);
                return GeometryOnly("ST_Boundary");
            case "stintersection":
                Arity(1);
                return GeometryOnly("ST_Intersection", args[0]);
            case "stunion":
                Arity(1);
                return GeometryOnly("ST_Union", args[0]);
            case "stdifference":
                Arity(1);
                return GeometryOnly("ST_Difference", args[0]);
            case "stsymdifference":
                Arity(1);
                return GeometryOnly("ST_SymDifference", args[0]);
            case "makevalid":
                Arity(0);
                return GeometryOnly("ST_MakeValid");
            case "reduce":
                // Both simplify with Douglas-Peucker.
                Arity(1);
                return GeometryOnly("ST_Simplify", args[0]);
            default:
                return SpatialPropertyValue(call, target, f) ?? throw MethodCall(f);
        }
    }

    /// <summary>A property (<c>Lat</c>, <c>Long</c>, <c>STX</c>, <c>STY</c>, <c>Z</c>, <c>M</c>, <c>STSrid</c>).</summary>
    private PgExpr SpatialProperty(SpatialCall call, TSqlFragment source, ExpressionContext c)
    {
        if (!options.AllowsExtension(PermittedExtensions.PostGis))
            throw Untranslatable(source, "a spatial property", DiagnosticCodes.SpatialMethod);
        c.Extensions.Add(PermittedExtensions.PostGis);
        return SpatialPropertyValue(call, Scalar(call.Target!, c), source)
            ?? throw Untranslatable(source, "this spatial property", DiagnosticCodes.SpatialMethod);
    }

    private static PgExpr? SpatialPropertyValue(SpatialCall call, PgExpr target, TSqlFragment source)
    {
        PgExpr Geometry() => call.IsGeography ? new PgCast(target, "geometry") : target;
        return call.Method switch
        {
            // geography keeps longitude as x and latitude as y.
            "lat" when call.IsGeography => Call("ST_Y", Geometry()),
            "long" when call.IsGeography => Call("ST_X", Geometry()),
            "stx" when !call.IsGeography => Call("ST_X", target),
            "sty" when !call.IsGeography => Call("ST_Y", target),
            "z" => Call("ST_Z", Geometry()),
            "m" => Call("ST_M", Geometry()),
            "stsrid" => Call("ST_SRID", target),
            "lat" or "long" or "stx" or "sty" => throw Untranslatable(source, $"{call.Method} on {call.Type}", DiagnosticCodes.SpatialMethod),
            _ => null,
        };
    }

    private static PgFunctionCall Call(string name, params PgExpr[] args) => new(name, args);
}
