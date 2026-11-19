namespace Majorsilence.Sql2Postgre.Model;

/// <summary>
/// Every diagnostic code, grouped by area. Each also has a section in <c>docs/diagnostics.md</c>
/// saying what it means and what a person should do about it. Codes are never reused.
/// </summary>
/// <remarks>
/// Ranges: 0xxx input, reading and parsing; 1xxx types, names and collations; 2xxx schema objects;
/// 3xxx expressions and built-in functions; 4xxx queries and DML; 5xxx procedural code;
/// 6xxx triggers; 7xxx data copy and verification; 9xxx internal errors.
/// </remarks>
public static class DiagnosticCodes
{
    /// <summary>A T-SQL batch did not parse. The batch is skipped; the reader continues with the next.</summary>
    public const string ParseError = "S2P0001";

    /// <summary>The module was created <c>WITH ENCRYPTION</c>; SQL Server does not reveal its text.</summary>
    public const string EncryptedModule = "S2P0101";

    /// <summary>A CLR function, procedure, aggregate or trigger: .NET code, not T-SQL.</summary>
    public const string ClrObject = "S2P0102";

    /// <summary>Views, functions, procedures and triggers are listed but not converted until Stage 2/3.</summary>
    public const string NotYetSupported = "S2P0201";

    /// <summary>The object uses another that is not converted yet (a routine, before Stage 3) or needs a person.</summary>
    public const string DependsOnUnconverted = "S2P0203";

    /// <summary>USE or a session SET option in a script is dropped.</summary>
    public const string SessionStatementDropped = "S2P0204";

    /// <summary>DDL in an ad-hoc script: schema objects are converted by sql2pg schema.</summary>
    public const string DdlInScript = "S2P0205";

    /// <summary>Procedural T-SQL not translated yet (EXEC, TRY/CATCH, cursors, temp tables, transactions, …).</summary>
    public const string ProceduralStatement = "S2P0206";

    /// <summary>Objects depend on each other in a cycle; they are emitted in name order.</summary>
    public const string DependencyCycle = "S2P0202";

    /// <summary>Code refers to an object in another database or on a linked server.</summary>
    public const string ExternalReference = "S2P0301";

    /// <summary>A name is longer than PostgreSQL's 63-byte limit and was shortened, with a hash to keep it unique.</summary>
    public const string IdentifierTruncated = "S2P1001";

    /// <summary>Two names map to the same PostgreSQL identifier; the later one gets a numeric suffix.</summary>
    public const string IdentifierCollision = "S2P1002";

    /// <summary>SQL Server keeps 7 fractional-second digits here; PostgreSQL keeps 6, so values are rounded.</summary>
    public const string FractionalSecondsReduced = "S2P1101";

    /// <summary>datetimeoffset keeps each value's offset; timestamptz keeps only the instant.</summary>
    public const string OffsetNotPreserved = "S2P1102";

    /// <summary>rowversion becomes an ordinary bytea column; SQL Server no longer maintains it.</summary>
    public const string RowversionSemantics = "S2P1103";

    /// <summary>sql_variant values are kept as text; their original types are lost.</summary>
    public const string SqlVariantAsText = "S2P1104";

    /// <summary>hierarchyid values are kept as their string form; its methods need rewriting.</summary>
    public const string HierarchyIdAsText = "S2P1105";

    /// <summary>A spatial column is kept as WKB in bytea because postgis is disabled.</summary>
    public const string SpatialAsWkb = "S2P1106";

    /// <summary>A vector column is kept as real[] because the vector extension is disabled.</summary>
    public const string VectorAsArray = "S2P1107";

    /// <summary>No PostgreSQL type is known for the SQL Server type; it is written as text.</summary>
    public const string UnknownType = "S2P1199";

    /// <summary>A text column uses a case-insensitive collation; PostgreSQL will compare it case-sensitively.</summary>
    public const string CaseInsensitiveCollation = "S2P1301";

