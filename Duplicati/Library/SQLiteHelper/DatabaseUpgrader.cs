// Copyright (C) 2025, The Duplicati Team
// https://duplicati.com, hello@duplicati.com
//
// Permission is hereby granted, free of charge, to any person obtaining a
// copy of this software and associated documentation files (the "Software"),
// to deal in the Software without restriction, including without limitation
// the rights to use, copy, modify, merge, publish, distribute, sublicense,
// and/or sell copies of the Software, and to permit persons to whom the
// Software is furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in
// all copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS
// OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING
// FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER
// DEALINGS IN THE SOFTWARE.

using System;
using System.Collections.Generic;
using System.Text;
using System.Data;
using System.Text.RegularExpressions;
using System.Linq;
using Duplicati.Library.SQLiteHelper.DBUpdates;
using Duplicati.Library.SQLiteHelper.DBSchemaUpgrades;
using System.Globalization;

namespace Duplicati.Library.SQLiteHelper
{
    /// <summary>
    /// This class will read embedded files from the given folder.
    /// Updates should have the form &quot;1.Sample upgrade.sql&quot;.
    /// When the database schema changes, simply put a new file into the folder
    /// and set it to be embedded in the binary.
    ///
    /// Additionally, it's possible to execute custom code before and after
    /// the SQL is executed. To set up a custom upgrade stage, add your
    /// code to DbUpgradesRegistry along with the DB version to apply it with.
    ///
    /// Even if all the DB upgrade code is handled in C#, you still have to add
    /// a dummy SQL file to indicate the version ID is already taken.
    ///
    /// Each upgrade file should ONLY upgrade from the previous version.
    /// If done correctly, a user may be upgrade from the very first version
    /// to the very latest.
    ///
    /// The Schema.sql file should ALWAYS have the latest schema, as that will
    /// ensure that new installs do not run upgrades after installation.
    /// Also remember to update the last line in Schema.sql to insert the
    /// current version number in the version table.
    ///
    /// Currently no upgrades may contain semicolons, except as the SQL statement
    /// delimiter.
    /// </summary>
    public static class DatabaseUpgrader
    {
        //This is the "folder" where the embedded resources can be found
        private const string FOLDER_NAME = "Database_schema";

        //This is the name of the schema sql
        private const string SCHEMA_NAME = "Schema.sql";

        /// <summary> Helper func to evaluate a condition like "sqlitever > 3.8.2" </summary>
        private static bool evalCondition(string cond, IDictionary<string, IComparable> vars)
        {
            var ops = new Dictionary<string, Func<IComparable, IComparable, bool>>()
             {
                {"<=", (x,y) => x.CompareTo(y) <= 0},
                {">=", (x,y) => x.CompareTo(y) >= 0},
                {"!=", (x,y) => x.CompareTo(y) != 0},
                {"==", (x,y) => x.CompareTo(y) == 0},
                {"<",  (x,y) => x.CompareTo(y) <  0},
                {">",  (x,y) => x.CompareTo(y) >  0},
                {"=",  (x,y) => x.CompareTo(y) == 0},
            };

            // build RegEx list with operators
            var opsList = "(" + string.Join("|", ops.Keys.Select(sop => Regex.Escape(sop) + (sop.Length == 1 ? @"(?!\=)" : ""))) + ")";
            var condPattern = string.Format(@"^\s*(?<VARIABLE>[a-zA-Z_][a-zA-Z0-9_]*)\s*(?<OPERATOR>{0})(?<LITERAL>.*)$"
                , opsList);

            // match condition to retrieve parts
            var m = Regex.Match(cond, condPattern, RegexOptions.ExplicitCapture | RegexOptions.IgnoreCase | RegexOptions.Singleline);
            if (!m.Success) throw new ArgumentException(string.Format("Malformed condition '{0}'.", cond));
            var variable = m.Groups["VARIABLE"].Value;
            var op = m.Groups["OPERATOR"].Value;
            var literal = m.Groups["LITERAL"].Value.Trim();

            // find variable and convert literal to correct type
            IComparable varVal;
            if (!vars.TryGetValue(variable, out varVal))
                throw new KeyNotFoundException(string.Format("Unknown variable '{0}' used in condition.", variable));

            IComparable litVal;
            try
            {
                if (varVal.GetType() == typeof(Version))
                    litVal = Version.Parse(literal);
                else // good for most other value types
                    litVal = (IComparable)System.Convert.ChangeType(literal, varVal.GetType(), System.Globalization.CultureInfo.InvariantCulture);
            }
            catch (Exception ex)
            { throw new FormatException(string.Format("Failed to convert literal '{0}' to desired type '{1}' for comparison", literal, varVal.GetType().Name), ex); }

            return ops[op](varVal, litVal);
        }

