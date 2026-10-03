using System.CommandLine;
using System.CommandLine.Invocation;
using System.CommandLine.NamingConventionBinder;

namespace ClassLibrary
{
    class Program
    {
        static async Task Main(string[] args)
        {
            // Simple manual argument parsing to avoid dependency on specific System.CommandLine API versions
            string GetArg(string[] a, string name)
            {
                for (int i = 0; i < a.Length; i++)
                {
                    if (a[i] == name && i + 1 < a.Length)
                        return a[i + 1];
                }
                return null;
            }

            string direction = GetArg(args, "--direction") ?? "sql-to-sqlite";
            string connType = GetArg(args, "--conn-type");
            string connServer = GetArg(args, "--conn-server");
            string connDb = GetArg(args, "--conn-db");
            string connUser = GetArg(args, "--conn-user") ?? "sa";
            string connPass = GetArg(args, "--conn-pass");
            string sqlitePath = GetArg(args, "--sqlite-path");

            if (string.IsNullOrEmpty(connType) || string.IsNullOrEmpty(connServer) || string.IsNullOrEmpty(connDb) || string.IsNullOrEmpty(sqlitePath))
            {
                Console.WriteLine("Missing required arguments.");
                Console.WriteLine("  SQL Server -> SQLite:  --direction sql-to-sqlite  --conn-type <win|sql> --conn-server <server> --conn-db <db> --sqlite-path <path> [--conn-user <user>] [--conn-pass <pass>]");
                Console.WriteLine("  SQLite -> SQL Server:  --direction sqlite-to-sql  --conn-type <win|sql> --conn-server <server> --conn-db <db> --sqlite-path <path> [--conn-user <user>] [--conn-pass <pass>]");
                Console.WriteLine("  A sidecar file '<sqlite-path>.typemap.json' ({\"Table.Column\": \"bigint identity\", ...})");
                Console.WriteLine("  is used automatically when present to pin exact SQL Server types.");
                return;
            }

            string sqlConnString;
            string dirPath = Path.GetDirectoryName(sqlitePath);
            if (!string.IsNullOrEmpty(dirPath))
            {
                DirectoryInfo di = System.IO.Directory.CreateDirectory(dirPath);
                if (di == null)
                {
                    Console.WriteLine("Sorry but can't create your file in that dir");
                    return;
                }
            }

            if (connType == "win")
            {
                sqlConnString = GetSqlServerConnectionString(connServer, connDb);
            }
            else
            {
                if (connUser == null || connPass == null)
                {
                    Console.WriteLine("You must provide user and pass if you are not going to use windows auth");
                    return;
                }
                sqlConnString = GetSqlServerConnectionString(connServer, connDb, connUser, connPass);
            }

            if (direction == "sqlite-to-sql")
            {
                Console.WriteLine("Converting SQLite -> SQL Server...");
                SQLiteToSqlServer.Convert(sqlitePath, sqlConnString, (percent, message) =>
                    Console.WriteLine($"[{percent,3}%] {message}"));
                Console.WriteLine("Conversion finished.");
                return;
            }

            // change true to argument for Triggers
            // change false to argument for Views
            // change true to argument for GuiDAs String
            SqlServerToSQLite.ConvertSqlServerToSQLiteDatabase(sqlConnString, sqlitePath, null,
                (done, success, percent, message) => Console.WriteLine($"[{percent,3}%] {message}"),
                null, null, true, false, true, false);
        }

        static string GetSqlServerConnectionString(string address, string db)
        {
            string res = @"Data Source=" + address.Trim() +
                    ";Initial Catalog=" + db.Trim() + ";Integrated Security=SSPI;";
            return res;
        }
        static string GetSqlServerConnectionString(string address, string db, string user, string pass)
        {
            string res = @"Data Source=" + address.Trim() +
                ";Initial Catalog=" + db.Trim() + ";User ID=" + user.Trim() + ";Password=" + pass.Trim();
            return res;
        }
    }
}