    /// <summary>LIKE is not supported on a nondeterministic collation before PostgreSQL 18.</summary>
    public const string LikeOnNondeterministic = "S2P1302";

    /// <summary>The object uses a permitted extension, which the target database must have available.</summary>
    public const string ExtensionRequired = "S2P1401";

    /// <summary>A non-persisted computed column becomes a STORED generated column (VIRTUAL needs PostgreSQL 18).</summary>
    public const string GeneratedColumnStored = "S2P2001";

    /// <summary>Clustered indexes and primary keys become ordinary B-tree indexes; PostgreSQL does not keep rows in index order.</summary>
    public const string ClusteredIgnored = "S2P2002";

    /// <summary>A columnstore, XML, hash or (without postgis) spatial index, or a full-text index with nothing left to index, has no PostgreSQL equivalent and is not created.</summary>
    public const string IndexTypeUnsupported = "S2P2003";

    /// <summary>A constraint SQL Server does not trust is created NOT VALID: existing rows are not checked.</summary>
    public const string ConstraintNotValid = "S2P2004";

    /// <summary>A table type becomes a composite type; table-valued parameters need manual review.</summary>
    public const string TableTypeAsComposite = "S2P2005";

    /// <summary>A sequence's type has no PostgreSQL sequence equivalent and becomes bigint.</summary>
    public const string SequenceTypeChanged = "S2P2006";

    /// <summary>A computed column read from a script has a type that cannot be inferred; a type override is needed.</summary>
    public const string ComputedColumnTypeUnknown = "S2P2007";

    /// <summary>A computed column read from a script has its type inferred from its expression.</summary>
    public const string ComputedColumnTypeInferred = "S2P2010";

    /// <summary>An identity column on decimal/numeric becomes an integer type, as PostgreSQL identity requires.</summary>
    public const string IdentityTypeChanged = "S2P2008";

    /// <summary>A computed column's expression is not immutable, which a PostgreSQL generated column requires.</summary>
    public const string GeneratedNotImmutable = "S2P2009";

    /// <summary>ROWGUIDCOL has no meaning in PostgreSQL and is dropped.</summary>
    public const string RowGuidColIgnored = "S2P2011";

    /// <summary>SPARSE has no meaning in PostgreSQL (NULLs take no space anyway) and is dropped.</summary>
    public const string SparseIgnored = "S2P2012";

    /// <summary>A disabled constraint is created NOT VALID, but PostgreSQL will enforce it for new rows.</summary>
    public const string DisabledConstraintEnforced = "S2P2013";

    /// <summary>A key, index or constraint uses a column that was left out of the table (it could not be translated).</summary>
    public const string UsesUntranslatedColumn = "S2P2014";

    /// <summary>An indexed view becomes a materialized view, which must be refreshed (SQL Server kept it current).</summary>
    public const string MaterializedViewRefresh = "S2P2015";

    /// <summary>WITH SCHEMABINDING (and VIEW_METADATA) have no PostgreSQL counterpart and are dropped.</summary>
    public const string ViewOptionDropped = "S2P2016";

    /// <summary>A system-versioned temporal table: a trigger sets its period columns and keeps its history.</summary>
    public const string SystemVersioning = "S2P2020";

    /// <summary>A full-text index's language, stoplist or document type column has no PostgreSQL counterpart.</summary>
    public const string FullTextSettingUnmapped = "S2P2021";

    /// <summary>A pg_trgm GIN index for a column searched with a leading-wildcard LIKE (or named in the options).</summary>
    public const string TrigramIndex = "S2P2022";

    /// <summary>A SQL Server Agent schedule cron cannot express, or a job that is converted but not scheduled.</summary>
    public const string AgentScheduleUnmapped = "S2P2023";

    /// <summary>A SQL Server Agent job step that is not converted (not T-SQL, another database, a jump to a step).</summary>
    public const string AgentStepUnconverted = "S2P2024";

    /// <summary>A SQL Server Agent job scheduled with pg_cron.</summary>
    public const string AgentJobScheduled = "S2P2025";

