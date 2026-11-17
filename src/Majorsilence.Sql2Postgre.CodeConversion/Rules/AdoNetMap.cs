using Majorsilence.Sql2Postgre.Model;

namespace Majorsilence.Sql2Postgre.CodeConversion.Rules;

/// <summary>What SqlClient names become in Npgsql, and what has no counterpart.</summary>
internal static class AdoNetMap
{
    public static readonly string[] SqlClientNamespaces = ["Microsoft.Data.SqlClient", "System.Data.SqlClient"];

    public static bool IsSqlClient(string? ns) => ns is not null && SqlClientNamespaces.Contains(ns, StringComparer.Ordinal);

    /// <summary>SqlClient types and their Npgsql counterparts.</summary>
    public static readonly Dictionary<string, string> Types = new(StringComparer.Ordinal)
    {
        ["SqlConnection"] = "NpgsqlConnection",
        ["SqlCommand"] = "NpgsqlCommand",
        ["SqlDataReader"] = "NpgsqlDataReader",
        ["SqlParameter"] = "NpgsqlParameter",
        ["SqlParameterCollection"] = "NpgsqlParameterCollection",
        ["SqlTransaction"] = "NpgsqlTransaction",
        ["SqlDataAdapter"] = "NpgsqlDataAdapter",
        ["SqlCommandBuilder"] = "NpgsqlCommandBuilder",
        ["SqlConnectionStringBuilder"] = "NpgsqlConnectionStringBuilder",
        ["SqlClientFactory"] = "NpgsqlFactory",
        ["SqlBatch"] = "NpgsqlBatch",
        ["SqlBatchCommand"] = "NpgsqlBatchCommand",
        // NpgsqlException is the base of PostgresException: like SqlException it covers network errors too.
        ["SqlException"] = "NpgsqlException",
    };

    /// <summary>SqlClient types with no Npgsql counterpart of the same shape, and why.</summary>
    public static readonly Dictionary<string, (string Code, string Why)> Unsupported = new(StringComparer.Ordinal)
    {
        ["SqlBulkCopy"] = (DiagnosticCodes.SqlBulkCopy,
            "SqlBulkCopy has no Npgsql class of the same shape: use NpgsqlConnection.BeginBinaryImport (COPY … FROM STDIN (FORMAT BINARY)), writing each row with StartRow and Write."),
        ["SqlBulkCopyOptions"] = (DiagnosticCodes.SqlBulkCopy, "SqlBulkCopyOptions belongs to SqlBulkCopy (see the same line for its replacement)."),
        ["SqlBulkCopyColumnMapping"] = (DiagnosticCodes.SqlBulkCopy, "SqlBulkCopyColumnMapping belongs to SqlBulkCopy: with COPY, the column list is part of the COPY command."),
        ["SqlDependency"] = (DiagnosticCodes.SqlClientOnly, "SqlDependency (query notifications) has no Npgsql equivalent: use LISTEN/NOTIFY with a trigger that calls pg_notify."),
        ["SqlCredential"] = (DiagnosticCodes.SqlClientOnly, "SqlCredential has no Npgsql equivalent: put Username and Password in the connection string, or use a password provider."),
        ["SqlInfoMessageEventArgs"] = (DiagnosticCodes.SqlClientOnly, "PRINT/RAISERROR messages arrive through NpgsqlConnection.Notice (NoticeEventArgs.Notice), not InfoMessage."),
        ["SqlInfoMessageEventHandler"] = (DiagnosticCodes.SqlClientOnly, "PRINT/RAISERROR messages arrive through NpgsqlConnection.Notice (NoticeEventArgs.Notice), not InfoMessage."),
        ["SqlError"] = (DiagnosticCodes.SqlExceptionMember, "SqlError (one of SqlException.Errors) has no Npgsql counterpart: PostgresException carries one error with its SqlState, Detail and Hint."),
        ["SqlErrorCollection"] = (DiagnosticCodes.SqlExceptionMember, "SqlException.Errors has no Npgsql counterpart: PostgresException carries one error."),
        ["SqlAuthenticationMethod"] = (DiagnosticCodes.SqlClientOnly, "Azure AD / Entra authentication methods have no Npgsql enum: configure authentication in the connection string or a password provider."),
    };

