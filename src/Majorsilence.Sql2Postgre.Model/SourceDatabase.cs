namespace Majorsilence.Sql2Postgre.Model;

/// <summary>A schema-qualified SQL Server name, as SQL Server spells it (not yet mapped).</summary>
public sealed record ObjectName(string Schema, string Name)
{
    public override string ToString() => $"{Schema}.{Name}";
}

public enum ObjectKind { Schema, Table, View, Function, Procedure, Trigger, Sequence, UserType, Index, Constraint, Synonym, Extension, Collation }

/// <summary>A SQL Server column, parameter or expression type.</summary>
/// <param name="Name">Base type name in lower case: <c>nvarchar</c>, <c>decimal</c>, <c>datetime2</c>…</param>
/// <param name="Length">Characters for character types, bytes for binary types; <c>-1</c> is <c>max</c>.</param>
/// <param name="UserTypeName">The alias type a column was declared with, when it was.</param>
public sealed record SqlServerType(string Name, int? Length = null, int? Precision = null, int? Scale = null,
    string? UserTypeName = null, string? Collation = null)
{
    public bool IsMax => Length == -1;

    public override string ToString()
    {
        if (Length is int n && Name is "char" or "varchar" or "nchar" or "nvarchar" or "binary" or "varbinary")
            return $"{Name}({(n == -1 ? "max" : n.ToString(System.Globalization.CultureInfo.InvariantCulture))})";
        if (Precision is int p && Name is "decimal" or "numeric")
            return $"{Name}({p},{Scale ?? 0})";
        if (Scale is int s && Name is "datetime2" or "time" or "datetimeoffset")
            return $"{Name}({s})";
        return Name;
    }
}

public sealed record IdentitySpec(long Seed, long Increment);

public sealed record ComputedSpec(string Expression, bool IsPersisted);

public sealed record Column(
    string Name, int Ordinal, SqlServerType Type, bool IsNullable,
    IdentitySpec? Identity = null, string? DefaultExpression = null, string? DefaultConstraintName = null,
    ComputedSpec? Computed = null, bool IsRowGuidCol = false, bool IsSparse = false);

public sealed record IndexColumn(string Name, bool Descending = false);

/// <param name="Name">The constraint name, or null when SQL Server generated it (<c>PK__…</c>).</param>
public sealed record PrimaryKey(string? Name, IReadOnlyList<IndexColumn> Columns, bool IsClustered);

/// <param name="Name">The constraint name, or null when SQL Server generated it.</param>
public sealed record UniqueConstraint(string? Name, IReadOnlyList<IndexColumn> Columns);

public enum ReferentialAction { NoAction, Cascade, SetNull, SetDefault }

/// <param name="Name">The constraint name, or null when SQL Server generated it (<c>FK__…</c>).</param>
public sealed record ForeignKey(string? Name, IReadOnlyList<string> Columns, ObjectName Referenced,
    IReadOnlyList<string> ReferencedColumns, ReferentialAction OnDelete = ReferentialAction.NoAction,
    ReferentialAction OnUpdate = ReferentialAction.NoAction, bool IsDisabled = false, bool IsNotTrusted = false);

/// <param name="Name">The constraint name, or null when SQL Server generated it (<c>CK__…</c>).</param>
public sealed record CheckConstraint(string? Name, string Expression, bool IsDisabled = false, bool IsNotTrusted = false);

/// <summary>
/// An index that is not the table's primary key or a unique constraint. Named <c>TableIndex</c>
/// rather than the plan's <c>Index</c> so it never collides with <see cref="System.Index"/>,
/// which implicit usings bring into every file.
/// </summary>
/// <param name="Type"><c>rowstore</c>, <c>columnstore</c>, <c>xml</c>, <c>spatial</c> or <c>fulltext</c>.</param>
public sealed record TableIndex(string Name, IReadOnlyList<IndexColumn> Keys, IReadOnlyList<string> Included,
    bool IsUnique, bool IsClustered, string? FilterExpression, string Type = "rowstore");

public sealed record Table(ObjectName Name, IReadOnlyList<Column> Columns,
    PrimaryKey? PrimaryKey, IReadOnlyList<UniqueConstraint> Uniques,
    IReadOnlyList<ForeignKey> ForeignKeys, IReadOnlyList<CheckConstraint> Checks,
    IReadOnlyList<TableIndex> Indexes, long? ApproximateRowCount = null);

/// <param name="CurrentValue">The last value the sequence handed out; null when it has never been used.</param>
public sealed record Sequence(ObjectName Name, SqlServerType Type, long Start, long Increment,
    long? Min, long? Max, bool Cycle, long? CurrentValue = null);

/// <summary>An alias type (<c>CREATE TYPE … FROM</c>) or a table type (<c>CREATE TYPE … AS TABLE</c>).</summary>
public sealed record UserType(ObjectName Name, SqlServerType BaseType, bool IsNullable, bool IsTableType,
    IReadOnlyList<Column> TableColumns);

/// <summary>A view, function, procedure or trigger, with its full <c>CREATE</c> text.</summary>
/// <param name="Definition">The text from <c>sys.sql_modules</c>; empty for an encrypted module.</param>
/// <param name="ParentTable">The table or view a trigger is on.</param>
public sealed record Module(ObjectName Name, ObjectKind Kind, string Definition, ObjectName? ParentTable = null);

/// <summary>
/// <paramref name="From"/> references <paramref name="To"/>. A record rather than the plan's
/// tuple so it serializes to JSON (System.Text.Json ignores tuple fields).
/// </summary>
public sealed record ObjectDependency(ObjectName From, ObjectName To);

public sealed record SourceDatabase(string Name, string DefaultCollation, int CompatibilityLevel,
    IReadOnlyList<string> Schemas, IReadOnlyList<Table> Tables, IReadOnlyList<Sequence> Sequences,
    IReadOnlyList<UserType> UserTypes, IReadOnlyList<Module> Modules,
    IReadOnlyList<ObjectDependency> Dependencies);
