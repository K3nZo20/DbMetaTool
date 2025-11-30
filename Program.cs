using FirebirdSql.Data.FirebirdClient;
using System;
using System.Data;
using System.Text;
using System.Text.Json;

namespace DbMetaTool
{
    public static class Program
    {
        public static int Main(string[] args)
        {
            if (args.Length == 0)
            {
                Console.WriteLine("Użycie:");
                Console.WriteLine("  build-db --db-dir <ścieżka> --scripts-dir <ścieżka>");
                Console.WriteLine("  export-scripts --connection-string <connStr> --output-dir <ścieżka>");
                Console.WriteLine("  update-db --connection-string <connStr> --scripts-dir <ścieżka>");
                return 1;
            }

            try
            {
                var command = args[0].ToLowerInvariant();

                switch (command)
                {
                    case "build-db":
                        BuildDatabase(GetArgValue(args, "--db-dir"), GetArgValue(args, "--scripts-dir"));
                        Console.WriteLine("Baza danych została zbudowana pomyślnie.");
                        break;

                    case "export-scripts":
                        ExportScripts(GetArgValue(args, "--connection-string"), GetArgValue(args, "--output-dir"));
                        Console.WriteLine("Skrypty zostały wyeksportowane pomyślnie.");
                        break;

                    case "update-db":
                        UpdateDatabase(GetArgValue(args, "--connection-string"), GetArgValue(args, "--scripts-dir"));
                        Console.WriteLine("Baza danych została zaktualizowana pomyślnie.");
                        break;

                    default:
                        Console.WriteLine($"Nieznane polecenie: {command}");
                        return 1;
                }

                return 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine("Błąd: " + ex.Message);
                return -1;
            }
        }

        private static string GetArgValue(string[] args, string name)
        {
            int idx = Array.IndexOf(args, name);
            if (idx == -1 || idx + 1 >= args.Length)
                throw new ArgumentException($"Brak wymaganego parametru {name}");
            return args[idx + 1];
        }

        public static void BuildDatabase(string databaseDirectory, string scriptsDirectory)
        {
            Directory.CreateDirectory(databaseDirectory);
            string dbPath = Path.Combine(databaseDirectory, "database.fdb");

            var connectionString = new FbConnectionStringBuilder
            {
                Database = dbPath,
                UserID = "SYSDBA",
                Password = "masterkey",
                ServerType = FbServerType.Default,
                DataSource = "localHost",
                Charset = "UTF8"
            }.ToString();

            if (!File.Exists(dbPath))
                FbConnection.CreateDatabase(connectionString);

            using var connection = new FbConnection(connectionString);
            connection.Open();

            string[] executionOrder = { "domains.sql", "tables.sql", "procedures.sql" };

            foreach (var scriptName in executionOrder)
            {
                string filePath = Path.Combine(scriptsDirectory, scriptName);
                if (!File.Exists(filePath)) continue;

                string sql = File.ReadAllText(filePath);
                var commands = sql.Split(';', StringSplitOptions.RemoveEmptyEntries);

                foreach (var cmd in commands)
                {
                    string trimmed = cmd.Trim();
                    if (string.IsNullOrWhiteSpace(trimmed)) continue;
                    using var command = new FbCommand(trimmed, connection);
                    command.ExecuteNonQuery();
                }

                Console.WriteLine($"Executed: {scriptName}");
            }

            string metadataPath = Path.Combine(scriptsDirectory, "metadata.json");
            if (File.Exists(metadataPath))
            {
                Console.WriteLine("\n=== RAPORT METADANYCH ===");
                string json = File.ReadAllText(metadataPath);
                Console.WriteLine(json);
            }
            else
            {
                Console.WriteLine("\nBrak pliku metadata.json w katalogu skryptów.");
            }
        }

