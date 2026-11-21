using Majorsilence.Sql2Postgre.Model;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace Majorsilence.Sql2Postgre.Shared;

/// <summary>
/// A column definition's HIDDEN, MASKED WITH and ENCRYPTED WITH, which PostgreSQL columns do not
/// have. Compiled into the script reader and the translator (DDL in routines), as
/// <see cref="ScriptDomTypes"/> is.
/// </summary>
internal static class ColumnFeatureReader
{
    public static ColumnFeatures? Of(ColumnDefinition c) => ColumnFeatures.Of(
        c.IsHidden,
        c.IsMasked ? c.MaskingFunction?.Value ?? "default()" : null,
        c.Encryption is null ? null
            : c.Encryption.Parameters.OfType<ColumnEncryptionTypeParameter>().FirstOrDefault()?.EncryptionType.ToString().ToUpperInvariant() ?? "ENCRYPTED",
        XmlSchemaCollection(c.DataType));

    /// <summary>Typed xml's schema collection, <c>DOCUMENT</c> first when the values must be documents.</summary>
    public static string? XmlSchemaCollection(DataTypeReference? type) => type is XmlDataTypeReference { XmlSchemaCollection: { } collection } x
        ? (x.XmlDataTypeOption == XmlDataTypeOption.Document ? "DOCUMENT " : "")
          + string.Join('.', collection.Identifiers.Select(i => i.Value))
        : null;
}
