using Majorsilence.Sql2Postgre.Model;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace Majorsilence.Sql2Postgre.Translation.Semantics;

/// <summary>
/// A geography or geometry method or property: its lower-case name, the type it is called on
/// (<c>geography</c> or <c>geometry</c>), and its target (null for a static method such as
/// <c>geography::Point</c>).
/// </summary>
public sealed record SpatialCall(string Method, string Type, ScalarExpression? Target)
{
    public bool IsGeography => Type == "geography";
}

/// <summary>Spatial method calls and properties, and the types they return.</summary>
public sealed partial class SemanticModel
{
    /// <summary>The properties (no parentheses) of geography and geometry values.</summary>
    private static readonly HashSet<string> SpatialProperties = new(StringComparer.OrdinalIgnoreCase) { "lat", "long", "stx", "sty", "z", "m", "stsrid" };

    public static bool IsSpatialType(string? name) =>
        name is not null && (name.Equals("geography", StringComparison.OrdinalIgnoreCase) || name.Equals("geometry", StringComparison.OrdinalIgnoreCase));

    /// <summary><c>x.STDistance(y)</c>, <c>@g.STArea()</c>, <c>geography::Point(…)</c>: a call on a spatial type, or null.</summary>
    public SpatialCall? SpatialCall(FunctionCall f, Scope scope, string? contextSchema)
    {
        string method = f.FunctionName.Value.ToLowerInvariant();
        if (f.CallTarget is UserDefinedTypeCallTarget { SchemaObjectName.BaseIdentifier.Value: var type } && IsSpatialType(type))
            return new SpatialCall(method, type.ToLowerInvariant(), null);
        ScalarExpression? target = f.CallTarget switch
        {
            MultiPartIdentifierCallTarget mp => new ColumnReferenceExpression { MultiPartIdentifier = mp.MultiPartIdentifier },
            ExpressionCallTarget e => e.Expression,
            _ => null,
        };
        return target is not null && TypeOf(target, scope, contextSchema)?.Name is { } t && IsSpatialType(t)
            ? new SpatialCall(method, t, target)
            : null;
    }

    /// <summary>
    /// <c>t.Location.Lat</c>, <c>Location.STX</c> (a column reference whose last part is a property of
    /// the spatial column before it) or <c>@g.Long</c>: a spatial property, or null.
    /// </summary>
    public SpatialCall? SpatialProperty(ScalarExpression e, Scope scope, string? contextSchema)
    {
        switch (e)
        {
            case ColumnReferenceExpression { ColumnType: Microsoft.SqlServer.TransactSql.ScriptDom.ColumnType.Regular, MultiPartIdentifier.Identifiers: { Count: >= 2 } ids }
                when SpatialProperties.Contains(ids[^1].Value):
            {
                var parts = ids.Select(i => i.Value).ToList();
                if (scope.ResolveColumn(parts) is not null)
                    return null;
                if (scope.ResolveColumn(parts[..^1]) is not { Column.Type.Name: var type } || !IsSpatialType(type))
                    return null;
                var target = new MultiPartIdentifier();
                foreach (var id in ids.Take(ids.Count - 1))
                    target.Identifiers.Add(id);
                return new SpatialCall(ids[^1].Value.ToLowerInvariant(), type, new ColumnReferenceExpression { MultiPartIdentifier = target });
            }
            case UserDefinedTypePropertyAccess { CallTarget: ExpressionCallTarget target } p
                when TypeOf(target.Expression, scope, contextSchema)?.Name is { } t && IsSpatialType(t):
                return new SpatialCall(p.PropertyName.Value.ToLowerInvariant(), t, target.Expression);
            default:
                return null;
        }
    }

    /// <summary>What a spatial method or property returns, as SQL Server types it.</summary>
    public static SqlServerType SpatialReturnType(SpatialCall call) => call.Method switch
    {
        "stdistance" or "starea" or "stlength" or "stx" or "sty" or "lat" or "long" or "z" or "m" => new SqlServerType("float", Precision: 53),
        "stintersects" or "stcontains" or "stwithin" or "stequals" or "stdisjoint" or "sttouches" or "stoverlaps" or "stcrosses"
            or "stisvalid" or "stisempty" or "stisclosed" => new SqlServerType("bit"),
        "stastext" or "tostring" or "stgeometrytype" => new SqlServerType("nvarchar", -1),
        "stasbinary" => new SqlServerType("varbinary", -1),
        "stsrid" or "stnumpoints" or "stdimension" or "stnumgeometries" => new SqlServerType("int"),
        _ => new SqlServerType(call.Type),
    };
}
