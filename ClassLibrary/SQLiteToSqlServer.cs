using System.Data;
using System.Data.SQLite;
using System.Data.SqlClient;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace ClassLibrary
{
    /// <summary>
    /// Converts a SQLite database (as produced by SqlServerToSQLite) back into SQL Server,
    /// preserving the original column types.
    ///
    /// Type resolution order for every column:
    ///   1. An explicit override (sidecar JSON "&lt;sqlite path&gt;.typemap.json" or the
    ///      columnTypeOverrides parameter), keyed "Table.Column" with a value such as
    ///      "bigint", "int identity" or "nvarchar(450)".
    ///   2. The declared type, when it is already a SQL Server type (this is the case for
    ///      files written by SqlServerToSQLite, which keeps the original type names).
    ///   3. SQLite affinity fallback: INTEGER columns are measured (MIN/MAX) and created
    ///      as INT when every value fits, otherwise BIGINT; TEXT -> NVARCHAR(MAX),
    ///      REAL -> FLOAT, BLOB -> VARBINARY(MAX).
    ///
    /// Single-column numeric primary keys are created as IDENTITY(1,1) (matching the SQL
    /// Server / EF convention), the data is copied with IDENTITY_INSERT ON and the seed is
    /// restored afterwards. Indexes (except PK-backed sqlite_autoindex_*) are recreated.
    /// </summary>
    public static class SQLiteToSqlServer
    {
        public static void Convert(
            string sqlitePath,
            string sqlServerConnectionString,
            Action<int, string> progress = null,
            IDictionary<string, string> columnTypeOverrides = null)
        {
            if (string.IsNullOrWhiteSpace(sqlitePath) || !File.Exists(sqlitePath))
                throw new FileNotFoundException("The SQLite database file was not found.", sqlitePath);

            IDictionary<string, string> overrides = LoadOverrides(sqlitePath, columnTypeOverrides);
            IDictionary<string, string> primaryKeys = ExtractPrimaryKeys(overrides, out overrides);

            SqlConnectionStringBuilder targetBuilder = new SqlConnectionStringBuilder(sqlServerConnectionString);
            EnsureDatabaseExists(targetBuilder);

            using SQLiteConnection sqlite = new SQLiteConnection(CreateSQLiteConnectionString(sqlitePath));
            using SqlConnection sqlServer = new SqlConnection(targetBuilder.ConnectionString);
            sqlite.Open();
            sqlServer.Open();

            EnsureTargetIsEmpty(sqlServer);

            List<SQLiteTableInfo> tables = ReadTables(sqlite);
            if (tables.Count == 0)
                throw new InvalidOperationException("The SQLite database does not contain any user tables.");

            // some source tables may have lost their PRIMARY KEY on the SQLite side; the
            // sidecar ("@primarykey:Table": "Col1,Col2") restores it so foreign keys resolve
            foreach (SQLiteTableInfo table in tables)
            {
                if (table.Columns.Any(column => column.PrimaryKeyOrder > 0))
                    continue;
                if (primaryKeys.TryGetValue(table.Name, out string pkColumns))
                {
                    int order = 1;
                    foreach (string pkColumn in pkColumns.Split(','))
                    {
                        SQLiteColumnInfo pkInfo = table.Columns.FirstOrDefault(
                            candidate => string.Equals(candidate.Name, pkColumn.Trim(), StringComparison.OrdinalIgnoreCase));
                        if (pkInfo != null)
                            pkInfo.PrimaryKeyOrder = order++;
                    }
                }
            }

            // Resolve every column type up-front (may run MIN/MAX probes for INTEGER columns).
            progress?.Invoke(0, "Resolving column types...");
            foreach (SQLiteTableInfo table in tables)
            {
                foreach (SQLiteForeignKeyInfo foreignKey in table.ForeignKeys)
                {
                    SQLiteTableInfo referencedTable = tables.FirstOrDefault(candidate => candidate.Name == foreignKey.ReferencedTable);
                    referencedTable?.ReferencedKeyColumns.Add(foreignKey.ReferencedColumnName);
                }
                foreach (SQLiteColumnInfo column in table.Columns)
                    column.ResolvedSqlType = ResolveSqlServerType(sqlite, table, column, overrides);
            }

            // Foreign keys require the referencing column to have EXACTLY the same type as
            // the referenced column. Identity primary keys are collapsed to SQLite "integer"
            // (AUTOINCREMENT requirement) while referencing columns keep their original
            // "bigint"/"int" declaration - so propagate the integer width along every FK edge
            // until all linked columns agree (the wider width always wins).
            PropagateIntegerWidthsAlongForeignKeys(tables);

            // a single-column numeric primary key is an IDENTITY column by the SQL Server
            // convention (EF generates all keys that way); the sidecar can override this
            foreach (SQLiteTableInfo table in tables)
            {
                List<SQLiteColumnInfo> pkColumns = table.Columns
                    .Where(column => column.PrimaryKeyOrder > 0)
                    .OrderBy(column => column.PrimaryKeyOrder)
                    .ToList();
                if (pkColumns.Count != 1)
                    continue;
                string pkType = pkColumns[0].ResolvedSqlType.ToUpperInvariant();
                if ((pkType.StartsWith("INT") || pkType.StartsWith("BIGINT") ||
                     pkType.StartsWith("SMALLINT") || pkType.StartsWith("TINYINT")) &&
                    !pkType.Contains("IDENTITY") && !pkColumns[0].HasExplicitOverride)
                {
                    pkColumns[0].ResolvedSqlType += " IDENTITY";
                }
            }

            for (int index = 0; index < tables.Count; index++)
            {
                SQLiteTableInfo table = tables[index];
                CreateSqlServerTable(sqlServer, table);
                progress?.Invoke((index + 1) * 40 / tables.Count, "Created table " + table.Name);
            }

            for (int index = 0; index < tables.Count; index++)
            {
                CopyRows(sqlite, sqlServer, tables[index]);
                progress?.Invoke(40 + (index + 1) * 40 / tables.Count, "Copied data for " + tables[index].Name);
            }

            RecreateIndexes(sqlServer, tables, progress);
            AddForeignKeys(sqlServer, tables);
            ReseedIdentities(sqlServer, tables);

            progress?.Invoke(100, "SQLite to SQL Server conversion finished.");
        }

        // ------------------------------------------------------------------ schema reading

        private static List<SQLiteTableInfo> ReadTables(SQLiteConnection connection)
        {
            List<SQLiteTableInfo> tables = new List<SQLiteTableInfo>();
            List<string> tableNames = new List<string>();
            using SQLiteCommand command = new SQLiteCommand(
                "SELECT name, sql FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%' ORDER BY name;",
                connection);
            using SQLiteDataReader reader = command.ExecuteReader();
            while (reader.Read())
            {
                tableNames.Add(reader.GetString(0));
            }

            foreach (string tableName in tableNames)
            {
                SQLiteTableInfo table = new SQLiteTableInfo { Name = tableName };
                table.Columns = ReadColumns(connection, table.Name);
                table.ForeignKeys = ReadForeignKeys(connection, table.Name);
                table.Indexes = ReadIndexes(connection, table.Name);
                using (SQLiteCommand ddlCommand = new SQLiteCommand(
                    "SELECT sql FROM sqlite_master WHERE type = 'table' AND name = @name;", connection))
                {
                    ddlCommand.Parameters.AddWithValue("@name", tableName);
                    table.Ddl = System.Convert.ToString(ddlCommand.ExecuteScalar(), CultureInfo.InvariantCulture) ?? string.Empty;
                }
                tables.Add(table);
            }
            return tables;
        }

        private static List<SQLiteColumnInfo> ReadColumns(SQLiteConnection connection, string tableName)
        {
            List<SQLiteColumnInfo> columns = new List<SQLiteColumnInfo>();
            using SQLiteCommand command = new SQLiteCommand("PRAGMA table_info(" + QuoteSQLiteIdentifier(tableName) + ");", connection);
            using SQLiteDataReader reader = command.ExecuteReader();
            while (reader.Read())
            {
                columns.Add(new SQLiteColumnInfo
                {
                    Name = reader[1].ToString(),
                    DeclaredType = reader[2].ToString(),
                    NotNull = System.Convert.ToInt32(reader[3], CultureInfo.InvariantCulture) != 0,
                    PrimaryKeyOrder = System.Convert.ToInt32(reader[5], CultureInfo.InvariantCulture)
                });
            }
            return columns;
        }

        private static List<SQLiteForeignKeyInfo> ReadForeignKeys(SQLiteConnection connection, string tableName)
        {
            List<SQLiteForeignKeyInfo> foreignKeys = new List<SQLiteForeignKeyInfo>();
            using SQLiteCommand command = new SQLiteCommand("PRAGMA foreign_key_list(" + QuoteSQLiteIdentifier(tableName) + ");", connection);
            using SQLiteDataReader reader = command.ExecuteReader();
            while (reader.Read())
            {
                foreignKeys.Add(new SQLiteForeignKeyInfo
                {
                    Id = System.Convert.ToInt32(reader[0], CultureInfo.InvariantCulture),
                    Sequence = System.Convert.ToInt32(reader[1], CultureInfo.InvariantCulture),
                    ReferencedTable = reader[2].ToString(),
                    ColumnName = reader[3].ToString(),
                    ReferencedColumnName = reader[4].ToString()
                });
            }
            return foreignKeys;
        }

        private static List<SQLiteIndexInfo> ReadIndexes(SQLiteConnection connection, string tableName)
        {
            List<SQLiteIndexInfo> indexes = new List<SQLiteIndexInfo>();
            using SQLiteCommand command = new SQLiteCommand("PRAGMA index_list(" + QuoteSQLiteIdentifier(tableName) + ");", connection);
            using SQLiteDataReader reader = command.ExecuteReader();
            while (reader.Read())
            {
                string indexName = reader[1].ToString();
                if (indexName.StartsWith("sqlite_autoindex_", StringComparison.OrdinalIgnoreCase))
                    continue; // backing structure of a PRIMARY KEY / UNIQUE constraint; nothing to recreate

                indexes.Add(new SQLiteIndexInfo
                {
                    Name = indexName,
                    IsUnique = System.Convert.ToInt32(reader[2], CultureInfo.InvariantCulture) != 0
                });
            }

            foreach (SQLiteIndexInfo index in indexes)
            {
                using SQLiteCommand infoCommand = new SQLiteCommand("PRAGMA index_info(" + QuoteSQLiteIdentifier(index.Name) + ");", connection);
                using SQLiteDataReader infoReader = infoCommand.ExecuteReader();
                while (infoReader.Read())
                    index.Columns.Add(infoReader[2].ToString());
            }
            return indexes;
        }

        // ------------------------------------------------------------------ type resolution

        private static string ResolveSqlServerType(
            SQLiteConnection connection,
            SQLiteTableInfo table,
            SQLiteColumnInfo column,
            IDictionary<string, string> overrides)
        {
            if (overrides != null &&
                overrides.TryGetValue(table.Name + "." + column.Name, out string overridden) &&
                !string.IsNullOrWhiteSpace(overridden))
            {
                string normalized = NormalizeOverrideType(overridden);
                if (normalized != null)
                {
                    column.HasExplicitOverride = true;
                    return normalized;
                }
            }

            return NormalizeDeclaredType(connection, table, column);
        }

        /// <summary>
        /// Normalizes an explicit override value such as "bigint identity", "int" or
        /// "nvarchar(450)". Returns null when the value is not a recognized SQL Server type.
        /// </summary>
        private static string NormalizeOverrideType(string value)
        {
            string v = (value ?? string.Empty).Trim();
            bool identity = v.EndsWith(" identity", StringComparison.OrdinalIgnoreCase);
            if (identity)
                v = v.Substring(0, v.Length - " identity".Length).Trim();

            string type = v;
            string suffix = string.Empty;
            int parenIndex = v.IndexOf('(', StringComparison.Ordinal);
            if (parenIndex >= 0)
            {
                type = v.Substring(0, parenIndex).Trim();
                suffix = v.Substring(parenIndex).Trim();
            }
            string t = type.ToUpperInvariant();

            string mapped = t switch
            {
                "BIT" or "BOOLEAN" => "BIT",
                "DATETIME" or "DATETIME2" or "SMALLDATETIME" => "DATETIME2",
                "DATE" => "DATE",
                "TIME" => "TIME",
                "DATETIMEOFFSET" => "DATETIMEOFFSET",
                "BIGINT" => "BIGINT",
                "INT" or "INTEGER" => "INT",
                "SMALLINT" => "SMALLINT",
                "TINYINT" => "TINYINT",
                "FLOAT" or "DOUBLE" => "FLOAT",
                "REAL" => "REAL",
                "MONEY" => "MONEY",
                "UNIQUEIDENTIFIER" or "GUID" => "UNIQUEIDENTIFIER",
                "NVARCHAR" or "VARCHAR" or "NCHAR" or "CHAR" =>
                    t + (string.IsNullOrWhiteSpace(suffix) ? "(MAX)" : suffix),
                "TEXT" or "NTEXT" or "CLOB" => "NVARCHAR(MAX)",
                "VARBINARY" or "BINARY" =>
                    t + (string.IsNullOrWhiteSpace(suffix) ? "(MAX)" : suffix),
                "BLOB" or "IMAGE" => "VARBINARY(MAX)",
                "DECIMAL" or "NUMERIC" =>
                    t + (string.IsNullOrWhiteSpace(suffix) ? "(18, 2)" : suffix),
                _ => null
            };

            if (mapped == null)
                return null;
            return identity ? mapped + " IDENTITY" : mapped;
        }

        /// <summary>
        /// Maps a declared type that is already (close to) a SQL Server type - the
        /// SqlServerToSQLite converter keeps the original type names, so "bit", "datetime",
        /// "nvarchar(450)", "bigint", "int", ... come back exactly as they were.
        /// </summary>
        private static string NormalizeDeclaredType(SQLiteConnection connection, SQLiteTableInfo table, SQLiteColumnInfo column)
        {
            string declared = (column.DeclaredType ?? string.Empty).Trim();
            string baseType = declared;
            string suffix = string.Empty;
            int parenIndex = declared.IndexOf('(', StringComparison.Ordinal);
            if (parenIndex >= 0)
            {
                baseType = declared.Substring(0, parenIndex).Trim();
                suffix = declared.Substring(parenIndex).Trim();
            }
            string type = baseType.ToUpperInvariant();

            bool isKeyColumn = column.PrimaryKeyOrder > 0 ||
                table.ForeignKeys.Any(foreignKey => foreignKey.ColumnName == column.Name) ||
                table.ReferencedKeyColumns.Contains(column.Name);

            switch (type)
            {
                case "BIT":
                case "BOOLEAN":
                    return "BIT";
                case "DATETIME":
                case "DATETIME2":
                case "SMALLDATETIME":
                    return "DATETIME2";
                case "DATE":
                    return "DATE";
                case "TIME":
                    return "TIME";
                case "DATETIMEOFFSET":
                    return "DATETIMEOFFSET";
                case "BIGINT":
                    return "BIGINT";
                case "INT":
                    return "INT";
                case "INTEGER":
                {
                    // bare INTEGER is the SQLite-native declaration: the original width is
                    // unknown (the forward converter collapses int and bigint for identity
                    // columns) - mark it so the FK width propagation can adjust it later
                    column.AmbiguousInteger = true;
                    string width = ResolveIntegerWidth(connection, table.Name, column.Name);
                    if (IsAutoIncrementPrimaryKey(table, column))
                        return width + " IDENTITY";
                    return width;
                }
                case "SMALLINT":
                {
                    string width = "SMALLINT";
                    if (IsAutoIncrementPrimaryKey(table, column))
                        return width + " IDENTITY";
                    return width;
                }
                case "TINYINT":
                    return "TINYINT";
                case "FLOAT":
                case "DOUBLE":
                    return "FLOAT";
                case "REAL":
                    return "REAL";
                case "MONEY":
                    return "MONEY";
                case "UNIQUEIDENTIFIER":
                case "GUID":
                    return "UNIQUEIDENTIFIER";
                case "NVARCHAR":
                case "VARCHAR":
                case "NCHAR":
                case "CHAR":
                {
                    string length = suffix;
                    if (string.IsNullOrWhiteSpace(length) || length == "(MAX)")
                        return type + (isKeyColumn ? "(450)" : "(MAX)");
                    if (type == "VARCHAR" || type == "CHAR")
                        return "VARCHAR" + length;
                    return type + length;
                }
                case "TEXT":
                case "NTEXT":
                case "CLOB":
                    return "NVARCHAR(MAX)";
                case "VARBINARY":
                case "BINARY":
                case "BLOB":
                case "IMAGE":
                {
                    if (type == "VARBINARY" || type == "BINARY")
                    {
                        if (string.IsNullOrWhiteSpace(suffix))
                            return "VARBINARY(MAX)";
                        return type + suffix;
                    }
                    return "VARBINARY(MAX)";
                }
                case "DECIMAL":
                case "NUMERIC":
                    return string.IsNullOrWhiteSpace(suffix) ? "DECIMAL(18, 2)" : type + suffix;
                default:
                    // unknown declared type - SQLite affinity fallback
                    if (type.Contains("INT")) return ResolveIntegerWidth(connection, table.Name, column.Name);
                    if (type.Contains("CHAR") || type.Contains("CLOB") || type.Contains("TEXT"))
                        return isKeyColumn ? "NVARCHAR(450)" : "NVARCHAR(MAX)";
                    if (type.Contains("BLOB")) return "VARBINARY(MAX)";
                    if (type.Contains("REAL") || type.Contains("FLOA") || type.Contains("DOUB")) return "FLOAT";
                    if (type.Contains("DEC") || type.Contains("NUM")) return "DECIMAL(18, 2)";
                    return isKeyColumn ? "NVARCHAR(450)" : "NVARCHAR(MAX)";
            }
        }

        /// <summary>
        /// True when the column was written by SqlServerToSQLite as an IDENTITY column
        /// ("[column] integer PRIMARY KEY AUTOINCREMENT").
        /// </summary>
        private static bool IsAutoIncrementPrimaryKey(SQLiteTableInfo table, SQLiteColumnInfo column)
        {
            if (column.PrimaryKeyOrder != 1)
                return false;
            string ddl = table.Ddl ?? string.Empty;
            return ddl.IndexOf("AUTOINCREMENT", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>
        /// SQLite cannot distinguish int from bigint; measure the stored values and use INT
        /// when every value fits, otherwise BIGINT. Empty columns default to INT.
        /// </summary>
        private static string ResolveIntegerWidth(SQLiteConnection connection, string tableName, string columnName)
        {            using SQLiteCommand command = new SQLiteCommand(
                "SELECT MIN([" + columnName.Replace("]", "]]", StringComparison.Ordinal) + "]), MAX([" +
                columnName.Replace("]", "]]", StringComparison.Ordinal) + "]) FROM [" +
                tableName.Replace("]", "]]", StringComparison.Ordinal) + "];", connection);
            using SQLiteDataReader reader = command.ExecuteReader();
            if (!reader.Read() || reader.IsDBNull(0) || reader.IsDBNull(1))
                return "INT";

            long min = System.Convert.ToInt64(reader[0], CultureInfo.InvariantCulture);
            long max = System.Convert.ToInt64(reader[1], CultureInfo.InvariantCulture);
            return (min >= int.MinValue && max <= int.MaxValue) ? "INT" : "BIGINT";
        }

        // ------------------------------------------------------------------ FK width propagation

        /// <summary>
        /// Foreign keys require the referencing and referenced columns to have exactly the
        /// same type. Identity primary keys arrive as ambiguous SQLite INTEGER columns while
        /// referencing columns keep their original bigint/int declaration - so walk every FK
        /// edge and widen ambiguous integer columns until both sides agree (BIGINT wins).
        /// </summary>
        private static void PropagateIntegerWidthsAlongForeignKeys(List<SQLiteTableInfo> tables)
        {
            bool changed = true;
            while (changed)
            {
                changed = false;
                foreach (SQLiteTableInfo table in tables)
                {
                    foreach (SQLiteForeignKeyInfo foreignKey in table.ForeignKeys)
                    {
                        SQLiteTableInfo parent = tables.FirstOrDefault(candidate => candidate.Name == foreignKey.ReferencedTable);
                        if (parent == null)
                            continue;
                        SQLiteColumnInfo childColumn = table.Columns.FirstOrDefault(candidate => candidate.Name == foreignKey.ColumnName);
                        SQLiteColumnInfo parentColumn = parent.Columns.FirstOrDefault(candidate => candidate.Name == foreignKey.ReferencedColumnName);
                        if (childColumn == null || parentColumn == null)
                            continue;

                        string childWidth = GetIntegerWidth(childColumn.ResolvedSqlType);
                        string parentWidth = GetIntegerWidth(parentColumn.ResolvedSqlType);
                        if (childWidth == null || parentWidth == null || childWidth == parentWidth)
                            continue;

                        // one side BIGINT, the other INT - widen whichever side is ambiguous
                        if (childWidth == "BIGINT" && parentColumn.AmbiguousInteger)
                        {
                            parentColumn.ResolvedSqlType = WidenInteger(parentColumn.ResolvedSqlType);
                            changed = true;
                        }
                        else if (parentWidth == "BIGINT" && childColumn.AmbiguousInteger)
                        {
                            childColumn.ResolvedSqlType = WidenInteger(childColumn.ResolvedSqlType);
                            changed = true;
                        }
                    }
                }
            }
        }

        private static string GetIntegerWidth(string resolvedSqlType)
        {
            string type = (resolvedSqlType ?? string.Empty).ToUpperInvariant();
            if (type.StartsWith("BIGINT")) return "BIGINT";
            if (type.StartsWith("INT") || type.StartsWith("SMALLINT") || type.StartsWith("TINYINT")) return "INT";
            return null;
        }

        private static string WidenInteger(string resolvedSqlType)
        {
            return resolvedSqlType.ToUpperInvariant().StartsWith("BIGINT")
                ? resolvedSqlType
                : "BIGINT" + (resolvedSqlType.ToUpperInvariant().Contains(" IDENTITY") ? " IDENTITY" : string.Empty);
        }

        // ------------------------------------------------------------------ DDL

        private static void CreateSqlServerTable(SqlConnection connection, SQLiteTableInfo table)
        {
            if (table.Columns.Count == 0)
                return;

            StringBuilder sql = new StringBuilder();
            sql.Append("CREATE TABLE ");
            sql.Append(QuoteSqlServerIdentifier("dbo"));
            sql.Append(".");
            sql.Append(QuoteSqlServerIdentifier(table.Name));
            sql.AppendLine(" (");

            for (int index = 0; index < table.Columns.Count; index++)
            {
                SQLiteColumnInfo column = table.Columns[index];
                sql.Append("    ");
                sql.Append(QuoteSqlServerIdentifier(column.Name));
                sql.Append(" ");
                sql.Append(column.ResolvedSqlType);
                sql.Append(column.NotNull || column.PrimaryKeyOrder > 0 ? " NOT NULL" : " NULL");
                if (index < table.Columns.Count - 1 || table.Columns.Any(candidate => candidate.PrimaryKeyOrder > 0))
                    sql.Append(",");
                sql.AppendLine();
            }

            List<SQLiteColumnInfo> primaryKey = table.Columns
                .Where(column => column.PrimaryKeyOrder > 0)
                .OrderBy(column => column.PrimaryKeyOrder)
                .ToList();
            if (primaryKey.Count > 0)
            {
                sql.Append("    CONSTRAINT ");
                sql.Append(QuoteSqlServerIdentifier("PK_" + table.Name));
                sql.Append(" PRIMARY KEY (");
                sql.Append(string.Join(", ", primaryKey.Select(column => QuoteSqlServerIdentifier(column.Name))));
                sql.AppendLine(")");
            }

            sql.Append(");");
            using SqlCommand command = new SqlCommand(sql.ToString(), connection);
            command.ExecuteNonQuery();
        }

        private static void RecreateIndexes(SqlConnection connection, List<SQLiteTableInfo> tables, Action<int, string> progress)
        {
            int created = 0;
            int total = tables.Sum(table => table.Indexes.Count);
            foreach (SQLiteTableInfo table in tables)
            {
                foreach (SQLiteIndexInfo index in table.Indexes)
                {
                    if (index.Columns.Count == 0)
                        continue;
                    string sql = "CREATE " + (index.IsUnique ? "UNIQUE " : string.Empty) + "INDEX " +
                        QuoteSqlServerIdentifier(index.Name) + " ON " +
                        QuoteSqlServerIdentifier("dbo") + "." + QuoteSqlServerIdentifier(table.Name) +
                        " (" + string.Join(", ", index.Columns.Select(QuoteSqlServerIdentifier)) + ");";
                    using SqlCommand command = new SqlCommand(sql, connection);
                    command.ExecuteNonQuery();
                    created++;
                    progress?.Invoke(80 + created * 5 / Math.Max(1, total), "Created index " + index.Name);
                }
            }
        }

        private static void AddForeignKeys(SqlConnection connection, List<SQLiteTableInfo> tables)
        {
            int constraintIndex = 0;
            int uniqueIndex = 0;
            foreach (SQLiteTableInfo table in tables)
            {
                foreach (IGrouping<string, SQLiteForeignKeyInfo> foreignKeyGroup in table.ForeignKeys
                    .GroupBy(foreignKey => foreignKey.Id + "|" + foreignKey.ReferencedTable))
                {
                    List<SQLiteForeignKeyInfo> foreignKeys = foreignKeyGroup.OrderBy(foreignKey => foreignKey.Sequence).ToList();
                    SQLiteForeignKeyInfo firstForeignKey = foreignKeys[0];
                    SQLiteTableInfo referencedTable = tables.FirstOrDefault(candidate => candidate.Name == firstForeignKey.ReferencedTable);
                    if (referencedTable == null)
                        throw new InvalidOperationException("Referenced table [" + firstForeignKey.ReferencedTable + "] was not found.");

                    List<string> referencedColumns = foreignKeys.Select(foreignKey => foreignKey.ReferencedColumnName).ToList();
                    bool referencesPrimaryKey = referencedTable.Columns
                        .Where(column => column.PrimaryKeyOrder > 0)
                        .OrderBy(column => column.PrimaryKeyOrder)
                        .Select(column => column.Name)
                        .SequenceEqual(referencedColumns, StringComparer.OrdinalIgnoreCase);

                    if (!referencesPrimaryKey)
                    {
                        string indexName = "UX_" + firstForeignKey.ReferencedTable + "_" + uniqueIndex++;
                        string indexSql = "CREATE INDEX " + QuoteSqlServerIdentifier(indexName) + " ON " +
                            QuoteSqlServerIdentifier("dbo") + "." + QuoteSqlServerIdentifier(firstForeignKey.ReferencedTable) +
                            " (" + string.Join(", ", referencedColumns.Select(QuoteSqlServerIdentifier)) + ");";
                        using SqlCommand indexCommand = new SqlCommand(indexSql, connection);
                        indexCommand.ExecuteNonQuery();
                    }

                    string constraintName = "FK_" + table.Name + "_" + firstForeignKey.ReferencedTable + "_" + constraintIndex++;
                    string sql = "ALTER TABLE " + QuoteSqlServerIdentifier("dbo") + "." + QuoteSqlServerIdentifier(table.Name) +
                        " ADD CONSTRAINT " + QuoteSqlServerIdentifier(constraintName) + " FOREIGN KEY (" +
                        string.Join(", ", foreignKeys.Select(foreignKey => QuoteSqlServerIdentifier(foreignKey.ColumnName))) +
                        ") REFERENCES " + QuoteSqlServerIdentifier("dbo") + "." +
                        QuoteSqlServerIdentifier(firstForeignKey.ReferencedTable) + " (" +
                        string.Join(", ", referencedColumns.Select(QuoteSqlServerIdentifier)) + ");";
                    using SqlCommand command = new SqlCommand(sql, connection);
                    command.ExecuteNonQuery();
                }
            }
        }

        // ------------------------------------------------------------------ data copy

        private static void CopyRows(SQLiteConnection sqlite, SqlConnection sqlServer, SQLiteTableInfo table)
        {
            if (table.Columns.Count == 0)
                return;

            bool usesIdentity = table.Columns.Any(column => column.ResolvedSqlType.EndsWith(" IDENTITY", StringComparison.OrdinalIgnoreCase));

            string selectSql = "SELECT " + string.Join(", ", table.Columns.Select(column => QuoteSQLiteIdentifier(column.Name))) +
                               " FROM " + QuoteSQLiteIdentifier(table.Name) + ";";
            string qualifiedTable = QuoteSqlServerIdentifier("dbo") + "." + QuoteSqlServerIdentifier(table.Name);
            string insertSql = "INSERT INTO " + qualifiedTable +
                               " (" + string.Join(", ", table.Columns.Select(column => QuoteSqlServerIdentifier(column.Name))) + ") VALUES (" +
                               string.Join(", ", table.Columns.Select((_, index) => "@p" + index.ToString(CultureInfo.InvariantCulture))) + ");";

            using SQLiteCommand select = new SQLiteCommand(selectSql, sqlite);
            using SQLiteDataReader reader = select.ExecuteReader();
            using SqlTransaction transaction = sqlServer.BeginTransaction();
            using SqlCommand insert = new SqlCommand(insertSql, sqlServer, transaction);

            if (usesIdentity)
            {
                using SqlCommand allow = new SqlCommand("SET IDENTITY_INSERT " + qualifiedTable + " ON;", sqlServer, transaction);
                allow.ExecuteNonQuery();
            }

            for (int index = 0; index < table.Columns.Count; index++)
            {
                SqlParameter parameter = insert.Parameters.Add("@p" + index.ToString(CultureInfo.InvariantCulture),
                    GetSqlServerDbType(table.Columns[index].ResolvedSqlType));
                if (parameter.SqlDbType == SqlDbType.NVarChar || parameter.SqlDbType == SqlDbType.VarBinary)
                    parameter.Size = -1;
            }

            long rows = 0;
            while (reader.Read())
            {
                for (int index = 0; index < table.Columns.Count; index++)
                {
                    object value = reader[index];
                    insert.Parameters[index].Value = value == DBNull.Value
                        ? DBNull.Value
                        : NormalizeValue(value, table.Columns[index].ResolvedSqlType);
                }
                insert.ExecuteNonQuery();
                rows++;
            }

            if (usesIdentity)
            {
                using SqlCommand deny = new SqlCommand("SET IDENTITY_INSERT " + qualifiedTable + " OFF;", sqlServer, transaction);
                deny.ExecuteNonQuery();
            }

            transaction.Commit();
            table.CopiedRowCount = rows;
        }

        private static void ReseedIdentities(SqlConnection connection, List<SQLiteTableInfo> tables)
        {
            foreach (SQLiteTableInfo table in tables)
            {
                if (!table.Columns.Any(column => column.ResolvedSqlType.EndsWith(" IDENTITY", StringComparison.OrdinalIgnoreCase)))
                    continue;

                string identityColumn = table.Columns
                    .First(column => column.ResolvedSqlType.EndsWith(" IDENTITY", StringComparison.OrdinalIgnoreCase)).Name;
                string qualifiedTable = QuoteSqlServerIdentifier("dbo") + "." + QuoteSqlServerIdentifier(table.Name);

                using SqlCommand maxCommand = new SqlCommand(
                    "SELECT MAX(" + QuoteSqlServerIdentifier(identityColumn) + ") FROM " + qualifiedTable + ";", connection);
                object max = maxCommand.ExecuteScalar();

                string reseedSql = max == null || max == DBNull.Value
                    ? "DBCC CHECKIDENT ('" + qualifiedTable + "', RESEED, 0);"
                    : "DBCC CHECKIDENT ('" + qualifiedTable + "', RESEED, " + System.Convert.ToInt64(max, CultureInfo.InvariantCulture) + ");";
                using SqlCommand reseed = new SqlCommand(reseedSql, connection);
                reseed.ExecuteNonQuery();
            }
        }

        // ------------------------------------------------------------------ value conversion

        private static object NormalizeValue(object value, string resolvedSqlType)
        {
            string type = resolvedSqlType.ToUpperInvariant();

            if (type.StartsWith("INT") || type.StartsWith("SMALLINT") || type.StartsWith("TINYINT"))
            {
                if (value is bool b) return b ? 1 : 0;
                return System.Convert.ToInt32(value, CultureInfo.InvariantCulture);
            }

            if (type.StartsWith("BIGINT"))
            {
                if (value is bool b) return b ? 1L : 0L;
                return System.Convert.ToInt64(value, CultureInfo.InvariantCulture);
            }

            if (type.StartsWith("BIT"))
            {
                if (value is bool) return value;
                return System.Convert.ToBoolean(value, CultureInfo.InvariantCulture);
            }

            if (type.StartsWith("DATETIME") || type.StartsWith("SMALLDATETIME"))
            {
                if (value is DateTime) return value;
                return DateTime.Parse(System.Convert.ToString(value, CultureInfo.InvariantCulture), CultureInfo.CurrentCulture);
            }

            if (type.StartsWith("DATE"))
            {
                if (value is DateTime) return value;
                return DateTime.Parse(System.Convert.ToString(value, CultureInfo.InvariantCulture), CultureInfo.CurrentCulture).Date;
            }

            if (type.StartsWith("DATETIMEOFFSET"))
            {
                if (value is DateTimeOffset) return value;
                if (value is DateTime moment2) return new DateTimeOffset(moment2);
                return DateTimeOffset.Parse(System.Convert.ToString(value, CultureInfo.InvariantCulture), CultureInfo.CurrentCulture);
            }

            if (type == "TIME")
            {
                if (value is TimeSpan span) return span;
                if (value is DateTime moment) return moment.TimeOfDay;
                return TimeSpan.Parse(System.Convert.ToString(value, CultureInfo.InvariantCulture), CultureInfo.CurrentCulture);
            }

            if (type.StartsWith("NVARCHAR") || type.StartsWith("VARCHAR") || type.StartsWith("NCHAR") || type.StartsWith("CHAR") ||
                type.StartsWith("NTEXT") || type.StartsWith("TEXT"))
            {
                if (value is string) return value;
                if (value is DateTime moment) return moment.ToString(CultureInfo.InvariantCulture);
                return System.Convert.ToString(value, CultureInfo.InvariantCulture);
            }

            if (type.StartsWith("UNIQUEIDENTIFIER"))
            {
                if (value is Guid) return value;
                return new Guid(System.Convert.ToString(value, CultureInfo.InvariantCulture));
            }

            if (type.StartsWith("FLOAT") || type.StartsWith("REAL"))
            {
                if (value is double) return value;
                return System.Convert.ToDouble(value, CultureInfo.InvariantCulture);
            }

            if (type.StartsWith("DECIMAL") || type.StartsWith("NUMERIC") || type.StartsWith("MONEY"))
            {
                if (value is decimal) return value;
                return System.Convert.ToDecimal(value, CultureInfo.InvariantCulture);
            }

            if (type.StartsWith("VARBINARY") || type.StartsWith("BINARY"))
                return value is byte[] ? value : System.Convert.FromBase64String(System.Convert.ToString(value, CultureInfo.InvariantCulture));

            return value;
        }

        private static SqlDbType GetSqlServerDbType(string resolvedSqlType)
        {
            string type = resolvedSqlType.ToUpperInvariant();

            if (type.StartsWith("BIGINT")) return SqlDbType.BigInt;
            if (type.StartsWith("INT") || type.StartsWith("SMALLINT") || type.StartsWith("TINYINT")) return SqlDbType.Int;
            if (type.StartsWith("BIT")) return SqlDbType.Bit;
            if (type.StartsWith("DATETIME") || type.StartsWith("SMALLDATETIME")) return SqlDbType.DateTime2;
            if (type.StartsWith("DATE")) return SqlDbType.Date;
            if (type.StartsWith("DATETIMEOFFSET")) return SqlDbType.DateTimeOffset;
            if (type == "TIME") return SqlDbType.Time;
            if (type.StartsWith("NVARCHAR")) return SqlDbType.NVarChar;
            if (type.StartsWith("VARCHAR") || type.StartsWith("TEXT")) return SqlDbType.VarChar;
            if (type.StartsWith("NCHAR") || type.StartsWith("NTEXT")) return SqlDbType.NChar;
            if (type.StartsWith("CHAR")) return SqlDbType.Char;
            if (type.StartsWith("UNIQUEIDENTIFIER")) return SqlDbType.UniqueIdentifier;
            if (type.StartsWith("FLOAT")) return SqlDbType.Float;
            if (type.StartsWith("REAL")) return SqlDbType.Real;
            if (type.StartsWith("DECIMAL") || type.StartsWith("NUMERIC")) return SqlDbType.Decimal;
            if (type.StartsWith("MONEY")) return SqlDbType.Money;
            if (type.StartsWith("VARBINARY") || type.StartsWith("BLOB") || type.StartsWith("IMAGE")) return SqlDbType.VarBinary;
            if (type.StartsWith("BINARY")) return SqlDbType.Binary;
            return SqlDbType.NVarChar;
        }

        // ------------------------------------------------------------------ helpers

        private static void EnsureTargetIsEmpty(SqlConnection connection)
        {
            using SqlCommand command = new SqlCommand(
                "SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_SCHEMA = 'dbo' AND TABLE_TYPE = 'BASE TABLE';", connection);
            int existing = System.Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
            if (existing > 0)
                throw new InvalidOperationException(
                    "The target SQL Server database already contains " + existing +
                    " table(s). Use an empty database so that types are created from the declared SQLite types.");
        }

        private static void EnsureDatabaseExists(SqlConnectionStringBuilder builder)
        {
            if (string.IsNullOrWhiteSpace(builder.InitialCatalog))
                return;

            SqlConnectionStringBuilder masterBuilder = new SqlConnectionStringBuilder(builder.ConnectionString)
            {
                InitialCatalog = "master"
            };

            using SqlConnection connection = new SqlConnection(masterBuilder.ConnectionString);
            connection.Open();
            using SqlCommand command = new SqlCommand(
                "SELECT DB_ID(@db)", connection);
            command.Parameters.AddWithValue("@db", builder.InitialCatalog);
            object result = command.ExecuteScalar();
            if (result == null || result == DBNull.Value)
            {
                using SqlCommand create = new SqlCommand(
                    "CREATE DATABASE " + QuoteSqlServerIdentifier(builder.InitialCatalog) + ";", connection);
                create.ExecuteNonQuery();
            }
        }

        private static IDictionary<string, string> ExtractPrimaryKeys(
            IDictionary<string, string> overrides,
            out IDictionary<string, string> columnOverrides)
        {
            IDictionary<string, string> primaryKeys = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            columnOverrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, string> pair in overrides)
            {
                if (pair.Key.StartsWith("@primarykey:", StringComparison.OrdinalIgnoreCase))
                    primaryKeys[pair.Key.Substring("@primarykey:".Length)] = pair.Value;
                else
                    columnOverrides[pair.Key] = pair.Value;
            }
            return primaryKeys;
        }

        private static IDictionary<string, string> LoadOverrides(string sqlitePath, IDictionary<string, string> explicitOverrides)
        {
            IDictionary<string, string> overrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (explicitOverrides != null)
            {
                foreach (KeyValuePair<string, string> pair in explicitOverrides)
                    overrides[pair.Key] = pair.Value;
            }

            string sidecar = sqlitePath.TrimEnd() + ".typemap.json";
            if (File.Exists(sidecar))
            {
                using JsonDocument document = JsonDocument.Parse(File.ReadAllText(sidecar));
                foreach (JsonProperty property in document.RootElement.EnumerateObject())
                    overrides[property.Name] = property.Value.GetString();
            }
            return overrides;
        }

        private static string CreateSQLiteConnectionString(string sqlitePath)
        {
            SQLiteConnectionStringBuilder builder = new SQLiteConnectionStringBuilder { DataSource = sqlitePath };
            return builder.ConnectionString;
        }

        private static string QuoteSQLiteIdentifier(string identifier)
        {
            return "[" + identifier.Replace("]", "]]", StringComparison.Ordinal) + "]";
        }

        private static string QuoteSqlServerIdentifier(string identifier)
        {
            return "[" + identifier.Replace("]", "]]", StringComparison.Ordinal) + "]";
        }

        private sealed class SQLiteTableInfo
        {
            public string Name { get; set; }
            public string Ddl { get; set; }
            public List<SQLiteColumnInfo> Columns { get; set; } = new List<SQLiteColumnInfo>();
            public List<SQLiteForeignKeyInfo> ForeignKeys { get; set; } = new List<SQLiteForeignKeyInfo>();
            public List<SQLiteIndexInfo> Indexes { get; set; } = new List<SQLiteIndexInfo>();
            public HashSet<string> ReferencedKeyColumns { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            public long CopiedRowCount { get; set; }
        }

        private sealed class SQLiteColumnInfo
        {
            public string Name { get; set; }
            public string DeclaredType { get; set; }
            public bool NotNull { get; set; }
            public int PrimaryKeyOrder { get; set; }
            public string ResolvedSqlType { get; set; }
            public bool AmbiguousInteger { get; set; }
            public bool HasExplicitOverride { get; set; }
        }

        private sealed class SQLiteForeignKeyInfo
        {
            public int Id { get; set; }
            public int Sequence { get; set; }
            public string ReferencedTable { get; set; }
            public string ColumnName { get; set; }
            public string ReferencedColumnName { get; set; }
        }

        private sealed class SQLiteIndexInfo
        {
            public string Name { get; set; }
            public bool IsUnique { get; set; }
            public List<string> Columns { get; } = new List<string>();
        }
    }
}
