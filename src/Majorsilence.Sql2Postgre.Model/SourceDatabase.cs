namespace Majorsilence.Sql2Postgre.Model;

/// <summary>A schema-qualified SQL Server name, as SQL Server spells it (not yet mapped).</summary>
public sealed record ObjectName(string Schema, string Name)
{
    public override string ToString() => $"{Schema}.{Name}";
}

public enum ObjectKind { Schema, Table, View, Function, Procedure, Trigger, Sequence, UserType, Index, Constraint, Synonym, Extension, Collation, Job }

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
/// <param name="FullText">A full-text index's key, languages and stoplist.</param>
public sealed record TableIndex(string Name, IReadOnlyList<IndexColumn> Keys, IReadOnlyList<string> Included,
    bool IsUnique, bool IsClustered, string? FilterExpression, string Type = "rowstore", FullTextIndex? FullText = null);

/// <summary>
/// What a full-text index (<c>CREATE FULLTEXT INDEX ON t (cols) KEY INDEX ix</c>) has beyond its
/// columns, which are the index's <see cref="TableIndex.Keys"/>.
/// </summary>
/// <param name="KeyColumn">The single column of the KEY INDEX, which CONTAINSTABLE returns as KEY; null when unknown.</param>
/// <param name="Columns">Each indexed column with its language and type column.</param>
/// <param name="StopList"><c>SYSTEM</c> (the default), <c>OFF</c>, or the name of a stoplist of the database's own.</param>
public sealed record FullTextIndex(string? KeyColumn, IReadOnlyList<FullTextColumn> Columns, string StopList = FullTextIndex.SystemStopList)
{
    public const string SystemStopList = "SYSTEM";
    public const string NoStopList = "OFF";
}

/// <param name="Language">The column's word-breaker language (an LCID: 1033 is English); null when not given (the server default).</param>
/// <param name="TypeColumn">For a varbinary/image column, the column holding each document's file extension.</param>
public sealed record FullTextColumn(string Name, int? Language = null, string? TypeColumn = null);

/// <summary>
/// A table's <c>PERIOD FOR SYSTEM_TIME</c>: the row start and end columns SQL Server sets, and
/// the history table old rows go to while <c>SYSTEM_VERSIONING = ON</c> (null while it is off).
/// </summary>
public sealed record SystemTimePeriod(string StartColumn, string EndColumn, ObjectName? HistoryTable);

/// <param name="Period">The table's system-time period, when it has one.</param>
public sealed record Table(ObjectName Name, IReadOnlyList<Column> Columns,
    PrimaryKey? PrimaryKey, IReadOnlyList<UniqueConstraint> Uniques,
    IReadOnlyList<ForeignKey> ForeignKeys, IReadOnlyList<CheckConstraint> Checks,
    IReadOnlyList<TableIndex> Indexes, long? ApproximateRowCount = null, SystemTimePeriod? Period = null);

/// <param name="CurrentValue">The last value the sequence handed out; null when it has never been used.</param>
public sealed record Sequence(ObjectName Name, SqlServerType Type, long Start, long Increment,
    long? Min, long? Max, bool Cycle, long? CurrentValue = null);

/// <summary>An alias type (<c>CREATE TYPE … FROM</c>) or a table type (<c>CREATE TYPE … AS TABLE</c>).</summary>
public sealed record UserType(ObjectName Name, SqlServerType BaseType, bool IsNullable, bool IsTableType,
    IReadOnlyList<Column> TableColumns);

/// <summary>A view, function, procedure or trigger, with its full <c>CREATE</c> text.</summary>
/// <param name="Definition">The text from <c>sys.sql_modules</c>; empty for an encrypted module.</param>
/// <param name="ParentTable">The table or view a trigger is on.</param>
/// <param name="IsIndexed">A view with a (unique clustered) index: SQL Server stores its rows.</param>
public sealed record Module(ObjectName Name, ObjectKind Kind, string Definition, ObjectName? ParentTable = null, bool IsIndexed = false);

/// <summary>
/// <paramref name="From"/> references <paramref name="To"/>. A record rather than the plan's
/// tuple so it serializes to JSON (System.Text.Json ignores tuple fields).
/// </summary>
public sealed record ObjectDependency(ObjectName From, ObjectName To);

/// <param name="AgentJobs">SQL Server Agent jobs with T-SQL steps in this database, read from msdb when asked for.</param>
public sealed record SourceDatabase(string Name, string DefaultCollation, int CompatibilityLevel,
    IReadOnlyList<string> Schemas, IReadOnlyList<Table> Tables, IReadOnlyList<Sequence> Sequences,
    IReadOnlyList<UserType> UserTypes, IReadOnlyList<Module> Modules,
    IReadOnlyList<ObjectDependency> Dependencies, IReadOnlyList<AgentJob>? AgentJobs = null);

/// <summary>
/// A SQL Server Agent job (msdb.dbo.sysjobs) with its steps and schedules. Its T-SQL steps in this
/// database are converted as the procedure <see cref="Procedure"/>; the schedules become pg_cron jobs.
/// </summary>
public sealed record AgentJob(string Name, string? Description, bool Enabled, IReadOnlyList<AgentJobStep> Steps, IReadOnlyList<AgentSchedule> Schedules)
{
    /// <summary>The schema the jobs' procedures are put in.</summary>
    public const string Schema = "sql2pg_agent";

    public ObjectName Procedure => new(Schema, Name);
}

/// <summary>A job step (msdb.dbo.sysjobsteps). Actions: 1 quit with success, 2 quit with failure, 3 go to the next step, 4 go to step n.</summary>
public sealed record AgentJobStep(int Id, string Name, string Subsystem, string Command, string? DatabaseName,
    int OnSuccessAction, int OnSuccessStepId, int OnFailAction, int OnFailStepId)
{
    /// <summary>Whether the step is T-SQL run in the given database, which the converter converts.</summary>
    public bool IsTsqlIn(string database) =>
        Subsystem.Equals("TSQL", StringComparison.OrdinalIgnoreCase)
        && (DatabaseName is null || DatabaseName.Equals(database, StringComparison.OrdinalIgnoreCase));
}

/// <summary>A job schedule (msdb.dbo.sysschedules), in SQL Server's encoding of the freq_* columns; times are HHMMSS integers.</summary>
public sealed record AgentSchedule(string Name, bool Enabled, int FreqType, int FreqInterval, int FreqSubdayType, int FreqSubdayInterval,
    int FreqRelativeInterval, int FreqRecurrenceFactor, int ActiveStartTime, int ActiveEndTime);