    /// <summary>An expression uses T-SQL the translator does not handle yet; it is kept as a comment.</summary>
    public const string UnsupportedExpression = "S2P3001";

    /// <summary>NEWSEQUENTIALID() becomes a random UUID (before PostgreSQL 18), so values are not ordered.</summary>
    public const string NotSequential = "S2P3002";

    /// <summary>CONVERT with a style the translator does not know (or a non-literal style).</summary>
    public const string ConvertStyleUnsupported = "S2P3003";

    /// <summary>FORMAT with a format or culture the translator does not know.</summary>
    public const string FormatUnsupported = "S2P3004";

    /// <summary>DATEPART(weekday/week) depends on SET DATEFIRST; the default (7) is assumed.</summary>
    public const string DateFirstAssumed = "S2P3005";

    /// <summary>@@ROWCOUNT, SCOPE_IDENTITY(), ERROR_*() and the like belong to procedural code (Stage 3).</summary>
    public const string ProceduralGlobal = "S2P3006";

    /// <summary>A JSON path that is not a simple literal $.a.b[n] path.</summary>
    public const string JsonPathUnsupported = "S2P3007";

    /// <summary>HASHBYTES on nvarchar hashes UTF-16 bytes in SQL Server, UTF-8 in the translation.</summary>
    public const string HashEncodingDiffers = "S2P3008";

    /// <summary>The construct needs a permitted extension that the options disable.</summary>
    public const string ExtensionDisabled = "S2P3009";

    /// <summary>+ on operands of unknown type is kept as addition; if they are strings it must be ||.</summary>
    public const string AmbiguousPlus = "S2P3010";

    /// <summary>ROUND on a float rounds the binary value in SQL Server and the decimal value in PostgreSQL.</summary>
    public const string FloatRounding = "S2P3012";

    /// <summary>NUL characters were removed from text values; PostgreSQL text cannot hold them.</summary>
    public const string NulCharacterRemoved = "S2P7001";

    /// <summary>Spatial values had Z or M coordinates, which the copy drops.</summary>
    public const string SpatialZmDropped = "S2P7002";

    /// <summary>A table's data (or a sequence's position) could not be copied.</summary>
    public const string TableCopyFailed = "S2P7003";

    /// <summary>How an incremental sync (data --since) brought a table up to date.</summary>
    public const string IncrementalSync = "S2P7010";

    /// <summary>A table has a different number of rows in PostgreSQL than in SQL Server.</summary>
    public const string RowCountMismatch = "S2P7101";

    /// <summary>A table has the same number of rows on both sides but different values.</summary>
    public const string ChecksumMismatch = "S2P7102";

    /// <summary>A column reference could not be resolved; it is written by its name without its type.</summary>
    public const string UnresolvedColumn = "S2P3013";

    /// <summary>DATALENGTH counts UTF-16 bytes for nvarchar in SQL Server, UTF-8 bytes in PostgreSQL.</summary>
    public const string ByteLengthDiffers = "S2P3014";

    /// <summary>A COLLATE clause is dropped.</summary>
    public const string CollationIgnored = "S2P3015";

    /// <summary>The translation needs an mssql_compat helper and the compat schema is disabled.</summary>
    public const string CompatDisabled = "S2P3020";

    /// <summary>A spatial method (STDistance, …), which needs the PostGIS mapping of Stage 4.</summary>
    public const string SpatialMethod = "S2P3030";

    /// <summary>A spatial method maps to a PostGIS function whose results differ slightly (buffers, geography containment).</summary>
    public const string SpatialResultsDiffer = "S2P3031";

    /// <summary>The xml modify() method: PostgreSQL's xml type has no update functions.</summary>
    public const string XmlModify = "S2P3040";

    /// <summary>An xml method's XQuery goes beyond XPath 1.0, which is all PostgreSQL evaluates.</summary>
    public const string XQueryUnsupported = "S2P3041";

    /// <summary>Full-text search through PostgreSQL text search: which rows match is close, ranks and stemming differ.</summary>
    public const string FullTextSearch = "S2P3042";

