﻿using System.Threading.Tasks;
using System;
using System.Data;
using System.Data.SqlClient;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Collections.Generic;
using static SQLConfirm.TemplateGeneration.Logic.OutputHelper;
using System.Linq;
using SQLConfirm.TemplateGeneration.Models;

namespace SQLConfirm.TemplateGeneration.Logic
{
    public class Generator
    {
        private readonly Options _options;
        private const string _getColumnsScript = "SQLConfirm.TemplateGeneration.Scripts.GetColumns.sql";

        public Generator(Options options) => _options = options;

        public async Task GenerateFileAsync()
        {
            string connectionString = GetConnectionString();
            if (connectionString == null)
            {
                return;
            }

            (string schemaName, string tableName) = DetermineSchemaAndTable(_options.SchemaName, _options.TableName);

            DataTable columns = await GetColumnsAsync(connectionString, schemaName, tableName);

            List<ColumnDefinition> processedColumns = columns?.AsEnumerable().Select(p => new ColumnDefinition(p)).ToList();

            if (processedColumns == null || !processedColumns.Any())
            {
                WriteError($"Cannot find table: {_options.SchemaName}.{_options.TableName}");
                return;
            }

            IEnumerable<IGrouping<string, ColumnDefinition>> grouped = processedColumns.GroupBy(p => p.TableName);
            foreach (IGrouping<string, ColumnDefinition> group in grouped)
            {
                WriteSuccess($"Found table ({schemaName}.{group.Key}) and {group.Count()} column{(group.Count() == 1 ? "" : "s")}");
            }
            foreach (IGrouping<string, ColumnDefinition> group in grouped)
            {
                GenerateFile(group, schemaName, group.Key);
            }
        }

        private (string, string) DetermineSchemaAndTable(string schemaName, string tableName)
        {
            if (schemaName == null && tableName.Contains("."))
            {
                Queue<string> parts = new Queue<string>(tableName.Split(new string[] { "." }, StringSplitOptions.RemoveEmptyEntries));
                schemaName = parts.Dequeue();
                tableName = string.Join(".", parts);
            }
            else
            {
                schemaName ??= "dbo";
            }

            schemaName = schemaName.Trim('[').Trim(']');
            if (tableName.Contains("*"))
            {
                tableName = tableName.Replace("*", "%");
            }
            else
            {
                tableName = tableName.Trim('[').Trim(']');
            }

            return (schemaName, tableName);
        }

        private void GenerateFile(IEnumerable<ColumnDefinition> processedColumns, string schemaName, string tableName)
        {
            (string classDefinition, string className) = GenerateClass(processedColumns, schemaName, tableName);

            if (_options.DryRun)
            {
                WriteSuccess("Class definition generated.  Outputting...");
                WriteSuccess(classDefinition);
            }
            else
            {
                WriteSuccess("Class definition generated.  Saving...");

                string target = (_options.Destination ?? Directory.GetCurrentDirectory()) + $@"\{className}.cs";

                if (File.Exists(target) && !_options.Overwrite)
                {
                    WriteError($"The file ({target}) already exists");
                    return;
                }

                File.WriteAllText(target, classDefinition);

                WriteSuccess($"File written to {target}");
            }
        }

        private string GetConnectionString()
        {
            if (string.IsNullOrWhiteSpace(_options.ConnectionString) && string.IsNullOrWhiteSpace(_options.DatabaseName))
            {
                WriteError("Either -c, --connectionString or -d, --databaseName needs to be supplied");
                return null;
            }

            string connectionString = _options.ConnectionString ?? $"SERVER=(local);DATABASE={_options.DatabaseName};Integrated Security=true;Connection Timeout=30;";

            WriteSuccess($"Using connection: {connectionString}");

            return connectionString;
        }

        private async Task<DataTable> GetColumnsAsync(string connectionString, string schemaName, string tableName)
        {
            using SqlConnection sqlConnection = new SqlConnection(connectionString);
            await sqlConnection.OpenAsync();
            using DataSet ds = new DataSet
            {
                Locale = CultureInfo.InvariantCulture
            };

            using (SqlCommand command = new SqlCommand(await ReadResource(_getColumnsScript), sqlConnection))
            {
                command.CommandType = CommandType.Text;
                command.Parameters.Add(new SqlParameter("SchemaName", schemaName));
                command.Parameters.Add(new SqlParameter("TableName", tableName));

                using (SqlDataAdapter adapter = new SqlDataAdapter(command))
                {
                    adapter.Fill(ds);
                }
            }
            if (ds.Tables.Count > 0 && ds.Tables[0].Rows.Count > 0)
            {
                return ds.Tables[0];
            }

            await sqlConnection.CloseAsync();

            return null;
        }

        private async Task<string> ReadResource(string resourceName)
        {
            using Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName);
            using StreamReader reader = new StreamReader(stream);

            return await reader.ReadToEndAsync();
        }

        private (string, string) GenerateClass(IEnumerable<ColumnDefinition> processedColumns, string schemaName, string tableName)
        {
            ColumnDefinition identityColumn = processedColumns.FirstOrDefault(p => p.IsIdentity);

            string className = $"{tableName.Replace(' ', '_')}Template";

            List<string> usings = new List<string>
            {
                "using SQLConfirm.Core.Data;",
                "using SQLConfirm.Core.Templates;"
            };

            if (processedColumns.Any(p => p.TypeRequiresSystem))
            {
                usings.Insert(0, "using System;");
            }

            if (processedColumns.Any(p => p.ReferencesIdentity))
            {
                usings.Add("using SQLConfirm.Core.Templates.Abstract;");
            }

            if (processedColumns.Any(p => p.RequiredPlaceholder))
            {
                usings.Add("using SQLConfirm.Core.Templates.Placeholders;");
            }

            string output = $@"{string.Join($"{Environment.NewLine}", usings)}

namespace {_options.Namespace ?? "SQLConfirm.Templates"}
{{
    public class {className}: Base{(identityColumn != null ? "Identity" : "Simple")}Template<{className}>
    {{
        public override string TableName => ""[{schemaName}].[{tableName}]"";
        {(identityColumn != null ? @$"{Environment.NewLine}        public override string IdentityColumnName => ""{identityColumn.ColumnName}"";{Environment.NewLine}" : "")}
        public override DataSetRow DefaultData => new DataSetRow
        {{
            {string.Join($",{Environment.NewLine}            ", processedColumns.Where(p => p.RequiresDefaultData).Select(p => p.ToDefaultData()))}
        }};

{string.Join($"{Environment.NewLine}", processedColumns.Select(p => p.ToFluentRow("        ", $"{className}")))}
    }}
}}";

            return (output, className);
        }
    }
}
