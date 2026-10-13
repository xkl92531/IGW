using System.Data;
using Microsoft.Data.Sqlite;

namespace IGW.Core;

/// <summary>Moves an explicit set of IGW-owned SQLite tables into the IGW_ namespace.</summary>
public static class IgwSqliteTableNamespace
{
    public static string PhysicalName(string legacy)
    {
        ValidateIdentifier(legacy);
        string logical = legacy.StartsWith("_igw_", StringComparison.OrdinalIgnoreCase) ? legacy[5..]
            : legacy.StartsWith("igw_", StringComparison.OrdinalIgnoreCase) ? legacy[4..] : legacy;
        if (logical.Length == 0) throw new ArgumentException("An IGW table name must have a nonempty suffix.", nameof(legacy));
        string physical = "IGW_" + logical;
        ValidateIdentifier(physical);
        return physical;
    }

    public static void Ensure(SqliteConnection connection, IEnumerable<string> ownedLegacyTables) =>
        Ensure(connection, ownedLegacyTables, null);

    /// <summary>
    /// Expected columns establish ownership before renaming ambiguous generic tables. The entire
    /// whitelist is checked before any DDL; collisions or invalid ownership roll back all changes.
    /// SQLite ALTER TABLE preserves rowids, foreign keys, indexes and trigger definitions.
    /// Missing signature tables are optional unless requireSignatureTables is true and any owned
    /// object already exists. This lets a primary store require its ownership marker while still
    /// allowing a new, empty database and auxiliary stores with later optional tables.
    /// </summary>
    public static void Ensure(SqliteConnection connection, IEnumerable<string> ownedLegacyTables,
        IReadOnlyDictionary<string, IReadOnlyCollection<string>>? expectedColumnsByLegacyTable,
        bool requireSignatureTables = false)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(ownedLegacyTables);
        if (connection.State != ConnectionState.Open)
            throw new InvalidOperationException("SQLite connection must be open before normalizing tables.");
        string[] owned = ownedLegacyTables.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        foreach (string table in owned) ValidateIdentifier(table);
        if (owned.Select(PhysicalName).Distinct(StringComparer.OrdinalIgnoreCase).Count() != owned.Length)
            throw new ArgumentException("Owned table aliases must map to distinct physical names.", nameof(ownedLegacyTables));
        if (expectedColumnsByLegacyTable is not null)
            foreach ((string table, IReadOnlyCollection<string> columns) in expectedColumnsByLegacyTable)
            {
                if (!owned.Contains(table, StringComparer.OrdinalIgnoreCase))
                    throw new ArgumentException("Ownership signature must refer to an owned table.");
                foreach (string column in columns) ValidateIdentifier(column);
            }

        using var transaction = connection.BeginTransaction();
        var objects = new Dictionary<string, (string Name, string Type)>(StringComparer.OrdinalIgnoreCase);
        using (var inventory = connection.CreateCommand())
        {
            inventory.Transaction = transaction;
            inventory.CommandText = "SELECT name,type FROM sqlite_master WHERE type IN ('table','view');";
            using var reader = inventory.ExecuteReader();
            while (reader.Read()) objects.Add(reader.GetString(0), (reader.GetString(0), reader.GetString(1)));
        }
        var renames = new List<(string Source, string Target)>();
        foreach (string legacy in owned)
        {
            string target = PhysicalName(legacy);
            bool hasOld = objects.TryGetValue(legacy, out var old);
            bool hasTarget = objects.TryGetValue(target, out var current);
            if (hasOld && hasTarget && !string.Equals(old.Name, current.Name, StringComparison.Ordinal))
                throw new InvalidDataException($"Cannot rename IGW table '{old.Name}': target '{current.Name}' already exists.");
            if ((hasOld && old.Type != "table") || (hasTarget && current.Type != "table"))
                throw new InvalidDataException($"IGW table '{legacy}' collides with a non-table object.");
            string? source = hasOld ? old.Name : hasTarget ? current.Name : null;
            if (source is not null && !string.Equals(source, target, StringComparison.Ordinal))
                renames.Add((source, target));
        }
        if (expectedColumnsByLegacyTable is not null)
            foreach ((string legacy, IReadOnlyCollection<string> requiredColumns) in expectedColumnsByLegacyTable)
            {
                string? source = objects.TryGetValue(legacy, out var old) ? old.Name
                    : objects.TryGetValue(PhysicalName(legacy), out var current) ? current.Name : null;
                if (source is null)
                {
                    if (requireSignatureTables && owned.Any(table => objects.ContainsKey(table) || objects.ContainsKey(PhysicalName(table))))
                        throw new InvalidDataException($"Missing IGW ownership signature table '{legacy}'.");
                    continue; // Optional tables may be added by the caller's later schema initialization.
                }
                using var signature = connection.CreateCommand();
                signature.Transaction = transaction;
                signature.CommandText = $"PRAGMA table_info(\"{source}\");";
                using var reader = signature.ExecuteReader();
                var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                while (reader.Read()) columns.Add(reader.GetString(1));
                if (requiredColumns.Any(column => !columns.Contains(column)))
                    throw new InvalidDataException($"Table '{source}' does not match the IGW ownership signature.");
            }
        foreach ((string source, string target) in renames)
        {
            using var rename = connection.CreateCommand();
            rename.Transaction = transaction;
            if (string.Equals(source, target, StringComparison.OrdinalIgnoreCase))
            {
                // SQLite treats identifier casing as equal, so use a unique intermediate name.
                string temporary = "IGW_namespace_" + Guid.NewGuid().ToString("N");
                rename.CommandText = $"ALTER TABLE \"{source}\" RENAME TO \"{temporary}\"; ALTER TABLE \"{temporary}\" RENAME TO \"{target}\";";
            }
            else rename.CommandText = $"ALTER TABLE \"{source}\" RENAME TO \"{target}\";";
            rename.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    private static void ValidateIdentifier(string name)
    {
        if (string.IsNullOrEmpty(name) || !(char.IsAsciiLetter(name[0]) || name[0] == '_')
            || name.Any(character => !(char.IsAsciiLetterOrDigit(character) || character == '_')))
            throw new ArgumentException("SQLite owned identifiers must contain only ASCII letters, digits and underscores.", nameof(name));
    }
}