    /// <summary>A SQL Server catalog view, or a catalog property, the converter does not answer.</summary>
    public const string CatalogViewUnsupported = "S2P3043";

    /// <summary>sys.*, INFORMATION_SCHEMA.* or a metadata function answered from mssql_compat's catalog of converted objects.</summary>
    public const string CatalogEmulated = "S2P3044";

    /// <summary>TOP … PERCENT counts the rows first: the query runs twice.</summary>
    public const string TopPercent = "S2P4001";

    /// <summary>A table, join or query hint is dropped (PostgreSQL's planner has no hints).</summary>
    public const string HintDropped = "S2P4002";

    /// <summary>INSERT … EXEC needs the procedure translated (Stage 3).</summary>
    public const string InsertExec = "S2P4003";

    /// <summary>SET IDENTITY_INSERT is dropped (identities accept explicit values); the sequence is moved past them.</summary>
    public const string IdentityInsert = "S2P4004";

    /// <summary>UPDATE … SET @v = col = expr (a variable assigned row by row).</summary>
    public const string QuirkyUpdate = "S2P4005";

    /// <summary>UPDATE or DELETE whose target is inside an outer join of its FROM clause.</summary>
    public const string UpdateFromOuterJoin = "S2P4008";

    /// <summary>UPDATE or DELETE with TOP.</summary>
    public const string DmlTop = "S2P4009";

    /// <summary>FOR XML other than PATH('') concatenation.</summary>
    public const string ForXmlUnsupported = "S2P4010";

    /// <summary>FOR JSON beyond PATH (or AUTO over one table) with named columns.</summary>
    public const string ForJsonUnsupported = "S2P4011";

    /// <summary>TOP 100 PERCENT … ORDER BY in a view is dropped (SQL Server ignores that order too).</summary>
    public const string ViewOrderIgnored = "S2P4012";

    /// <summary>FOR SYSTEM_TIME reads the current and the history table together.</summary>
    public const string SystemTimeQuery = "S2P4014";

    /// <summary>An OUTPUT … INTO form that is not translated (OUTPUT INTO alongside a plain OUTPUT).</summary>
    public const string OutputInto = "S2P4013";

    /// <summary>
    /// OUTPUT for a PostgreSQL before 18, which has no old/new in RETURNING: an UPDATE's deleted
    /// read through a self-join (Info; Warning on ctid), or a MERGE … OUTPUT that cannot be
    /// written (Error: MERGE has RETURNING from 17, and its deleted needs 18 when it updates).
    /// </summary>
    public const string OutputBefore18 = "S2P4015";

    /// <summary>MERGE … WHEN NOT MATCHED BY SOURCE for a PostgreSQL before 17: each action runs as a data-modifying CTE.</summary>
    public const string MergeBySource = "S2P4016";

    // ---- 5xxx procedural code ----

    /// <summary>A variable declared twice with different types (PostgreSQL has one declaration per body).</summary>
    public const string VariableRedeclared = "S2P5001";

    /// <summary>SELECT assigns variables from a query that may return several rows and has no ORDER BY: which row's values remain is arbitrary.</summary>
    public const string SelectAssignmentOrder = "S2P5002";

    /// <summary>Without XACT_ABORT, SQL Server continues after most statement errors; PostgreSQL stops at the first.</summary>
    public const string ErrorStopsExecution = "S2P5003";

    /// <summary>@@ERROR outside a CATCH block is translated as 0 (an error has already ended the routine in PostgreSQL).</summary>
    public const string ErrorGlobal = "S2P5004";

    /// <summary>GOTO and labels have no PL/pgSQL equivalent.</summary>
    public const string GotoLabel = "S2P5005";

    /// <summary>SELECT @v = expression-using-@v over a query with aggregates, grouping or subqueries (row-by-row accumulation that cannot be rewritten).</summary>
    public const string SelectAssignmentAccumulates = "S2P5006";

