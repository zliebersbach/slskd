﻿// <copyright file="SharedFileCache.cs" company="slskd Team">
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

namespace slskd
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.IO;
    using System.Linq;
    using System.Threading;
    using Microsoft.Data.Sqlite;
    using Soulseek;

    /// <summary>
    ///     Caches shared files.
    /// </summary>
    public class SharedFileCache : ISharedFileCache
    {
        /// <summary>
        ///     Initializes a new instance of the <see cref="SharedFileCache"/> class.
        /// </summary>
        /// <param name="shares"></param>
        /// <param name="ttl"></param>
        public SharedFileCache(IEnumerable<string> shares, long ttl)
        {
            Shares = shares;
            TTL = ttl;
        }

        public event EventHandler<(int Directories, int Files)> Refreshed;

        public IEnumerable<string> Shares { get; }
        public DateTime? LastFill { get; set; }
        public long TTL { get; }

        private Dictionary<string, Soulseek.File> Files { get; set; }
        private SqliteConnection SQLite { get; set; }
        private ReaderWriterLockSlim SyncRoot { get; } = new ReaderWriterLockSlim();

        private HashSet<string> Directories { get; set; }

        /// <summary>
        ///     Scans the configured <see cref="Shares"/> and fills the cache.
        /// </summary>
        public void Fill()
        {
            var sw = new Stopwatch();
            sw.Start();

            Console.WriteLine($"[SHARED FILE CACHE]: Refreshing...");

            SyncRoot.EnterWriteLock();

            try
            {
                CreateTable();
                var directories = new HashSet<string>();
                var files = new Dictionary<string, Soulseek.File>();

                foreach (var share in Shares)
                {
                    foreach (var directory in System.IO.Directory.GetDirectories(share, "*", SearchOption.AllDirectories))
                    {
                        directories.Add(directory);

                        // recursively find all files in the directory and stick a record in a dictionary, keyed on the sanitized
                        // filename and with a value of a Soulseek.File object
                        var newFiles = System.IO.Directory.GetFiles(directory, "*", SearchOption.TopDirectoryOnly)
                            .Select(f => new Soulseek.File(1, f.Replace("/", @"\"), new FileInfo(f).Length, Path.GetExtension(f)))
                            .ToDictionary(f => f.Filename, f => f);

                        // merge the new dictionary with the rest
                        // this will overwrite any duplicate keys, but keys are the fully qualified name
                        // the only time this *should* cause problems is if one of the shares is a subdirectory of another.
                        foreach (var file in newFiles)
                        {
                            if (files.ContainsKey(file.Key))
                            {
                                Console.WriteLine($"[WARNING] File {file.Key} shared in directory {directory} has already been cached.  This is probably a misconfiguration of the shared directories.");
                            }

                            files[file.Key] = file.Value;
                        }
                    }
                }

                // potentially optimize with multi-valued insert
                // https://stackoverflow.com/questions/16055566/insert-multiple-rows-in-sqlite
                foreach (var file in files)
                {
                    InsertFilename(file.Key);
                }

                Directories = directories;
                Files = files;
                Refreshed?.Invoke(this, (directories.Count, Files.Count));
            }
            finally
            {
                SyncRoot.ExitWriteLock();
            }

            sw.Stop();

            Console.WriteLine($"[SHARED FILE CACHE]: Refreshed in {sw.ElapsedMilliseconds}ms.  Found {Files.Count} files.");
            LastFill = DateTime.UtcNow;
        }

        /// <summary>
        ///     Searches the cache for files matching the specified <paramref name="query"/>.
        /// </summary>
        /// <param name="query"></param>
        /// <returns></returns>
        public IEnumerable<Soulseek.File> Search(SearchQuery query)
        {
            if (!LastFill.HasValue || LastFill.Value.AddMilliseconds(TTL) < DateTime.UtcNow)
            {
                Fill();
            }

            return QueryTable(query.Query);
        }

        public IEnumerable<Soulseek.Directory> Browse()
        {
            var directories = new ConcurrentDictionary<string, Soulseek.Directory>();

            // Soulseek requires that each directory in the tree have an entry in the list returned
            // in a browse response.  if missing, files that are nested within directories which contain
            // only directories (no files) are displayed as being in the root.  to get around this, prime
            // a dictionary with all known directories, and an empty Soulseek.Directory.  if there are any
            // files in the directory, this entry will be overwritten with a new Soulseek.Directory containing
            // the files. if not they'll be left as is.
            foreach (var directory in Directories)
            {
                directories.TryAdd(directory, new Soulseek.Directory(directory));
            }

            var groups = Files
                .GroupBy(f => Path.GetDirectoryName(f.Key))
                .Select(g => new Soulseek.Directory(g.Key, g.Select(g =>
                {
                    var f = g.Value;
                    return new Soulseek.File(
                        f.Code,
                        Path.GetFileName(f.Filename), // we can send the full path, or just the filename.  save bandwidth and omit the path.
                        f.Size,
                        f.Extension,
                        f.Attributes);
                })));

            // merge the dictionary containing all directories with the Soulseek.Directory instances
            // containing their files. entries with no files will remain untouched.
            foreach (var group in groups)
            {
                directories.AddOrUpdate(group.Name, group, (_, _) => group);
            }

            return directories.Values;
        }

        private void CreateTable()
        {
            SQLite = new SqliteConnection("Data Source=:memory:");
            SQLite.Open();

            using var cmd = new SqliteCommand("CREATE VIRTUAL TABLE cache USING fts5(filename)", SQLite);
            cmd.ExecuteNonQuery();
        }

        private void InsertFilename(string filename)
        {
            using var cmd = new SqliteCommand($"INSERT INTO cache(filename) VALUES('{filename.Replace("'", "''")}')", SQLite);
            cmd.ExecuteNonQuery();
        }

        private IEnumerable<Soulseek.File> QueryTable(string text)
        {
            // sanitize the query string. there's probably more to it than this.
            text = text
                .Replace("/", " ")
                .Replace("\\", " ")
                .Replace(":", " ")
                .Replace("\"", " ");

            var query = $"SELECT * FROM cache WHERE cache MATCH '\"{text.Replace("'", "''")}\"'";

            SyncRoot.EnterReadLock();

            try
            {
                using var cmd = new SqliteCommand(query, SQLite);
                var results = new List<string>();
                var reader = cmd.ExecuteReader();

                while (reader.Read())
                {
                    results.Add(reader.GetString(0));
                }

                return results.Select(r => Files[r.Replace("''", "'")]);
            }
            catch (Exception ex)
            {
                // temporary error trap to refine substitution rules
                Console.WriteLine($"[MALFORMED QUERY]: {query} ({ex.Message})");
                return Enumerable.Empty<Soulseek.File>();
            }
            finally
            {
                SyncRoot.ExitReadLock();
            }
        }
    }
}