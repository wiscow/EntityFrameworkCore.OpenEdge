﻿using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Data.Odbc;
using System.Linq;
using System.Text;
using EntityFrameworkCore.OpenEdge.Extensions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Scaffolding;
using Microsoft.EntityFrameworkCore.Scaffolding.Metadata;

namespace EntityFrameworkCore.OpenEdge.Scaffolding.Internal
{
    public class OpenEdgeDatabaseModelFactory : DatabaseModelFactory
    {
        protected internal const string DatabaseModelDefaultSchema = "pub";
#pragma warning disable IDE0052 // Ungelesene private Member entfernen
        private readonly IDiagnosticsLogger<DbLoggerCategory.Scaffolding> _logger;
#pragma warning restore IDE0052 // Ungelesene private Member entfernen



        public OpenEdgeDatabaseModelFactory(IDiagnosticsLogger<DbLoggerCategory.Scaffolding> logger)
        {
            _logger = logger;
        }

        public override DatabaseModel Create(string connectionString, DatabaseModelFactoryOptions options)
        {
            return Create(connectionString, options.Tables, options.Schemas);
        }

        public override DatabaseModel Create(DbConnection connection, DatabaseModelFactoryOptions options)
        {
            return Create(connection, options.Tables, options.Schemas);
        }

        public DatabaseModel Create(string connectionString, IEnumerable<string> tables, IEnumerable<string> schemas)
        {
            using (var connection = new OdbcConnection(connectionString))
            {
                return Create(connection, tables, schemas);
            }
        }

        public DatabaseModel Create(DbConnection connection, IEnumerable<string> tables, IEnumerable<string> schemas)
        {
            var databaseModel = new DatabaseModel();

            var connectionStartedOpen = connection.State == ConnectionState.Open;
            if (!connectionStartedOpen)
            {
                connection.Open();
            }

            try
            {
                databaseModel.DefaultSchema = DatabaseModelDefaultSchema;

                GetTables(connection, null, databaseModel);

                return databaseModel;
            }
            finally
            {
                if (!connectionStartedOpen)
                {
                    connection.Close();
                }
            }
        }

        private void GetTables(
            DbConnection connection,
            Func<string, string> tableFilter,
            DatabaseModel databaseModel)
        {
            using (var command = connection.CreateCommand())
            {
                var commandText = @"
SELECT
    t.""_File-Name"" AS 'name'
FROM ""pub"".""_File"" t ";

                var filter =
                    $"WHERE t.\"_File-Name\" <> '{HistoryRepository.DefaultTableName}' {(tableFilter != null ? $" AND {tableFilter("t.\"_file-name\"")}" : "")}";

                command.CommandText = commandText + filter;

                using (var reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        var name = reader.GetValueOrDefault<string>("name");

                        var table = new DatabaseTable
                        {
                            Schema = DatabaseModelDefaultSchema,
                            Name = name
                        };

                        databaseModel.Tables.Add(table);
                    }
                }

                GetColumns(connection, filter, databaseModel);
            }
        }

        private void GetColumns(
            DbConnection connection,
            string tableFilter,
            DatabaseModel databaseModel)
        {
            using (var command = connection.CreateCommand())
            {
                command.CommandText = new StringBuilder()
                    .AppendLine("SELECT")
                    .AppendLine("   f.\"_Field-Name\",")
                    .AppendLine("   f.\"_Data-Type\",")
                    .AppendLine("   t.\"_File-Name\" as name,")
                    .AppendLine("   t.\"_Prime-Index\" as primeindex,")
                    .AppendLine("   f.\"_Mandatory\",")
                    .AppendLine("   if.\"_index-recid\" as identity,")
                    .AppendLine("   f.\"_initial\"")
                    .AppendLine("FROM pub.\"_field\" f")
                    .AppendLine("INNER JOIN pub.\"_File\" t ")
                    .AppendLine("ON t.rowid = f.\"_File-recid\"")
                    .AppendLine("LEFT JOIN pub.\"_index-field\" if ")
                    .AppendLine("ON if.\"_index-recid\" = t.\"_Prime-Index\" AND if.\"_field-recid\" = f.rowid")
                    .AppendLine(tableFilter)
                    .AppendLine("ORDER BY f.\"_Order\"")
                    .ToString();
                
                using (var reader = command.ExecuteReader())
                {
                    var tableColumnGroups = reader.Cast<DbDataRecord>()
                        .GroupBy(
                            ddr => ddr.GetValueOrDefault<string>("name"));
                    
                    foreach (var tableColumnGroup in tableColumnGroups)
                    {
                        var tableName = tableColumnGroup.Key;
                        var table = databaseModel.Tables.Single(t => t.Schema == DatabaseModelDefaultSchema && t.Name == tableName);

                        var primaryKey = new DatabasePrimaryKey
                        {
                            Table = table,
                            Name = table + "_PK"
                        };

                        table.PrimaryKey = primaryKey;

                        foreach (var dataRecord in tableColumnGroup)
                        {
                            var columnName = dataRecord.GetValueOrDefault<string>("_Field-Name");
                            var dataTypeName = dataRecord.GetValueOrDefault<string>("_Data-Type");
                            var isNullable = !dataRecord.GetValueOrDefault<bool>("_Mandatory");
                            var isIdentity = dataRecord.GetValueOrDefault<long?>("identity") != null;
                            var defaultValue = !isIdentity ? dataRecord.GetValueOrDefault<object>("_initial") : null;

                            var storeType = dataTypeName;
                            if (string.IsNullOrWhiteSpace(defaultValue?.ToString()))
                            {
                                defaultValue = null;
                            }
                            
                            table.Columns.Add(new DatabaseColumn
                                {
                                    Table = table,
                                    Name = columnName,
                                    StoreType = storeType,
                                    IsNullable = isNullable,
                                    DefaultValueSql = defaultValue?.ToString(),
                                    ValueGenerated = default
                                });


                            if (isIdentity)
                            {
                                var column = table.Columns.FirstOrDefault(c => c.Name == columnName)
                                             ?? table.Columns.FirstOrDefault(c => c.Name.Equals(columnName, StringComparison.OrdinalIgnoreCase));
                                primaryKey.Columns.Add(column);
                            }
                        }

                        // OpenEdge supports having tables with no primary key
                        if (!primaryKey.Columns.Any())
                        {
                            databaseModel.Tables.Remove(table);
                        }
                    }
                }
            }
        }
    }
}