    /// <summary>RAISERROR with severity 11–18 outside TRY: SQL Server continues with the next statement, PostgreSQL ends the routine.</summary>
    public const string RaiserrorStops = "S2P5007";

    /// <summary>RAISERROR with a message number from sys.messages, or a non-literal message with arguments.</summary>
    public const string RaiserrorUntranslatable = "S2P5008";

    /// <summary>A statement returning rows inside a script batch that became a DO block, which cannot return rows.</summary>
    public const string ResultSetInBlock = "S2P5009";

    /// <summary>An inline table-valued function's result column has no known type, so RETURNS TABLE cannot be written.</summary>
    public const string FunctionColumnTypeUnknown = "S2P5010";

    /// <summary>A parameter named like one of the function's result columns is renamed p_name.</summary>
    public const string ParameterRenamed = "S2P5012";

    /// <summary>A parameter after one with a default gets DEFAULT NULL (PostgreSQL requires it).</summary>
    public const string ParameterDefaultAdded = "S2P5020";

    /// <summary>A procedure's result sets are returned through INOUT refcursor parameters: its callers change.</summary>
    public const string ResultSetCursor = "S2P5021";

    /// <summary>A procedure returning a status code gets an INOUT return_code parameter.</summary>
    public const string ReturnCodeParameter = "S2P5022";

    /// <summary>A call to a SQL Server system procedure (sp_*, xp_*), which PostgreSQL does not have.</summary>
    public const string SystemProcedure = "S2P5023";

    /// <summary>SAVE TRANSACTION or ROLLBACK to a savepoint inside a routine: PL/pgSQL has no savepoints.</summary>
    public const string Savepoint = "S2P5024";

    /// <summary>@@TRANCOUNT: PostgreSQL has no transaction nesting count.</summary>
    public const string TranCount = "S2P5025";

    /// <summary>BEGIN TRANSACTION inside a routine becomes COMMIT (the work before it was autocommitted), or inside TRY a COMMIT before the TRY block.</summary>
    public const string BeginTransactionDropped = "S2P5026";

    /// <summary>EXEC of a procedure that is not in the source database.</summary>
    public const string UnknownProcedure = "S2P5027";

    /// <summary>A global temporary table (##name), shared by every session: PostgreSQL has none.</summary>
    public const string GlobalTempTable = "S2P5030";

    /// <summary>ROLLBACK in a routine with table variables: SQL Server keeps their rows, PostgreSQL rolls them back.</summary>
    public const string TableVariableRollback = "S2P5031";

    /// <summary>A procedure calls one that creates a temp table of the same name: in PostgreSQL the callee's replaces the caller's.</summary>
    public const string TempTableReplaced = "S2P5032";

    /// <summary>A FOREIGN KEY on a temp table is dropped, as SQL Server itself ignores it.</summary>
    public const string TempTableForeignKey = "S2P5033";

    /// <summary>ERROR_LINE() is NULL and ERROR_STATE() is 1: PostgreSQL errors have neither.</summary>
    public const string ErrorLineState = "S2P5040";

    /// <summary>XACT_STATE() or @@TRANCOUNT in CATCH is a constant: whether a BEGIN TRAN may have run.</summary>
    public const string TransactionState = "S2P5041";

    /// <summary>COMMIT, ROLLBACK or a call of a procedure that ends a transaction inside TRY: PL/pgSQL cannot end a transaction inside a block with an exception handler.</summary>
    public const string TransactionInTry = "S2P5042";

    /// <summary>An ERROR_*() function outside CATCH is NULL.</summary>
    public const string ErrorFunctionOutsideCatch = "S2P5043";

    /// <summary>A TRY changes data outside a transaction and runs more statements: PostgreSQL undoes the changes when a later statement fails, SQL Server keeps them.</summary>
    public const string TryUndoesWrites = "S2P5044";

    /// <summary>A GLOBAL cursor, which outlives the code that declares it.</summary>
    public const string GlobalCursor = "S2P5050";

