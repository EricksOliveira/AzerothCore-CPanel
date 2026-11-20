using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text;
using AzerothCorePanel.Localization;
using AzerothCorePanel.Services.MySql;

namespace AzerothCorePanel.Services;

/// <summary>One finished backup: a zip with one .sql file per database.</summary>
public sealed record BackupResult(string ZipPath, long Bytes, TimeSpan Duration, IReadOnlyList<string> Databases);

/// <summary>A backup zip found in the backup folder.</summary>
public sealed record BackupFile(string Path, DateTime Created, long Bytes)
{
    public string Name => System.IO.Path.GetFileName(Path);

    public string CreatedText => Created.ToString("G", L.Culture);

    public string SizeText => Bytes >= 1024 * 1024 ? $"{Bytes / 1048576.0:0.0} MB" : $"{Bytes / 1024.0:0} KB";
}

/// <summary>
/// Backs up AzerothCore databases with mysqldump into "backup_yyyy-MM-dd_HH-mm-ss.zip" files and keeps only
/// the newest ones.
/// </summary>
public static class DatabaseBackup
{
    public const string FilePrefix = "backup_";
    private const string DumpExe = "mysqldump.exe";

    /// <summary>
    /// Finds mysqldump: the configured path, next to the controlled mysqld, in MySQL/MariaDB install folders,
    /// then on PATH.
    /// </summary>
    public static string? FindMysqldump(IMySqlController? mySql, string? configuredPath)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath) && File.Exists(configuredPath))
        {
            return configuredPath;
        }

        string? mysqld = mySql switch
        {
            PortableMySqlController portable => portable.MysqldPath,
            ServiceMySqlController service => service.ExecutablePath,
            _ => null,
        };

        var candidates = new List<string>();
        if (mysqld is not null)
        {
            candidates.Add(Path.Combine(Path.GetDirectoryName(mysqld)!, DumpExe));
        }

        foreach (string root in new[]
                 {
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                 })
        {
            foreach (string vendor in new[] { "MySQL", "MariaDB" })
            {
                string dir = Path.Combine(root, vendor);
                if (Directory.Exists(dir))
                {
                    candidates.AddRange(Directory.GetDirectories(dir)
                        .OrderByDescending(d => d, StringComparer.OrdinalIgnoreCase)
                        .Select(d => Path.Combine(d, "bin", DumpExe)));
                }
            }
        }

        foreach (string dir in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(';'))
        {
            if (dir.Length > 0)
            {
                candidates.Add(Path.Combine(dir.Trim(), DumpExe));
            }
        }

        return candidates.FirstOrDefault(File.Exists);
    }

    public static IReadOnlyList<BackupFile> List(string folder)
    {
        if (!Directory.Exists(folder))
        {
            return Array.Empty<BackupFile>();
        }

        return Directory.GetFiles(folder, FilePrefix + "*.zip")
            .Select(path => new FileInfo(path))
            .OrderByDescending(file => file.Name, StringComparer.Ordinal)
            .Select(file => new BackupFile(file.FullName, file.LastWriteTime, file.Length))
            .ToList();
    }

    public static async Task<BackupResult> RunAsync(string mysqldump, IReadOnlyList<DatabaseInfo> databases,
        string folder, int keep, CancellationToken token)
    {
        if (databases.Count == 0)
        {
            throw new InvalidOperationException(L.T("Backup.NoDatabase"));
        }

        var stopwatch = Stopwatch.StartNew();
        Directory.CreateDirectory(folder);

        string stamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
        string zipPath = Path.Combine(folder, $"{FilePrefix}{stamp}.zip");
        string workDir = Path.Combine(folder, $".{FilePrefix}{stamp}.tmp");
        Directory.CreateDirectory(workDir);

        try
        {
            foreach (DatabaseInfo database in databases)
            {
                await DumpAsync(mysqldump, database, Path.Combine(workDir, database.Database + ".sql"), token);
            }

            ZipFile.CreateFromDirectory(workDir, zipPath, CompressionLevel.Optimal, includeBaseDirectory: false);
        }
        catch
        {
            TryDelete(zipPath);
            throw;
        }
        finally
        {
            try
            {
                Directory.Delete(workDir, recursive: true);
            }
            catch (IOException)
            {
            }
        }

        DeleteOld(folder, keep);

        return new BackupResult(zipPath, new FileInfo(zipPath).Length, stopwatch.Elapsed,
            databases.Select(d => d.Database).ToList());
    }

    private static async Task DumpAsync(string mysqldump, DatabaseInfo database, string outputFile,
        CancellationToken token)
    {
        // Credentials go in a temporary option file instead of the command line, where other programs could read
        // them; %TEMP% is private to the current user.
        string optionFile = Path.Combine(Path.GetTempPath(), $"acpanel_{Guid.NewGuid():N}.cnf");
        var options = new StringBuilder("[client]\n");
        options.Append("user=").Append(Quote(database.User)).Append('\n');
        options.Append("password=").Append(Quote(database.Password)).Append('\n');
        if (database.IsNamedPipe)
        {
            options.Append("protocol=PIPE\nsocket=").Append(Quote(database.Port)).Append('\n');
        }
        else
        {
            options.Append("host=").Append(Quote(database.Host)).Append('\n');
            options.Append("port=").Append(database.Port).Append('\n');
        }

        await File.WriteAllTextAsync(optionFile, options.ToString(), new UTF8Encoding(false), token);

        try
        {
            var startInfo = new ProcessStartInfo(mysqldump)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
            };

            // --defaults-extra-file must be the first option.
            startInfo.ArgumentList.Add($"--defaults-extra-file={optionFile}");
            startInfo.ArgumentList.Add("--single-transaction"); // consistent InnoDB snapshot while the server runs
            startInfo.ArgumentList.Add("--routines");
            startInfo.ArgumentList.Add("--triggers");
            startInfo.ArgumentList.Add("--hex-blob");
            startInfo.ArgumentList.Add("--no-tablespaces"); // otherwise MySQL 8 needs the PROCESS privilege
            startInfo.ArgumentList.Add("--default-character-set=utf8mb4");
            startInfo.ArgumentList.Add($"--result-file={outputFile}");
            startInfo.ArgumentList.Add(database.Database);

            using Process process = Process.Start(startInfo)
                ?? throw new InvalidOperationException(L.T("Backup.DumpNotStarted"));

            Task<string> errors = process.StandardError.ReadToEndAsync(token);
            try
            {
                await process.WaitForExitAsync(token);
            }
            catch (OperationCanceledException)
            {
                process.Kill();
                throw;
            }

            if (process.ExitCode != 0)
            {
                string message = (await errors).Trim();
                throw new InvalidOperationException(
                    L.F("Backup.DumpFailed", database.Database, process.ExitCode, message));
            }
        }
        finally
        {
            TryDelete(optionFile);
        }
    }

    /// <summary>Keeps the newest <paramref name="keep"/> backup zips and deletes the rest (only backup_*.zip files).</summary>
    private static void DeleteOld(string folder, int keep)
    {
        if (keep <= 0)
        {
            return;
        }

        foreach (BackupFile old in List(folder).Skip(keep))
        {
            TryDelete(old.Path);
        }
    }

    /// <summary>Option file value in double quotes, escaping backslashes and quotes ("#" would start a comment).</summary>
    private static string Quote(string value) =>
        "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