        /// <summary>
        /// Preparses an SQL in a very simple way to support conditional statements / clauses.
        /// Nesting is supported by using {#if_xx} {#else_xx} {#endif_xx} with xx being a number inside the blocks.
        /// </summary>
        public static string PreparseSQL(string sql, IDictionary<string, IComparable> vars)
        {
            var prepPattern = @"\{\#if(?<NEST>(_\d*)?)\s+(?<CONDITION>[^\}]*)}(?<THENPART>.*?)(?:\{\#else\k<NEST>\}(?<ELSEPART>.*?))?\{\#endif\k<NEST>\}";
            var parsePoints = Regex.Matches(sql, prepPattern, RegexOptions.ExplicitCapture | RegexOptions.IgnoreCase | RegexOptions.Singleline);

            StringBuilder retSql = new StringBuilder();
            int curPos = 0;
            foreach (Match pp in parsePoints)
            {
                var cond = pp.Groups["CONDITION"].Value;
                var thenpart = pp.Groups["THENPART"].Value;
                var elsepart = pp.Groups["ELSEPART"].Success ? pp.Groups["ELSEPART"].Value : null;

                retSql.Append(sql.Substring(curPos, pp.Index - curPos));
                if (evalCondition(cond, vars))
                    retSql.Append(PreparseSQL(thenpart, vars));
                else if (elsepart != null)
                    retSql.Append(PreparseSQL(elsepart, vars));

                curPos = pp.Index + pp.Length;
            }
            retSql.Append(sql.Substring(curPos, sql.Length - curPos));
            return retSql.ToString();
        }

        public static void UpgradeDatabase(IDbConnection connection, string sourcefile, Type eltype)
        {
            var asm = eltype.Assembly;

            string schema;
            using (var rd = new System.IO.StreamReader(asm.GetManifestResourceStream(eltype, $"{FOLDER_NAME}.{SCHEMA_NAME}")))
                schema = rd.ReadToEnd();

            //Get updates, and sort them according to version
            //This enables upgrading through several versions
            //ea, from 1 to 8, by stepping 2->3->4->5->6->7->8
            SortedDictionary<int, string> upgrades = new SortedDictionary<int, string>();
            string prefix = FOLDER_NAME + ".";
            foreach (string s in asm.GetManifestResourceNames())
            {
                //The resource name will be "Duplicati.Library.Main.Database.Database_schema.1.Sample upgrade.sql"
                //The number indicates the version that will be upgraded to
                //Could be ""Duplicati.Server.Database.Database_schema.1. Add Notifications.sql""
                if ((s.IndexOf(prefix, 0, StringComparison.Ordinal) >= 0) && !s.EndsWith(prefix + SCHEMA_NAME))
                {
                    try
                    {
                        string version = s.Substring(s.IndexOf(prefix) + prefix.Length, s.IndexOf(".", s.IndexOf(prefix) + prefix.Length + 1, StringComparison.Ordinal) - s.IndexOf(prefix) - prefix.Length);
                        int fileversion = int.Parse(version);

                        string prev;

                        if (!upgrades.TryGetValue(fileversion, out prev))

                            prev = "";

                        upgrades[fileversion] = prev + new System.IO.StreamReader(asm.GetManifestResourceStream(s)).ReadToEnd();
                    }
                    catch
                    {
                    }
                }
            }

            UpgradeDatabase(connection, sourcefile, schema, new List<string>(upgrades.Values));
        }