    /// <summary>Dynamic SQL that cannot be translated: its text is not known here, or it is not a query or DML.</summary>
    public const string DynamicSqlUntranslatable = "S2P5060";

    /// <summary>A name in dynamic SQL comes from a variable and is mapped at run time.</summary>
    public const string DynamicIdentifier = "S2P5061";

    /// <summary>Dynamic SQL splices text from a variable in as it stands.</summary>
    public const string DynamicSqlSpliced = "S2P5062";

    /// <summary>DDL (CREATE, ALTER, DROP) inside a procedure, function or trigger.</summary>
    public const string DdlInRoutine = "S2P5070";

    /// <summary>Index DDL in a routine (CREATE, DROP, ALTER INDEX … REBUILD/REORGANIZE) translated; options SQL Server's alone are dropped.</summary>
    public const string IndexDdlInRoutine = "S2P5071";

    /// <summary>ALTER INDEX … DISABLE: PostgreSQL cannot disable an index, so it is dropped (REBUILD creates it again).</summary>
    public const string IndexDisabled = "S2P5072";

    /// <summary>ALTER TABLE … NOCHECK/CHECK CONSTRAINT: PostgreSQL cannot disable a constraint, so it is dropped and added back.</summary>
    public const string ConstraintDisabled = "S2P5073";

    /// <summary>A routine changes the schema at run time (columns, constraints, routines).</summary>
    public const string SchemaChangeInRoutine = "S2P5074";

    /// <summary>SYSTEM_VERSIONING switched off or on, or the period dropped or added, in a routine.</summary>
    public const string SystemVersioningInRoutine = "S2P5075";

    // ---- 6xxx triggers ----

    /// <summary>UPDATE(column) in an UPDATE trigger is true: PostgreSQL does not tell a trigger which columns the statement set.</summary>
    public const string UpdateColumnAssumed = "S2P6001";

    /// <summary>COLUMNS_UPDATED() has no PostgreSQL equivalent.</summary>
    public const string ColumnsUpdated = "S2P6002";

    /// <summary>An INSTEAD OF trigger on a view runs once per row in PostgreSQL, once per statement in SQL Server.</summary>
    public const string InsteadOfPerRow = "S2P6003";

    /// <summary>An INSTEAD OF trigger on a table: PostgreSQL has INSTEAD OF triggers on views only.</summary>
    public const string InsteadOfTable = "S2P6004";

    /// <summary>A DDL or logon trigger.</summary>
    public const string DdlTrigger = "S2P6005";

    /// <summary>Several triggers on one table and event: PostgreSQL fires them in name order.</summary>
    public const string TriggerOrder = "S2P6006";

    /// <summary>RAISERROR format flags, width, precision or %x/%o: the arguments are written in their plain text form.</summary>
    public const string RaiserrorFormat = "S2P5011";

    // ---- 8xxx application code (sql2pg code) ----

    /// <summary>SqlException becomes NpgsqlException: errors are identified by SQLSTATE, and messages differ.</summary>
    public const string SqlExceptionToNpgsql = "S2P8001";

    /// <summary>A SqlException member or error number with no Npgsql counterpart.</summary>
    public const string SqlExceptionMember = "S2P8002";

    /// <summary>A table-valued parameter (SqlDbType.Structured).</summary>
    public const string TableValuedParameter = "S2P8003";

    /// <summary>SqlBulkCopy: the Npgsql replacement (binary COPY) has a different shape.</summary>
    public const string SqlBulkCopy = "S2P8004";

    /// <summary>Integrated Security in a connection string: PostgreSQL signs in differently.</summary>
    public const string IntegratedSecurity = "S2P8005";

    /// <summary>Connection string keys with no Npgsql counterpart, or a SQL Server instance name.</summary>
    public const string ConnectionStringKeyDropped = "S2P8006";

    /// <summary>System.Data.SqlTypes values.</summary>
    public const string SqlTypes = "S2P8007";

    /// <summary>A SqlClient feature with no Npgsql counterpart (query notifications, InfoMessage, XML readers, …).</summary>
    public const string SqlClientOnly = "S2P8008";

