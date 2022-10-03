﻿// <copyright file="ShareRepository.cs" company="slskd Team">
//     Copyright (c) slskd Team. All rights reserved.
//
//     This program is free software: you can redistribute it and/or modify
//     it under the terms of the GNU Affero General Public License as published
//     by the Free Software Foundation, either version 3 of the License, or
//     (at your option) any later version.
//
//     This program is distributed in the hope that it will be useful,
//     but WITHOUT ANY WARRANTY; without even the implied warranty of
//     MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
//     GNU Affero General Public License for more details.
//
//     You should have received a copy of the GNU Affero General Public License
//     along with this program.  If not, see https://www.gnu.org/licenses/.
// </copyright>

namespace slskd.Shares
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using Microsoft.Data.Sqlite;
    using Serilog;
    using slskd.Transfers.Uploads;
    using Soulseek;

    /// <summary>
    ///     Persistent storage of shared files and metadata.
    /// </summary>
    public interface IShareRepository
    {
        /// <summary>
        ///     Backs the current database up to the database at the specified <paramref name="connectionString"/>.
        /// </summary>
        /// <param name="connectionString">The connection string of the destination database.</param>
        void BackupTo(string connectionString);

        /// <summary>
        ///     Counts the number of directories in the database.
        /// </summary>
        /// <param name="parentDirectory">The optional directory prefix used for counting subdirectories.</param>
        /// <returns>The number of directories.</returns>
        int CountDirectories(string parentDirectory = null);

        /// <summary>
        ///     Counts the number of files in the database.
        /// </summary>
        /// <param name="parentDirectory">The optional directory prefix used for counting files in a subdirectory.</param>
        /// <returns>The number of files.</returns>
        int CountFiles(string parentDirectory = null);

        /// <summary>
        ///     Creates a new database.
        /// </summary>
        /// <remarks>
        ///     Creates tables using 'IF NOT EXISTS', so this is idempotent unless 'discardExisting` is specified, in which case
        ///     tables are explicitly dropped prior to creation.
        /// </remarks>
        /// <param name="discardExisting">An optional value that determines whether the existing database should be discarded.</param>
        void Create(bool discardExisting = false);

        /// <summary>
        ///     Finds the filename of the file matching the specified <paramref name="maskedFilename"/>.
        /// </summary>
        /// <param name="maskedFilename">The fully qualified remote path of the file.</param>
        /// <returns>The filename, if found.</returns>
        string FindFilename(string maskedFilename);

        /// <summary>
        ///     Inserts a directory.
        /// </summary>
        /// <param name="name">The fully qualified local name of the directory.</param>
        /// <param name="timestamp">The timestamp to assign to the record.</param>
        void InsertDirectory(string name, long timestamp);

        /// <summary>
        ///     Inserts a file.
        /// </summary>
        /// <param name="maskedFilename">The fully qualified remote path of the file.</param>
        /// <param name="originalFilename">The fully qualified local path of the file.</param>
        /// <param name="touchedAt">The timestamp at which the file was last modified, according to the host OS.</param>
        /// <param name="file">The Soulseek.File instance representing the file.</param>
        /// <param name="timestamp">The timestamp to assign to the record.</param>
        void InsertFile(string maskedFilename, string originalFilename, DateTime touchedAt, Soulseek.File file, long timestamp);

        /// <summary>
        ///     Lists all directories.
        /// </summary>
        /// <param name="parentDirectory">The optional directory prefix used for listing subdirectories.</param>
        /// <returns>The list of directories.</returns>
        IEnumerable<string> ListDirectories(string parentDirectory = null);

        /// <summary>
        ///     Lists all files.
        /// </summary>
        /// <param name="parentDirectory">The optional parent directory.</param>
        /// <param name="includeFullPath">A value indicating whether the fully qualified path should be returned.</param>
        /// <returns>The list of files.</returns>
        IEnumerable<Soulseek.File> ListFiles(string parentDirectory = null, bool includeFullPath = false);

        /// <summary>
        ///     Deletes directory records with a timestamp prior to the specified <paramref name="olderThanTimestamp"/>.
        /// </summary>
        /// <param name="olderThanTimestamp">The timestamp before which to delete directories.</param>
        /// <returns>The number of records deleted.</returns>
        long PruneDirectories(long olderThanTimestamp);

        /// <summary>
        ///     Deletes file records with a timestamp prior to the specified <paramref name="olderThanTimestamp"/>.
        /// </summary>
        /// <param name="olderThanTimestamp">The timestamp before which to delete files.</param>
        /// <returns>The number of records deleted.</returns>
        long PruneFiles(long olderThanTimestamp);

        /// <summary>
        ///     Restores the current database from the database at the specified <paramref name="connectionString"/>.
        /// </summary>
        /// <param name="connectionString">The connection string of the source database.</param>
        void RestoreFrom(string connectionString);

        /// <summary>
        ///     Searches the database for files matching the specified <paramref name="query"/>.
        /// </summary>
        /// <param name="query">The search query.</param>
        /// <returns>The list of matching files.</returns>
        IEnumerable<Soulseek.File> Search(SearchQuery query);
    }

    /// <summary>
    ///     Persistent storage of shared files and metadata.
    /// </summary>
    public class ShareRepository : IShareRepository
    {
        /// <summary>
        ///     Initializes a new instance of the <see cref="ShareRepository"/> class.
        /// </summary>
        /// <param name="connectionString"></param>
        public ShareRepository(string connectionString)
        {
            ConnectionString = connectionString;
        }

        private string ConnectionString { get; }
        private ILogger Log { get; } = Serilog.Log.ForContext<ShareRepository>();

        /// <summary>
        ///     Attempts to validate the database at the specified <paramref name="connectionString"/>.
        /// </summary>
        /// <param name="connectionString">The connection string of the database to validate.</param>
        /// <returns>A value indicating whether the database is valid.</returns>
        /// <exception cref="ArgumentNullException">Thrown if no connection string is provided.</exception>
        public static bool TryValidateDatabase(string connectionString)
        {
            if (connectionString == null)
            {
                throw new ArgumentNullException(nameof(connectionString), "A connection string is required");
            }

            var log = Serilog.Log.ForContext<ShareRepository>();

            var schema = new Dictionary<string, string>()
            {
                { "directories", "CREATE TABLE directories (name TEXT PRIMARY KEY, timestamp INTEGER NOT NULL)" },
                { "filenames", "CREATE VIRTUAL TABLE filenames USING fts5(maskedFilename)" },
                { "files", "CREATE TABLE files (maskedFilename TEXT PRIMARY KEY, originalFilename TEXT NOT NULL, size BIGINT NOT NULL, touchedAt TEXT NOT NULL, code INTEGER DEFAULT 1 NOT NULL, extension TEXT, attributeJson TEXT NOT NULL, timestamp INTEGER NOT NULL)" },
            };

            try
            {
                log.Debug("Validating shares database with connection string {String}", connectionString);

                using var conn = new SqliteConnection(connectionString);
                conn.Open();

                using var cmd = new SqliteCommand("SELECT name, sql from sqlite_master WHERE type = 'table' AND name IN ('directories', 'filenames', 'files');", conn);

                var reader = cmd.ExecuteReader();
                var rows = 0;

                while (reader.Read())
                {
                    var table = reader.GetString(0);
                    var expectedSql = reader.GetString(1);

                    if (schema.TryGetValue(table, out var actualSql))
                    {
                        if (actualSql != expectedSql)
                        {
                            throw new MissingFieldException($"Expected {table} schema to be {expectedSql}, found {actualSql}");
                        }
                        else
                        {
                            log.Debug("Shares database table {Table} is valid: {Actual}", table, actualSql);
                        }
                    }

                    rows++;
                }

                if (rows != schema.Count)
                {
                    throw new MissingMemberException($"Expected {schema.Count} tables, found {rows}");
                }

                return true;
            }
            catch (Exception ex)
            {
                log.Debug(ex, $"Failed to validate shares database with connection string {connectionString}: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        ///     Backs the current database up to the database at the specified <paramref name="connectionString"/>.
        /// </summary>
        /// <param name="connectionString">The connection string of the destination database.</param>
        public void BackupTo(string connectionString)
        {
            using var sourceConn = GetConnection(ConnectionString);
            using var backupConn = GetConnection(connectionString);
            sourceConn.BackupDatabase(backupConn);
        }

        /// <summary>
        ///     Counts the number of directories in the database.
        /// </summary>
        /// <param name="parentDirectory">The optional directory prefix used for counting subdirectories.</param>
        /// <returns>The number of directories.</returns>
        public int CountDirectories(string parentDirectory = null)
        {
            using var conn = GetConnection();

            SqliteCommand cmd = default;

            try
            {
                if (string.IsNullOrEmpty(parentDirectory))
                {
                    cmd = new SqliteCommand("SELECT COUNT(*) FROM directories;", conn);
                }
                else
                {
                    cmd = new SqliteCommand("SELECT COUNT(*) FROM directories WHERE name LIKE @prefix || '%'", conn);
                    cmd.Parameters.AddWithValue("prefix", parentDirectory);
                }

                var reader = cmd.ExecuteReader();
                reader.Read();
                return reader.GetInt32(0);
            }
            finally
            {
                cmd.Dispose();
            }
        }

        /// <summary>
        ///     Counts the number of files in the database.
        /// </summary>
        /// <param name="parentDirectory">The optional directory prefix used for counting files in a subdirectory.</param>
        /// <returns>The number of files.</returns>
        public int CountFiles(string parentDirectory = null)
        {
            using var conn = GetConnection();

            SqliteCommand cmd = default;

            try
            {
                if (string.IsNullOrEmpty(parentDirectory))
                {
                    cmd = new SqliteCommand("SELECT COUNT(*) FROM files;", conn);
                }
                else
                {
                    cmd = new SqliteCommand("SELECT COUNT(*) FROM files WHERE maskedFilename LIKE @prefix || '%'", conn);
                    cmd.Parameters.AddWithValue("prefix", parentDirectory);
                }

                var reader = cmd.ExecuteReader();
                reader.Read();
                return reader.GetInt32(0);
            }
            finally
            {
                cmd.Dispose();
            }
        }

        /// <summary>
        ///     Creates a new database.
        /// </summary>
        /// <remarks>
        ///     Creates tables using 'IF NOT EXISTS', so this is idempotent unless 'discardExisting` is specified, in which case
        ///     tables are explicitly dropped prior to creation.
        /// </remarks>
        /// <param name="discardExisting">An optional value that determines whether the existing database should be discarded.</param>
        public void Create(bool discardExisting = false)
        {
            using var conn = GetConnection();

            conn.ExecuteNonQuery("PRAGMA journal_mode=WAL");

            if (discardExisting)
            {
                conn.ExecuteNonQuery("DROP TABLE IF EXISTS directories; DROP TABLE IF EXISTS filenames; DROP TABLE IF EXISTS files;");
            }

            conn.ExecuteNonQuery("CREATE TABLE IF NOT EXISTS directories (name TEXT PRIMARY KEY, timestamp INTEGER NOT NULL);");

            conn.ExecuteNonQuery("CREATE VIRTUAL TABLE IF NOT EXISTS filenames USING fts5(maskedFilename);");

            conn.ExecuteNonQuery("CREATE TABLE IF NOT EXISTS files " +
                "(maskedFilename TEXT PRIMARY KEY, originalFilename TEXT NOT NULL, size BIGINT NOT NULL, touchedAt TEXT NOT NULL, code INTEGER DEFAULT 1 NOT NULL, " +
                "extension TEXT, attributeJson TEXT NOT NULL, timestamp INTEGER NOT NULL);");
        }

        /// <summary>
        ///     Finds the filename of the file matching the specified <paramref name="maskedFilename"/>.
        /// </summary>
        /// <param name="maskedFilename">The fully qualified remote path of the file.</param>
        /// <returns>The filename, if found.</returns>
        public string FindFilename (string maskedFilename)
        {
            using var conn = GetConnection();
            using var cmd = new SqliteCommand("SELECT originalFilename FROM files WHERE maskedFilename = @maskedFilename;", conn);
            cmd.Parameters.AddWithValue("maskedFilename", maskedFilename);

            var reader = cmd.ExecuteReader();

            if (!reader.Read())
            {
                Log.Warning("Failed to resolve shared file {Filename}", maskedFilename);
                return null;
            }

            var resolved = reader.GetString(0);
            Log.Debug($"Resolved requested shared file {maskedFilename} to {resolved}");
            return resolved;
        }

        /// <summary>
        ///     Inserts a directory.
        /// </summary>
        /// <param name="name">The fully qualified local name of the directory.</param>
        /// <param name="timestamp">The timestamp to assign to the record.</param>
        public void InsertDirectory(string name, long timestamp)
        {
            using var conn = GetConnection();

            conn.ExecuteNonQuery("INSERT INTO directories VALUES(@name, @timestamp) ON CONFLICT DO UPDATE SET timestamp = excluded.timestamp;", cmd =>
            {
                cmd.Parameters.AddWithValue("name", name);
                cmd.Parameters.AddWithValue("timestamp", timestamp);
            });
        }

        /// <summary>
        ///     Inserts a file.
        /// </summary>
        /// <param name="maskedFilename">The fully qualified remote path of the file.</param>
        /// <param name="originalFilename">The fully qualified local path of the file.</param>
        /// <param name="touchedAt">The timestamp at which the file was last modified, according to the host OS.</param>
        /// <param name="file">The Soulseek.File instance representing the file.</param>
        /// <param name="timestamp">The timestamp to assign to the record.</param>
        public void InsertFile(string maskedFilename, string originalFilename, DateTime touchedAt, Soulseek.File file, long timestamp)
        {
            using var conn = GetConnection();

            conn.ExecuteNonQuery("INSERT INTO files (maskedFilename, originalFilename, size, touchedAt, code, extension, attributeJson, timestamp) " +
                "VALUES(@maskedFilename, @originalFilename, @size, @touchedAt, @code, @extension, @attributeJson, @timestamp) " +
                "ON CONFLICT DO UPDATE SET originalFilename = excluded.originalFilename, size = excluded.size, touchedAt = excluded.touchedAt, " +
                "code = excluded.code, extension = excluded.extension, attributeJson = excluded.attributeJson, timestamp = excluded.timestamp;", cmd =>
                {
                    cmd.Parameters.AddWithValue("maskedFilename", maskedFilename);
                    cmd.Parameters.AddWithValue("originalFilename", originalFilename);
                    cmd.Parameters.AddWithValue("size", file.Size);
                    cmd.Parameters.AddWithValue("touchedAt", touchedAt.ToLongDateString());
                    cmd.Parameters.AddWithValue("code", file.Code);
                    cmd.Parameters.AddWithValue("extension", file.Extension);
                    cmd.Parameters.AddWithValue("attributeJson", file.Attributes.ToJson());
                    cmd.Parameters.AddWithValue("timestamp", timestamp);
                });

            conn.ExecuteNonQuery("INSERT INTO filenames (maskedFilename) VALUES(@maskedFilename);", cmd =>
            {
                cmd.Parameters.AddWithValue("maskedFilename", maskedFilename);
            });
        }

        /// <summary>
        ///     Lists all directories.
        /// </summary>
        /// <param name="parentDirectory">The optional directory prefix used for listing subdirectories.</param>
        /// <returns>The list of directories.</returns>
        public IEnumerable<string> ListDirectories(string parentDirectory = null)
        {
            var results = new List<string>();

            using var conn = GetConnection();

            SqliteCommand cmd = default;

            try
            {
                if (string.IsNullOrEmpty(parentDirectory))
                {
                    cmd = new SqliteCommand("SELECT name FROM directories ORDER BY name ASC;", conn);
                }
                else
                {
                    cmd = new SqliteCommand("SELECT name from directories WHERE name LIKE @prefix || '%' ORDER BY name ASC;", conn);
                    cmd.Parameters.AddWithValue("prefix", parentDirectory);
                }

                var reader = cmd.ExecuteReader();

                while (reader.Read())
                {
                    results.Add(reader.GetString(0));
                }

                return results;
            }
            finally
            {
                cmd.Dispose();
            }
        }

        /// <summary>
        ///     Lists all files.
        /// </summary>
        /// <param name="parentDirectory">The optional parent directory.</param>
        /// <param name="includeFullPath">A value indicating whether the fully qualified path should be returned.</param>
        /// <returns>The list of files.</returns>
        public IEnumerable<Soulseek.File> ListFiles(string parentDirectory = null, bool includeFullPath = false)
        {
            var results = new List<Soulseek.File>();

            SqliteCommand cmd = default;
            using var conn = GetConnection();

            try
            {
                if (string.IsNullOrEmpty(parentDirectory))
                {
                    cmd = new SqliteCommand("SELECT maskedFilename, code, size, extension, attributeJson FROM files ORDER BY maskedFilename ASC;", conn);
                }
                else
                {
                    cmd = new SqliteCommand("SELECT maskedFilename, code, size, extension, attributeJson FROM files WHERE maskedFilename LIKE @match ORDER BY maskedFilename ASC;", conn);
                    cmd.Parameters.AddWithValue("match", parentDirectory + '%');
                }

                var reader = cmd.ExecuteReader();

                while (reader.Read())
                {
                    var filename = reader.GetString(0);
                    var code = reader.GetInt32(1);
                    var size = reader.GetInt64(2);
                    var extension = reader.GetString(3);
                    var attributeJson = reader.GetString(4);

                    var attributeList = attributeJson.FromJson<List<FileAttribute>>();

                    filename = includeFullPath ? filename : Path.GetFileName(filename);

                    var file = new Soulseek.File(code, filename, size, extension, attributeList);

                    results.Add(file);
                }

                return results;
            }
            finally
            {
                cmd?.Dispose();
            }
        }

        /// <summary>
        ///     Deletes directory records with a timestamp prior to the specified <paramref name="olderThanTimestamp"/>.
        /// </summary>
        /// <param name="olderThanTimestamp">The timestamp before which to delete directories.</param>
        /// <returns>The number of records deleted.</returns>
        public long PruneDirectories(long olderThanTimestamp)
        {
            using var conn = GetConnection();

            using var cmd = new SqliteCommand("DELETE FROM directories WHERE timestamp < @timestamp; SELECT changes()", conn);
            cmd.Parameters.AddWithValue("timestamp", olderThanTimestamp);

            var reader = cmd.ExecuteReader();
            reader.Read();

            return reader.GetInt64(0);
        }

        /// <summary>
        ///     Deletes file records with a timestamp prior to the specified <paramref name="olderThanTimestamp"/>.
        /// </summary>
        /// <param name="olderThanTimestamp">The timestamp before which to delete files.</param>
        /// <returns>The number of records deleted.</returns>
        public long PruneFiles(long olderThanTimestamp)
        {
            using var conn = GetConnection();

            using var cmd = new SqliteCommand("DELETE FROM files WHERE timestamp < @timestamp; SELECT changes()", conn);
            cmd.Parameters.AddWithValue("timestamp", olderThanTimestamp);

            var reader = cmd.ExecuteReader();
            reader.Read();

            return reader.GetInt64(0);
        }

        /// <summary>
        ///     Restores the current database from the database at the specified <paramref name="connectionString"/>.
        /// </summary>
        /// <param name="connectionString">The connection string of the source database.</param>
        public void RestoreFrom(string connectionString)
        {
            using var sourceConn = GetConnection(connectionString);
            using var restoreConn = GetConnection();
            sourceConn.BackupDatabase(restoreConn);
        }

        /// <summary>
        ///     Searches the database for files matching the specified <paramref name="query"/>.
        /// </summary>
        /// <param name="query">The search query.</param>
        /// <returns>The list of matching files.</returns>
        public IEnumerable<Soulseek.File> Search(SearchQuery query)
        {
            string Clean(string str) => str.Replace("/", " ")
                .Replace("\\", " ")
                .Replace(":", " ")
                .Replace("\"", " ")
                .Replace("'", "''");

            var match = string.Join(" AND ", query.Terms.Select(token => $"\"{Clean(token)}\""));
            var exclusions = string.Join(" OR ", query.Exclusions.Select(exclusion => $"\"{Clean(exclusion)}\""));

            var sql = $"SELECT files.maskedFilename, files.code, files.size, files.extension, files.attributeJson FROM filenames " +
                "INNER JOIN files ON filenames.maskedFilename = files.maskedFilename " +
                $"WHERE filenames MATCH '({match}) {(query.Exclusions.Any() ? $"NOT ({exclusions})" : string.Empty)}' " +
                "ORDER BY filenames.maskedFilename ASC;";

            var results = new List<Soulseek.File>();
            SqliteDataReader reader = default;

            try
            {
                using var conn = GetConnection();
                using var cmd = new SqliteCommand(sql, conn);
                reader = cmd.ExecuteReader();

                while (reader.Read())
                {
                    var filename = reader.GetString(0);
                    var code = reader.GetInt32(1);
                    var size = reader.GetInt64(2);
                    var extension = reader.GetString(3);
                    var attributeJson = reader.GetString(4);

                    var attributeList = attributeJson.FromJson<List<FileAttribute>>();

                    var file = new Soulseek.File(code, filename, size, extension, attributeList);
                    results.Add(file);
                }

                return results;
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to execute shared file query '{Query}': {Message}", query, ex.Message);
                return Enumerable.Empty<Soulseek.File>();
            }
        }

        private SqliteConnection GetConnection(string connectionString = null)
        {
            connectionString ??= ConnectionString;

            var conn = new SqliteConnection(connectionString);
            conn.Open();
            return conn;
        }
    }
}