        /// <summary>
        /// Ensures that the database is up to date
        /// </summary>
        /// <param name="connection">The database connection to use</param>
        /// <param name="sourcefile">The file the database is placed in</param>
        private static void UpgradeDatabase(IDbConnection connection, string sourcefile, string schema, IList<string> versions)
        {
            if (connection.State != ConnectionState.Open)
            {
                if (string.IsNullOrEmpty(connection.ConnectionString))
                    connection.ConnectionString = "Data Source=" + sourcefile;

                connection.Open();
            }


            int dbversion = 0;
            using (var cmd = connection.CreateCommand())
            {
                try
                {
                    //See if the version table is present,
                    cmd.CommandText = @"
                        SELECT COUNT(*)
                        FROM SQLITE_MASTER
                        WHERE Name LIKE 'Version'
                    ";
                    int count = Convert.ToInt32(cmd.ExecuteScalar());

                    if (count == 0)
                        dbversion = -1; //Empty
                    else if (count == 1)
                    {
                        cmd.CommandText = @"
                            SELECT max(Version)
                            FROM Version
                        ";
                        dbversion = Convert.ToInt32(cmd.ExecuteScalar());
                    }
                    else
                        throw new Exception(Strings.DatabaseUpgrader.TableLayoutError);

                }
                catch (Exception ex)
                {
                    //Hopefully a more explanatory error message
                    throw new Duplicati.Library.Interface.UserInformationException(Strings.DatabaseUpgrader.DatabaseFormatError(ex.Message), "DatabaseFormatError", ex);
                }

                Dictionary<string, IComparable> preparserVars = null;

                if (dbversion > versions.Count)
                    throw new Duplicati.Library.Interface.UserInformationException(Strings.DatabaseUpgrader.InvalidVersionError(dbversion, versions.Count, System.IO.Path.GetDirectoryName(sourcefile)), "DatabaseVersionNotSupportedError");
                else if (dbversion < versions.Count) // will need action, collect vars for preparser
                {
                    preparserVars = new Dictionary<string, IComparable>(StringComparer.OrdinalIgnoreCase);
                    cmd.CommandText = "SELECT sqlite_version()";
                    System.Version sqliteversion;
                    if (Version.TryParse(cmd.ExecuteScalar().ToString(), out sqliteversion))
                        preparserVars["sqlite_version"] = sqliteversion;

                    preparserVars["db_version"] = dbversion;
                }

                //On a new database, we just load the most current schema, and upgrade from there
                //This avoids potentially lengthy upgrades
                if (dbversion == -1)
                {
                    cmd.CommandText = PreparseSQL(schema, preparserVars);
                    cmd.ExecuteNonQuery();
                    UpgradeDatabase(connection, sourcefile, schema, versions);
                    return;
                }
                else if (versions.Count > dbversion)
                {
                    // In some cases (mostly test setups)
                    // the database upgrades can happen within a second of each other
                    // causing the upgrades to fail. This scheme adds up to 15 seconds
                    // delay, making room for multiple rapid upgrade calls
                    var backupfile = string.Empty;
                    for (var i = 0; i < 10; i++)
                    {
                        backupfile = System.IO.Path.Combine(
                            System.IO.Path.GetDirectoryName(sourcefile),
                            Strings.DatabaseUpgrader.BackupFilenamePrefix + " " + System.IO.Path.GetFileNameWithoutExtension(sourcefile) + " " + (DateTime.Now + TimeSpan.FromSeconds(i * 1.5)).ToString("yyyyMMddhhmmss", System.Globalization.CultureInfo.InvariantCulture) + ".sqlite");

                        if (!System.IO.File.Exists(backupfile))
                            break;
                    }

                    try
                    {
                        if (!System.IO.Directory.Exists(System.IO.Path.GetDirectoryName(backupfile)))
                            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(backupfile));

                        //Keep a backup
                        System.IO.File.Copy(sourcefile, backupfile, false);

                        for (int i = dbversion; i < versions.Count; i++)
                        {
                            IDbSchemaUpgrade dbCodeUpgrade;

                            // The versions in the registry are 1-based, the loop index is zero based.
                            bool hookFound = DbUpgradesRegistry.CodeChanges.TryGetValue(i + 1, out dbCodeUpgrade);

                            if (hookFound)
                            {
                                dbCodeUpgrade.BeforeSql(connection);
                            }

                            //TODO: Find a better way to split SQL statements, as there may be embedded semicolons
                            //in the SQL, like "UPDATE x WHERE y = ';';"

                            // Preparse before splitting to enable statement spanning conditional blocks
                            string versionscript = PreparseSQL(versions[i], preparserVars);

                            //We split them to get a better error message
                            foreach (string c in versionscript.Split(';'))
                                if (c.Trim().Length > 0)
                                {
                                    cmd.CommandText = c;
                                    cmd.ExecuteNonQuery();
                                }

                            if (hookFound)
                            {
                                dbCodeUpgrade.AfterSql(connection);
                            }

                            // after upgrade, db_version should have changed to i + 1. If logic changes, just requery.
                            preparserVars["db_version"] = i + 1;
                        }

                        //Update databaseversion, so we don't run the scripts again
                        cmd.CommandText = "Update version SET Version = " + versions.Count.ToString(CultureInfo.InvariantCulture);
                        cmd.ExecuteNonQuery();
                    }
                    catch (Exception ex)
                    {
                        connection.Close();
                        //Restore the database
                        System.IO.File.Copy(backupfile, sourcefile, true);
                        throw new Exception(Strings.DatabaseUpgrader.UpgradeFailure(cmd.CommandText, ex.Message), ex);
                    }
                }
            }

        }
    }
}