    /// <summary>SQL built at run time (concatenation with values that are not constants): it cannot be translated where it is written.</summary>
    public const string SqlBuiltAtRunTime = "S2P8010";

    /// <summary>SCOPE_IDENTITY() after an INSERT reads lastval(): the identity column is not known without --source.</summary>
    public const string IdentityFromLastval = "S2P8011";

    /// <summary>Procedural T-SQL (variables, control flow) in a command.</summary>
    public const string ProceduralSqlInCommand = "S2P8012";

    /// <summary>Translated SQL in code calls mssql_compat helpers, which must exist in the database.</summary>
    public const string CompatHelperInCode = "S2P8013";

    /// <summary>A stored procedure call whose procedure returns refcursors: the caller must fetch them.</summary>
    public const string ProcedureCursors = "S2P8020";

    /// <summary>A stored procedure call whose converted form is not known here (no --source, or parameters not visible).</summary>
    public const string ProcedureShapeUnknown = "S2P8021";

    /// <summary>Dapper.Contrib quotes column names, which must then match the converted columns exactly.</summary>
    public const string DapperContribColumns = "S2P8030";

    /// <summary>A Dapper.Contrib adapter hook (GetDatabaseType, ISqlAdapter).</summary>
    public const string DapperContribAdapter = "S2P8031";

    /// <summary>An EF Core option or fluent call that is SQL Server's, removed or changed.</summary>
    public const string EfSqlServerOnly = "S2P8040";

    /// <summary>EF Core model names: OnModelCreating names everything as the converted schema does.</summary>
    public const string EfNaming = "S2P8041";

    /// <summary>An EF.Functions method or model feature (temporal tables) with no PostgreSQL counterpart.</summary>
    public const string EfSqlServerFunction = "S2P8042";

    /// <summary>EF Core migrations made for SQL Server: regenerate them.</summary>
    public const string EfMigrations = "S2P8043";

    /// <summary>A package reference in a project file changed (SqlClient or the EF Core SQL Server provider).</summary>
    public const string PackageReplaced = "S2P8044";

    /// <summary>A SQL Server database project (.sqlproj, .dbproj) is left as it is.</summary>
    public const string DatabaseProjectSkipped = "S2P8051";

    /// <summary>A connection string in a configuration file (appsettings*.json, web.config, app.config) was converted.</summary>
    public const string ConfigConnectionString = "S2P8052";

    /// <summary>The converter threw while converting an object: a bug. The object is left for a person.</summary>
    public const string InternalError = "S2P9001";

    /// <summary>
    /// Codes whose work differs from their severity's default weight: what a person needs to
    /// rewrite by hand weighs more, and a dependent's error weighs nothing (the work is counted on
    /// the object it waits for).
    /// </summary>
    private static readonly Dictionary<string, int> Weights = new(StringComparer.Ordinal)
    {
        [EncryptedModule] = 20,
        [ClrObject] = 20,
        [ParseError] = 10,
        [InternalError] = 10,
        [DependsOnUnconverted] = 0,
        [ExternalReference] = 8,
        [DynamicSqlUntranslatable] = 8,
        [InsteadOfTable] = 8,
        [DdlTrigger] = 8,
        [ResultSetCursor] = 3,
        [DdlInRoutine] = 3,
    };

    /// <summary>
    /// The effort a diagnostic stands for, in points: Info 0, Warning 1, Error 5, unless the code
    /// has a weight of its own. An assessment adds them up per object.
    /// </summary>
    public static int Weight(string code, Severity severity) =>
        Weights.TryGetValue(code, out int weight) && severity != Severity.Info
            ? weight
            : severity switch { Severity.Error => 5, Severity.Warning => 1, _ => 0 };

    /// <summary>The effort of a set of diagnostics: the sum of their weights.</summary>
    public static int Effort(IEnumerable<Diagnostic> diagnostics) => diagnostics.Sum(d => Weight(d.Code, d.Severity));
}