        public static void ExportScripts(string connectionString, string outputDirectory)
        {
            Directory.CreateDirectory(outputDirectory);

            using var connection = new FbConnection(connectionString);
            connection.Open();

            //Domeny
            var domainsTable = new DataTable();
            using (var cmd = new FbCommand(
                @"SELECT TRIM(RDB$FIELD_NAME) AS FIELD_NAME,
                     RDB$FIELD_TYPE,
                     RDB$FIELD_LENGTH,
                     RDB$CHARACTER_LENGTH,
                     RDB$DEFAULT_SOURCE,
                     RDB$VALIDATION_SOURCE
                  FROM RDB$FIELDS
                  WHERE RDB$SYSTEM_FLAG = 0
                    AND RDB$FIELD_NAME NOT LIKE 'RDB$%'", connection))
            using (var adapter = new FbDataAdapter(cmd))
            {
                adapter.Fill(domainsTable);
            }

            var sbDomains = new StringBuilder();
            var domains = new List<object>();
            foreach (DataRow d in domainsTable.Rows)
            {
                string name = d["FIELD_NAME"].ToString();
                int fieldType = d["RDB$FIELD_TYPE"] == DBNull.Value ? -1 : Convert.ToInt32(d["RDB$FIELD_TYPE"]);
                int length = d["RDB$FIELD_LENGTH"] == DBNull.Value ? 0 : Convert.ToInt32(d["RDB$FIELD_LENGTH"]);
                int charLength = d["RDB$CHARACTER_LENGTH"] == DBNull.Value ? 0 : Convert.ToInt32(d["RDB$CHARACTER_LENGTH"]);

                string sqlType = MapFieldType(fieldType, length, charLength);

                sbDomains.AppendLine($"CREATE DOMAIN \"{name}\" AS {sqlType};");
                domains.Add(new { Name = name, Type = sqlType });
            }
            File.WriteAllText(Path.Combine(outputDirectory, "domains.sql"), sbDomains.ToString());

            //Tabele
            var tablesTable = new DataTable();
            using (var cmd = new FbCommand(
                @"SELECT TRIM(RDB$RELATION_NAME) AS RELATION_NAME
                  FROM RDB$RELATIONS
                  WHERE RDB$SYSTEM_FLAG = 0 AND RDB$VIEW_BLR IS NULL", connection))
            using (var adapter = new FbDataAdapter(cmd))
            {
                adapter.Fill(tablesTable);
            }

            var sbTables = new StringBuilder();
            var tablesList = new List<object>();

            foreach (DataRow tableRow in tablesTable.Rows)
            {
                string tableName = tableRow["RELATION_NAME"].ToString();
                sbTables.AppendLine($"CREATE TABLE \"{tableName}\" (");

                using var colCmd = new FbCommand(
                    @"SELECT TRIM(rf.RDB$FIELD_NAME) AS FIELD_NAME,
                             TRIM(rf.RDB$FIELD_SOURCE) AS FIELD_SOURCE,
                             f.RDB$FIELD_TYPE,
                             f.RDB$FIELD_LENGTH,
                             f.RDB$CHARACTER_LENGTH
                      FROM RDB$RELATION_FIELDS rf
                      JOIN RDB$FIELDS f ON rf.RDB$FIELD_SOURCE = f.RDB$FIELD_NAME
                      WHERE rf.RDB$RELATION_NAME = @name
                      ORDER BY rf.RDB$FIELD_POSITION", connection);
                colCmd.Parameters.AddWithValue("@name", tableName);

                using var reader = colCmd.ExecuteReader();
                var colLines = new List<string>();
                var columnList = new List<object>();

                while (reader.Read())
                {
                    string colName = reader.GetString(0);
                    string fieldSource = reader.GetString(1);

                    string sqlType;
                    if (!fieldSource.StartsWith("RDB$"))
                    {
                        sqlType = $"\"{fieldSource}\"";
                    }
                    else
                    {
                        int fieldType = reader.IsDBNull(2) ? -1 : reader.GetInt32(2);
                        int length = reader.IsDBNull(3) ? 0 : reader.GetInt32(3);
                        int charLength = reader.IsDBNull(4) ? 0 : reader.GetInt32(4);

                        sqlType = MapFieldType(fieldType, length, charLength);
                    }

                    colLines.Add($"\"{colName}\" {sqlType}");
                    columnList.Add(new { Name = colName, Type = sqlType });
                }

                sbTables.AppendLine("  " + string.Join(",\n  ", colLines));
                sbTables.AppendLine(");");

                tablesList.Add(new { Name = tableName, Columns = columnList });

            }

            File.WriteAllText(Path.Combine(outputDirectory, "tables.sql"), sbTables.ToString());

            //Procedury
            var procTable = new DataTable();
            using (var cmd = new FbCommand(
                @"SELECT TRIM(RDB$PROCEDURE_NAME) AS PROCEDURE_NAME,
                    RDB$PROCEDURE_SOURCE
                  FROM RDB$PROCEDURES
                  WHERE RDB$SYSTEM_FLAG = 0", connection))
            using (var adapter = new FbDataAdapter(cmd))
            {
                adapter.Fill(procTable);
            }

            var sbProcs = new StringBuilder();
            var procsList = new List<object>();
            foreach (DataRow row in procTable.Rows)
            {
                string name = row["PROCEDURE_NAME"].ToString();
                string source = row["RDB$PROCEDURE_SOURCE"]?.ToString()?.Trim();

                if (string.IsNullOrWhiteSpace(source))
                    continue;

                sbProcs.AppendLine("SET TERM ^^ ;");
                sbProcs.AppendLine($"CREATE OR ALTER PROCEDURE \"{name}\"");
                sbProcs.AppendLine(source + " ^^");
                sbProcs.AppendLine("SET TERM ; ^^");
                procsList.Add(new { Name = name, Source = source });
            }

            File.WriteAllText(Path.Combine(outputDirectory, "procedures.sql"), sbProcs.ToString());

            //Metadane JSON
            var json = JsonSerializer.Serialize(new { Domains = domains, Tables = tablesList, Procedures = procsList },
                new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(Path.Combine(outputDirectory, "metadata.json"), json);
        }

        public static void UpdateDatabase(string connectionString, string scriptsDirectory)
        {
            using var connection = new FbConnection(connectionString);
            connection.Open();
            string[] executionOrder = { "domains.sql", "tables.sql", "procedures.sql" };

            foreach (var file in executionOrder)
            {
                string filePath = Path.Combine(scriptsDirectory, file);
                if (!File.Exists(filePath)) continue;

                string sql = File.ReadAllText(filePath);
                var commands = sql.Split(';', StringSplitOptions.RemoveEmptyEntries);
                using var transaction = connection.BeginTransaction();
                try
                {
                    foreach (var cmd in commands)
                    {
                        string trimmed = cmd.Trim();
                        if (string.IsNullOrWhiteSpace(trimmed)) continue;

                        try
                        {
                            using var command = new FbCommand(trimmed, connection, transaction);
                            command.ExecuteNonQuery();
                        }
                        catch (FbException fbEx)
                        {
                            Console.WriteLine($"Błąd SQL w pliku {Path.GetFileName(file)}:");
                            Console.WriteLine($"Kod błędu: {fbEx.ErrorCode}");
                            Console.WriteLine($"Wiadomość: {fbEx.Message}");
                            Console.WriteLine($"Polecenie: {trimmed}");
                            Console.WriteLine($"Kontynuuję");
                        }
                        catch (InvalidOperationException invOpEx)
                        {
                            Console.WriteLine($"Nieprawidłowa operacja w pliku {Path.GetFileName(file)}:");
                            Console.WriteLine(invOpEx.Message);
                            throw;
                        }
                        catch (IOException ioEx)
                        {
                            Console.WriteLine($"Błąd odczytu pliku {Path.GetFileName(file)}:");
                            Console.WriteLine(ioEx.Message);
                            throw;
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"Nieoczekiwany błąd w pliku {Path.GetFileName(file)}:");
                            Console.WriteLine(ex.Message);
                            throw;
                        }
                    }

                    transaction.Commit();
                    Console.WriteLine($"Plik {Path.GetFileName(file)} został wykonany poprawnie.");
                }
                catch
                {
                    transaction.Rollback();
                    Console.WriteLine($"Wycofano zmiany dla pliku {Path.GetFileName(file)}.");
                    throw;
                }
            }
        }
        private static string MapFieldType(int fieldType, int length, int charLength)
        {
            return fieldType switch
            {
                7 => "SMALLINT",
                8 => "INTEGER",
                10 => "FLOAT",
                12 => "DATE",
                13 => "TIME",
                14 => $"CHAR({(charLength > 0 ? charLength : length)})",
                16 => "BIGINT",
                27 => "DOUBLE PRECISION",
                35 => "TIMESTAMP",
                37 => $"VARCHAR({(charLength > 0 ? charLength : length)})",
                40 => "CSTRING",
                _ => "UNKNOWN"
            };
        }
    }
}