    /// <summary>Members of SqlClient objects with no Npgsql counterpart.</summary>
    public static readonly Dictionary<string, string> UnsupportedMembers = new(StringComparer.Ordinal)
    {
        ["ExecuteXmlReader"] = "ExecuteXmlReader has no Npgsql counterpart: select the xml column and read it as a string.",
        ["ExecuteXmlReaderAsync"] = "ExecuteXmlReaderAsync has no Npgsql counterpart: select the xml column and read it as a string.",
        ["InfoMessage"] = "PRINT/RAISERROR messages arrive through NpgsqlConnection.Notice (NoticeEventArgs.Notice), not InfoMessage.",
        ["RetrieveStatistics"] = "Connection statistics have no Npgsql counterpart (Npgsql reports metrics through System.Diagnostics.Metrics).",
        ["StatisticsEnabled"] = "Connection statistics have no Npgsql counterpart (Npgsql reports metrics through System.Diagnostics.Metrics).",
        ["FireInfoMessageEventOnUserErrors"] = "FireInfoMessageEventOnUserErrors has no Npgsql counterpart.",
        ["AccessToken"] = "AccessToken has no Npgsql counterpart: use a password provider (NpgsqlDataSourceBuilder.UsePeriodicPasswordProvider).",
    };

    /// <summary>SqlDbType members and the NpgsqlDbType for the PostgreSQL type the schema converter maps the SQL Server type to.</summary>
    public static readonly Dictionary<string, string?> DbTypes = new(StringComparer.Ordinal)
    {
        ["BigInt"] = "Bigint",
        ["Binary"] = "Bytea",
        ["Bit"] = "Boolean",
        ["Char"] = "Char",
        ["Date"] = "Date",
        ["DateTime"] = "Timestamp",
        ["DateTime2"] = "Timestamp",
        ["DateTimeOffset"] = "TimestampTz",
        ["Decimal"] = "Numeric",
        ["Float"] = "Double",
        ["Image"] = "Bytea",
        ["Int"] = "Integer",
        ["Money"] = "Numeric",
        ["NChar"] = "Char",
        ["NText"] = "Text",
        ["NVarChar"] = "Varchar",
        ["Real"] = "Real",
        ["SmallDateTime"] = "Timestamp",
        ["SmallInt"] = "Smallint",
        ["SmallMoney"] = "Numeric",
        ["Text"] = "Text",
        ["Time"] = "Time",
        ["Timestamp"] = "Bytea",
        ["TinyInt"] = "Smallint",
        ["UniqueIdentifier"] = "Uuid",
        ["VarBinary"] = "Bytea",
        ["VarChar"] = "Varchar",
        ["Variant"] = "Text",
        ["Xml"] = "Xml",
        ["Json"] = "Jsonb",
        ["Structured"] = null,
        ["Udt"] = null,
    };

    /// <summary>SQL Server error numbers and the SQLSTATEs of the same errors in PostgreSQL.</summary>
    public static readonly Dictionary<int, string[]> ErrorNumbers = new()
    {
        [2627] = ["23505"], // unique or primary key constraint
        [2601] = ["23505"], // unique index
        [547] = ["23503", "23514"], // foreign key or check constraint
        [515] = ["23502"], // NULL into NOT NULL
        [1205] = ["40P01"], // deadlock victim
        [8152] = ["22001"], // string or binary data would be truncated
        [2628] = ["22001"],
        [8134] = ["22012"], // divide by zero
        [8115] = ["22003"], // arithmetic overflow
        [245] = ["22P02"], // conversion failed
        [50000] = ["P0001"], // RAISERROR / THROW with a message
    };

    /// <summary>System.Data.SqlTypes types.</summary>
    public static readonly HashSet<string> SqlTypes = new(StringComparer.Ordinal)
    {
        "SqlString", "SqlInt16", "SqlInt32", "SqlInt64", "SqlByte", "SqlBoolean", "SqlDecimal", "SqlDouble", "SqlSingle",
        "SqlMoney", "SqlDateTime", "SqlGuid", "SqlBinary", "SqlBytes", "SqlChars", "SqlXml",
    };

    /// <summary>The SqlException members other than Message, and why each has no direct counterpart.</summary>
    public static readonly Dictionary<string, string> ExceptionMembers = new(StringComparer.Ordinal)
    {
        ["Number"] = "SqlException.Number is a SQL Server error number; NpgsqlException.SqlState is a five-character SQLSTATE",
        ["Errors"] = "SqlException.Errors has no counterpart: PostgresException carries one error",
        ["Class"] = "SqlException.Class (severity) has no counterpart: PostgresException.Severity is ERROR, FATAL or PANIC",
        ["LineNumber"] = "SqlException.LineNumber has no counterpart: PostgresException.Line is the server source line, not yours",
        ["Procedure"] = "SqlException.Procedure has no counterpart: PostgresException.Routine names the server's C function; PL/pgSQL context is in Where",
        ["Server"] = "SqlException.Server has no counterpart",
        ["State"] = "SqlException.State has no counterpart",
    };
}
