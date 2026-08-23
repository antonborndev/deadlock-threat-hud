using System.Buffers.Binary;
using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

internal sealed record ThreatHudModStatus(
    string? DeadlockDirectory,
    bool IsInstalled,
    bool IsActive,
    bool HasVpkConflict,
    bool IsCurrentPayload,
    string? VpkError,
    string? ActivationError
)
{
    public bool IsDeadlockLocated =>
        !String.IsNullOrWhiteSpace(DeadlockDirectory);
}

internal sealed class ThreatHudModManagerService
{
    private const string DeadlockProcessName = "deadlock";
    private const string DeadlockAppId = "1422450";
    private const string InstalledVpkFileName = "pak57_dir.vpk";
    private const string InstalledHashFileName =
        "pak57_dir.vpk.threathud.sha256";
    private const string EmbeddedVpkResourceName =
        "ThreatHudBridge.Resources.pak57_dir.vpk";
    private const uint VpkSignature = 0x55AA1234;
    private const string ManagedSearchPathComment = "Threat HUD Bridge";
    private const string ManagedAddonSearchPathLine =
        "Game                citadel/addons // Threat HUD Bridge";
    private const string ManagedModSearchPathLine =
        "Mod                 citadel // Threat HUD Bridge";
    private const string ManagedWriteSearchPathLine =
        "Write               citadel // Threat HUD Bridge";

    private static readonly Regex LibraryPathPattern = new(
        @"""path""\s+""(?<value>(?:\\.|[^""])*)""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
    );

    private static readonly Regex InstallDirectoryPattern = new(
        @"""installdir""\s+""(?<value>(?:\\.|[^""])*)""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
    );

    private readonly Assembly _resourceAssembly =
        typeof(ThreatHudModManagerService).Assembly;
    private readonly object _embeddedHashSync = new();
    private readonly SemaphoreSlim _operationLock = new(1, 1);
    private string? _embeddedVpkHash;

    public Task<ThreatHudModStatus> GetStatusAsync(
        CancellationToken cancellationToken = default
    ) =>
        RunExclusiveAsync(
            () => Task.Run(
                () => GetStatusCore(cancellationToken),
                cancellationToken
            ),
            cancellationToken
        );

    public Task InstallAsync(
        CancellationToken cancellationToken = default
    ) =>
        RunExclusiveAsync(
            () => InstallCoreAsync(cancellationToken),
            cancellationToken
        );

    private async Task InstallCoreAsync(
        CancellationToken cancellationToken
    )
    {
        var paths = await Task.Run(
            () =>
            {
                EnsureDeadlockIsStopped();
                return RequireDeadlockPaths();
            },
            cancellationToken
        );

        Directory.CreateDirectory(paths.AddonsDirectory);

        var ownership = await Task.Run(
            () => InspectInstalledVpk(paths, cancellationToken),
            cancellationToken
        );

        if (ownership.Exists && !ownership.IsOwned)
        {
            throw new InvalidOperationException(
                $"Another file already uses {InstalledVpkFileName}. " +
                "Threat HUD Bridge will not overwrite it."
            );
        }

        if (ownership.Exists)
        {
            if (ownership.IsCurrentPayload)
            {
                EnsureDeadlockIsStopped();

                WriteTextAtomically(
                    paths.InstalledHashPath,
                    GetEmbeddedVpkHash() + Environment.NewLine
                );
                return;
            }

            throw new InvalidOperationException(
                "An older Threat HUD VPK is installed. " +
                "Uninstall it before installing this build."
            );
        }

        var embeddedHash = await Task.Run(
            GetEmbeddedVpkHash,
            cancellationToken
        );
        var temporaryPath = Path.Combine(
            paths.AddonsDirectory,
            $".{InstalledVpkFileName}.{Guid.NewGuid():N}.tmp"
        );

        try
        {
            await using (var source = OpenEmbeddedVpk())
            await using (var destination = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan
            ))
            {
                await source.CopyToAsync(
                    destination,
                    128 * 1024,
                    cancellationToken
                );
                await destination.FlushAsync(cancellationToken);
                destination.Flush(flushToDisk: true);
            }

            cancellationToken.ThrowIfCancellationRequested();
            EnsureDeadlockIsStopped();

            var copiedHash = await Task.Run(
                () => CalculateFileHash(temporaryPath, cancellationToken),
                cancellationToken
            );

            if (!String.Equals(
                copiedHash,
                embeddedHash,
                StringComparison.OrdinalIgnoreCase
            ))
            {
                throw new IOException(
                    "The copied VPK failed SHA-256 verification."
                );
            }

            EnsureDeadlockIsStopped();

            File.Move(
                temporaryPath,
                paths.InstalledVpkPath,
                overwrite: false
            );
            WriteTextAtomically(
                paths.InstalledHashPath,
                embeddedHash + Environment.NewLine
            );
        }
        finally
        {
            TryDeleteFile(temporaryPath);
        }
    }

    public Task UninstallAsync(
        CancellationToken cancellationToken = default
    ) =>
        RunExclusiveAsync(
            () => Task.Run(
                () => UninstallCore(cancellationToken),
                cancellationToken
            ),
            cancellationToken
        );

    public Task SetActiveAsync(
        bool active,
        CancellationToken cancellationToken = default
    ) =>
        RunExclusiveAsync(
            () => Task.Run(
                () =>
                {
                    EnsureDeadlockIsStopped();
                    var paths = RequireDeadlockPaths();
                    UpdateGameInfo(
                        paths.GameInfoPath,
                        active,
                        cancellationToken
                    );
                },
                cancellationToken
            ),
            cancellationToken
        );

    private void UninstallCore(
        CancellationToken cancellationToken
    )
    {
        EnsureDeadlockIsStopped();
        var paths = RequireDeadlockPaths();
        var ownership = InspectInstalledVpk(paths, cancellationToken);

        if (!ownership.Exists)
        {
            TryDeleteFile(paths.InstalledHashPath);
            return;
        }

        if (!ownership.IsOwned)
        {
            throw new InvalidOperationException(
                $"{InstalledVpkFileName} is not owned by " +
                "Threat HUD Bridge and will not be deleted."
            );
        }

        cancellationToken.ThrowIfCancellationRequested();
        EnsureDeadlockIsStopped();

        var quarantinePath = Path.Combine(
            paths.AddonsDirectory,
            $".{InstalledVpkFileName}.{Guid.NewGuid():N}.uninstalling"
        );

        File.Move(
            paths.InstalledVpkPath,
            quarantinePath,
            overwrite: false
        );

        try
        {
            var quarantinedHash = CalculateFileHash(
                quarantinePath,
                cancellationToken
            );
            var embeddedHash = GetEmbeddedVpkHash();
            var recordedHash = TryReadRecordedHash(
                paths.InstalledHashPath
            );
            var isOwned = String.Equals(
                    quarantinedHash,
                    embeddedHash,
                    StringComparison.OrdinalIgnoreCase
                ) ||
                String.Equals(
                    quarantinedHash,
                    recordedHash,
                    StringComparison.OrdinalIgnoreCase
                );

            if (!isOwned)
            {
                throw new InvalidOperationException(
                    $"{InstalledVpkFileName} is not owned by " +
                    "Threat HUD Bridge and will not be deleted."
                );
            }

            cancellationToken.ThrowIfCancellationRequested();
            EnsureDeadlockIsStopped();
            File.Delete(quarantinePath);
            TryDeleteFile(paths.InstalledHashPath);
        }
        catch (Exception operationError)
        {
            if (File.Exists(quarantinePath))
            {
                try
                {
                    if (File.Exists(paths.InstalledVpkPath))
                    {
                        throw new IOException(
                            "The original VPK path is occupied."
                        );
                    }

                    File.Move(
                        quarantinePath,
                        paths.InstalledVpkPath,
                        overwrite: false
                    );
                }
                catch (Exception restoreError) when (
                    restoreError is IOException or
                    UnauthorizedAccessException
                )
                {
                    throw new IOException(
                        "The uninstall was aborted, but the VPK could not " +
                        "be restored automatically. The preserved file is: " +
                        quarantinePath,
                        new AggregateException(
                            operationError,
                            restoreError
                        )
                    );
                }
            }

            throw;
        }
    }

    internal static bool IsAddonLoadingActive(string gameInfo)
    {
        var paths = FindSearchPathLines(
            gameInfo,
            FindSearchPathsBlock(gameInfo)
        );
        var vanillaPath = RequireVanillaGamePath(paths);

        return HasAddonPathBefore(paths, vanillaPath) &&
            paths.Any(
                path => IsSearchPath(path, "Mod", "citadel")
            ) &&
            paths.Any(
                path => IsSearchPath(path, "Write", "citadel")
            );
    }

    private static SearchPathLine RequireVanillaGamePath(
        IReadOnlyList<SearchPathLine> paths
    ) =>
        paths.FirstOrDefault(
            path => IsSearchPath(path, "Game", "citadel")
        ) ?? throw new InvalidDataException(
            "SearchPaths does not contain Game citadel."
        );

    private static bool HasAddonPathBefore(
        IReadOnlyList<SearchPathLine> paths,
        SearchPathLine vanillaPath
    ) =>
        paths.Any(
            path =>
                path.Start < vanillaPath.Start &&
                IsSearchPath(path, "Game", "citadel/addons")
        );

    private static bool IsSearchPath(
        SearchPathLine path,
        string key,
        string value
    ) =>
        String.Equals(
            path.Key,
            key,
            StringComparison.OrdinalIgnoreCase
        ) &&
        String.Equals(
            path.Value,
            value,
            StringComparison.OrdinalIgnoreCase
        );

    private static bool IsManagedActivationLine(SearchPathLine path) =>
        path.IsManaged &&
        (
            IsSearchPath(path, "Game", "citadel/addons") ||
            IsSearchPath(path, "Mod", "citadel") ||
            IsSearchPath(path, "Write", "citadel")
        );

    internal static string SetAddonLoadingActive(
        string gameInfo,
        bool active
    )
    {
        var block = FindSearchPathsBlock(gameInfo);
        var paths = FindSearchPathLines(gameInfo, block);
        var vanillaPath = RequireVanillaGamePath(paths);

        if (!active)
        {
            var hasForeignActiveAddonPath = paths.Any(
                path =>
                    !path.IsManaged &&
                    path.Start < vanillaPath.Start &&
                    IsSearchPath(path, "Game", "citadel/addons")
            );

            if (hasForeignActiveAddonPath)
            {
                throw new InvalidOperationException(
                    "Addon loading is managed by another tool. " +
                    "Threat HUD Bridge will not remove or modify its " +
                    "Game citadel/addons entry. Deactivate addon " +
                    "loading in that tool first."
                );
            }

            return RemoveLines(
                gameInfo,
                paths.Where(IsManagedActivationLine)
            );
        }

        var normalized = RemoveLines(
            gameInfo,
            paths.Where(IsManagedActivationLine)
        );
        block = FindSearchPathsBlock(normalized);
        paths = FindSearchPathLines(normalized, block);
        vanillaPath = RequireVanillaGamePath(paths);

        var addonPath = paths.FirstOrDefault(
            path =>
                path.Start < vanillaPath.Start &&
                IsSearchPath(path, "Game", "citadel/addons")
        );
        var managedLines = new List<string>();

        if (!paths.Any(path => IsSearchPath(path, "Mod", "citadel")))
        {
            managedLines.Add(ManagedModSearchPathLine);
        }

        if (!paths.Any(path => IsSearchPath(path, "Write", "citadel")))
        {
            managedLines.Add(ManagedWriteSearchPathLine);
        }

        if (addonPath is null)
        {
            managedLines.Add(ManagedAddonSearchPathLine);
        }

        if (managedLines.Count == 0)
        {
            return normalized;
        }

        var newline = DetectNewline(normalized);
        var insertionPath = addonPath ?? vanillaPath;
        var needsLeadingNewline =
            insertionPath.Start > 0 &&
            normalized[insertionPath.Start - 1] is not '\r' and not '\n';
        var managedBlock =
            (needsLeadingNewline ? newline : String.Empty) +
            String.Join(
                newline,
                managedLines.Select(
                    line => vanillaPath.Indent + line
                )
            ) +
            newline;

        return normalized.Insert(insertionPath.Start, managedBlock);
    }

    private ThreatHudModStatus GetStatusCore(
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        var deadlockDirectory = FindDeadlockDirectory();

        if (String.IsNullOrWhiteSpace(deadlockDirectory))
        {
            return new ThreatHudModStatus(
                null,
                false,
                false,
                false,
                false,
                null,
                null
            );
        }

        var paths = CreateDeadlockPaths(deadlockDirectory);
        var ownership = new InstalledVpkOwnership(false, false, false);
        string? vpkError = null;
        var isActive = false;
        string? activationError = null;

        try
        {
            ownership = InspectInstalledVpk(paths, cancellationToken);
        }
        catch (Exception error) when (
            error is IOException or
            UnauthorizedAccessException or
            InvalidDataException or
            InvalidOperationException or
            CryptographicException
        )
        {
            vpkError = error.Message;
        }

        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            isActive = IsAddonLoadingActive(
                ReadTextFile(paths.GameInfoPath).Text
            );
        }
        catch (Exception error) when (
            error is IOException or
            UnauthorizedAccessException or
            InvalidDataException or
            DecoderFallbackException
        )
        {
            activationError = error.Message;
        }

        return new ThreatHudModStatus(
            deadlockDirectory,
            ownership.Exists && ownership.IsOwned,
            isActive,
            ownership.Exists && !ownership.IsOwned,
            ownership.IsCurrentPayload,
            vpkError,
            activationError
        );
    }

    private void UpdateGameInfo(
        string gameInfoPath,
        bool active,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        var originalBytes = File.ReadAllBytes(gameInfoPath);
        var originalHash = SHA256.HashData(originalBytes);
        var textFile = DecodeTextFile(originalBytes);
        var updatedText = SetAddonLoadingActive(textFile.Text, active);

        if (String.Equals(
            updatedText,
            textFile.Text,
            StringComparison.Ordinal
        ))
        {
            return;
        }

        CreateBackupIfMissing(
            gameInfoPath + ".threathud.bak",
            originalBytes
        );

        var temporaryPath =
            gameInfoPath + $".threathud.{Guid.NewGuid():N}.tmp";

        try
        {
            WriteBytesToDisk(
                temporaryPath,
                EncodeTextFile(updatedText, textFile)
            );

            var validationText = ReadTextFile(temporaryPath).Text;
            if (IsAddonLoadingActive(validationText) != active)
            {
                throw new InvalidDataException(
                    "The updated gameinfo.gi failed validation."
                );
            }

            cancellationToken.ThrowIfCancellationRequested();
            EnsureDeadlockIsStopped();

            var currentHash = SHA256.HashData(
                File.ReadAllBytes(gameInfoPath)
            );
            if (!CryptographicOperations.FixedTimeEquals(
                originalHash,
                currentHash
            ))
            {
                throw new IOException(
                    "gameinfo.gi changed during the operation. " +
                    "No changes were applied."
                );
            }

            ReplaceFileAtomically(temporaryPath, gameInfoPath);
        }
        finally
        {
            TryDeleteFile(temporaryPath);
        }
    }

    private InstalledVpkOwnership InspectInstalledVpk(
        DeadlockPaths paths,
        CancellationToken cancellationToken
    )
    {
        if (!File.Exists(paths.InstalledVpkPath))
        {
            return new InstalledVpkOwnership(false, false, false);
        }

        var installedHash =
            CalculateFileHash(paths.InstalledVpkPath, cancellationToken);
        var embeddedHash = GetEmbeddedVpkHash();
        var recordedHash = TryReadRecordedHash(paths.InstalledHashPath);
        var isCurrentPayload = String.Equals(
            installedHash,
            embeddedHash,
            StringComparison.OrdinalIgnoreCase
        );
        var isOwned = isCurrentPayload || String.Equals(
            installedHash,
            recordedHash,
            StringComparison.OrdinalIgnoreCase
        );

        return new InstalledVpkOwnership(
            true,
            isOwned,
            isCurrentPayload
        );
    }

    private string GetEmbeddedVpkHash()
    {
        lock (_embeddedHashSync)
        {
            if (!String.IsNullOrWhiteSpace(_embeddedVpkHash))
            {
                return _embeddedVpkHash;
            }

            using var resource = OpenEmbeddedVpk();
            _embeddedVpkHash = Convert.ToHexString(
                SHA256.HashData(resource)
            );
            return _embeddedVpkHash;
        }
    }

    private Stream OpenEmbeddedVpk()
    {
        var stream = _resourceAssembly.GetManifestResourceStream(
            EmbeddedVpkResourceName
        ) ?? throw new InvalidOperationException(
            "The embedded Threat HUD VPK is missing. " +
            "Rebuild ThreatHudBridge.exe with pak57_dir.vpk."
        );

        try
        {
            Span<byte> header = stackalloc byte[sizeof(uint)];
            stream.ReadExactly(header);

            if (BinaryPrimitives.ReadUInt32LittleEndian(header) != VpkSignature)
            {
                throw new InvalidDataException(
                    "The embedded mod payload is not a valid VPK file."
                );
            }

            if (!stream.CanSeek)
            {
                throw new InvalidOperationException(
                    "The embedded VPK stream cannot be rewound."
                );
            }

            stream.Position = 0;
            return stream;
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    private static string? TryReadRecordedHash(string hashPath)
    {
        if (!File.Exists(hashPath))
        {
            return null;
        }

        try
        {
            var value = File.ReadAllText(hashPath, Encoding.ASCII).Trim();
            return value.Length == 64 && value.All(Uri.IsHexDigit)
                ? value
                : null;
        }
        catch (Exception error) when (
            error is IOException or UnauthorizedAccessException
        )
        {
            return null;
        }
    }

    private static string CalculateFileHash(
        string path,
        CancellationToken cancellationToken
    )
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            128 * 1024,
            FileOptions.SequentialScan
        );
        using var hasher = IncrementalHash.CreateHash(
            HashAlgorithmName.SHA256
        );
        var buffer = new byte[128 * 1024];
        int bytesRead;

        while ((bytesRead = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            hasher.AppendData(buffer, 0, bytesRead);
        }

        return Convert.ToHexString(hasher.GetHashAndReset());
    }

    private DeadlockPaths RequireDeadlockPaths()
    {
        var directory = FindDeadlockDirectory();
        if (String.IsNullOrWhiteSpace(directory))
        {
            throw new DirectoryNotFoundException(
                "Deadlock was not found in the configured Steam libraries."
            );
        }

        return CreateDeadlockPaths(directory);
    }

    private static DeadlockPaths CreateDeadlockPaths(string deadlockDirectory)
    {
        var citadelDirectory = Path.Combine(
            deadlockDirectory,
            "game",
            "citadel"
        );
        var addonsDirectory = Path.Combine(citadelDirectory, "addons");

        return new DeadlockPaths(
            Path.Combine(citadelDirectory, "gameinfo.gi"),
            addonsDirectory,
            Path.Combine(addonsDirectory, InstalledVpkFileName),
            Path.Combine(addonsDirectory, InstalledHashFileName)
        );
    }

    private static string? FindDeadlockDirectory()
    {
        var checkedDirectories = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase
        );

        foreach (var steamRoot in EnumerateSteamRoots())
        foreach (var libraryRoot in EnumerateSteamLibraries(steamRoot))
        {
            var steamAppsDirectory = Path.Combine(libraryRoot, "steamapps");
            var installDirectory =
                ReadDeadlockInstallDirectory(steamAppsDirectory);

            if (String.IsNullOrWhiteSpace(installDirectory))
            {
                continue;
            }

            try
            {
                var commonDirectory = Path.GetFullPath(
                    Path.Combine(steamAppsDirectory, "common")
                );
                var candidate = Path.GetFullPath(
                    Path.Combine(commonDirectory, installDirectory)
                );

                if (!IsPathInsideDirectory(candidate, commonDirectory) ||
                    !checkedDirectories.Add(candidate))
                {
                    continue;
                }

                if (File.Exists(Path.Combine(
                    candidate,
                    "game",
                    "citadel",
                    "gameinfo.gi"
                )))
                {
                    return candidate;
                }
            }
            catch (Exception error) when (
                error is ArgumentException or
                NotSupportedException or
                PathTooLongException
            )
            {
            }
        }

        return null;
    }

    private static IEnumerable<string> EnumerateSteamRoots()
    {
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        TryAddRegistrySteamRoot(
            roots,
            RegistryHive.CurrentUser,
            RegistryView.Default
        );
        TryAddRegistrySteamRoot(
            roots,
            RegistryHive.CurrentUser,
            RegistryView.Registry32
        );
        TryAddRegistrySteamRoot(
            roots,
            RegistryHive.CurrentUser,
            RegistryView.Registry64
        );
        TryAddRegistrySteamRoot(
            roots,
            RegistryHive.LocalMachine,
            RegistryView.Registry32
        );
        TryAddRegistrySteamRoot(
            roots,
            RegistryHive.LocalMachine,
            RegistryView.Registry64
        );

        var programFilesX86 = Environment.GetFolderPath(
            Environment.SpecialFolder.ProgramFilesX86
        );
        if (!String.IsNullOrWhiteSpace(programFilesX86))
        {
            TryAddDirectory(roots, Path.Combine(programFilesX86, "Steam"));
        }

        return roots;
    }

    private static void TryAddRegistrySteamRoot(
        ISet<string> roots,
        RegistryHive hive,
        RegistryView view
    )
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            using var steamKey = baseKey.OpenSubKey(
                @"Software\Valve\Steam"
            );
            if (steamKey is null)
            {
                return;
            }

            TryAddDirectory(
                roots,
                steamKey.GetValue("SteamPath") as string
            );
            TryAddDirectory(
                roots,
                steamKey.GetValue("InstallPath") as string
            );
            var steamExecutable =
                steamKey.GetValue("SteamExe") as string;
            if (!String.IsNullOrWhiteSpace(steamExecutable))
            {
                TryAddDirectory(
                    roots,
                    Path.GetDirectoryName(steamExecutable)
                );
            }
        }
        catch (Exception error) when (
            error is IOException or
            UnauthorizedAccessException or
            System.Security.SecurityException or
            PlatformNotSupportedException
        )
        {
        }
    }

    private static IEnumerable<string> EnumerateSteamLibraries(
        string steamRoot
    )
    {
        var libraries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        TryAddDirectory(libraries, steamRoot);
        var libraryFile = Path.Combine(
            steamRoot,
            "steamapps",
            "libraryfolders.vdf"
        );

        try
        {
            if (File.Exists(libraryFile))
            {
                var content = File.ReadAllText(libraryFile);
                foreach (Match match in LibraryPathPattern.Matches(content))
                {
                    TryAddDirectory(
                        libraries,
                        DecodeVdfString(match.Groups["value"].Value)
                    );
                }
            }
        }
        catch (Exception error) when (
            error is IOException or
            UnauthorizedAccessException or
            DecoderFallbackException
        )
        {
        }

        return libraries;
    }

    private static void TryAddDirectory(
        ISet<string> directories,
        string? path
    )
    {
        if (String.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            var fullPath = Path.GetFullPath(path).TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar
            );
            if (Directory.Exists(fullPath))
            {
                directories.Add(fullPath);
            }
        }
        catch (Exception error) when (
            error is ArgumentException or
            NotSupportedException or
            PathTooLongException
        )
        {
        }
    }

    private static string? ReadDeadlockInstallDirectory(
        string steamAppsDirectory
    )
    {
        var manifest = Path.Combine(
            steamAppsDirectory,
            $"appmanifest_{DeadlockAppId}.acf"
        );

        try
        {
            if (!File.Exists(manifest))
            {
                return null;
            }

            var match = InstallDirectoryPattern.Match(
                File.ReadAllText(manifest)
            );
            if (!match.Success)
            {
                return null;
            }

            var value = DecodeVdfString(match.Groups["value"].Value);
            return String.IsNullOrWhiteSpace(value) || Path.IsPathRooted(value)
                ? null
                : value;
        }
        catch (Exception error) when (
            error is IOException or
            UnauthorizedAccessException or
            DecoderFallbackException
        )
        {
            return null;
        }
    }

    private static string DecodeVdfString(string value)
    {
        var result = new StringBuilder(value.Length);

        for (var index = 0; index < value.Length; index += 1)
        {
            if (value[index] == '\\' &&
                index + 1 < value.Length &&
                value[index + 1] is '\\' or '"')
            {
                index += 1;
            }

            result.Append(value[index]);
        }

        return result.ToString();
    }

    private static bool IsPathInsideDirectory(
        string path,
        string directory
    )
    {
        var relative = Path.GetRelativePath(directory, path);
        return !Path.IsPathRooted(relative) &&
            !String.Equals(relative, "..", StringComparison.Ordinal) &&
            !relative.StartsWith(
                ".." + Path.DirectorySeparatorChar,
                StringComparison.Ordinal
            ) &&
            !relative.StartsWith(
                ".." + Path.AltDirectorySeparatorChar,
                StringComparison.Ordinal
            );
    }

    private static SearchPathsBlock FindSearchPathsBlock(string gameInfo)
    {
        var index = 0;

        while (index < gameInfo.Length)
        {
            SkipTrivia(gameInfo, ref index);
            if (index >= gameInfo.Length)
            {
                break;
            }

            var tokenStart = index;
            string? token;

            if (gameInfo[index] == '"')
            {
                token = ReadQuotedToken(gameInfo, ref index);
            }
            else if (IsKeyValuesTokenCharacter(gameInfo[index]))
            {
                while (index < gameInfo.Length &&
                    IsKeyValuesTokenCharacter(gameInfo[index]))
                {
                    index += 1;
                }

                token = gameInfo.Substring(
                    tokenStart,
                    index - tokenStart
                );
            }
            else
            {
                index += 1;
                continue;
            }

            if (!String.Equals(
                    token,
                    "SearchPaths",
                    StringComparison.OrdinalIgnoreCase
                ) ||
                !IsFirstTokenOnLine(gameInfo, tokenStart))
            {
                continue;
            }

            var openingBrace = index;
            SkipTrivia(gameInfo, ref openingBrace);
            if (openingBrace < gameInfo.Length &&
                gameInfo[openingBrace] == '{')
            {
                return new SearchPathsBlock(
                    openingBrace,
                    FindMatchingBrace(gameInfo, openingBrace)
                );
            }
        }

        throw new InvalidDataException(
            "gameinfo.gi does not contain a valid SearchPaths block."
        );
    }

    private static string ReadQuotedToken(string text, ref int index)
    {
        index += 1;
        var token = new StringBuilder();

        while (index < text.Length)
        {
            var character = text[index];
            var next = index + 1 < text.Length
                ? text[index + 1]
                : '\0';

            if (character == '\\' && next is '\\' or '"')
            {
                token.Append(next);
                index += 2;
            }
            else if (character == '"')
            {
                index += 1;
                return token.ToString();
            }
            else
            {
                token.Append(character);
                index += 1;
            }
        }

        throw new InvalidDataException(
            "gameinfo.gi contains an unterminated string."
        );
    }

    private static bool IsKeyValuesTokenCharacter(char value) =>
        !Char.IsWhiteSpace(value) &&
        value is not '{' and not '}' and not '"' and not '/';

    private static bool IsFirstTokenOnLine(string text, int tokenStart)
    {
        for (var index = tokenStart - 1; index >= 0; index -= 1)
        {
            if (text[index] is '\r' or '\n')
            {
                return true;
            }
            if (text[index] is not ' ' and not '\t')
            {
                return false;
            }
        }

        return true;
    }

    private static void SkipTrivia(string text, ref int index)
    {
        while (index < text.Length)
        {
            if (Char.IsWhiteSpace(text[index]))
            {
                index += 1;
                continue;
            }

            if (index + 1 < text.Length &&
                text[index] == '/' && text[index + 1] == '/')
            {
                index += 2;
                while (index < text.Length &&
                    text[index] is not '\r' and not '\n')
                {
                    index += 1;
                }
                continue;
            }

            if (index + 1 < text.Length &&
                text[index] == '/' && text[index + 1] == '*')
            {
                var end = text.IndexOf(
                    "*/",
                    index + 2,
                    StringComparison.Ordinal
                );
                if (end < 0)
                {
                    throw new InvalidDataException(
                        "gameinfo.gi contains an unterminated comment."
                    );
                }
                index = end + 2;
                continue;
            }

            break;
        }
    }

    private static int FindMatchingBrace(string text, int openingBrace)
    {
        var depth = 0;
        var inString = false;
        var inLineComment = false;
        var inBlockComment = false;

        for (var index = openingBrace; index < text.Length; index += 1)
        {
            var character = text[index];
            var next = index + 1 < text.Length ? text[index + 1] : '\0';

            if (inLineComment)
            {
                if (character is '\r' or '\n')
                {
                    inLineComment = false;
                }
                continue;
            }

            if (inBlockComment)
            {
                if (character == '*' && next == '/')
                {
                    inBlockComment = false;
                    index += 1;
                }
                continue;
            }

            if (inString)
            {
                if (character == '\\' && next != '\0')
                {
                    index += 1;
                }
                else if (character == '"')
                {
                    inString = false;
                }
                continue;
            }

            if (character == '/' && next == '/')
            {
                inLineComment = true;
                index += 1;
            }
            else if (character == '/' && next == '*')
            {
                inBlockComment = true;
                index += 1;
            }
            else if (character == '"')
            {
                inString = true;
            }
            else if (character == '{')
            {
                depth += 1;
            }
            else if (character == '}')
            {
                depth -= 1;
                if (depth == 0)
                {
                    return index;
                }
                if (depth < 0)
                {
                    break;
                }
            }
        }

        throw new InvalidDataException(
            "SearchPaths has unbalanced braces."
        );
    }

    private static IReadOnlyList<SearchPathLine> FindSearchPathLines(
        string text,
        SearchPathsBlock block
    )
    {
        var result = new List<SearchPathLine>();
        var lineStart = block.OpeningBrace + 1;
        var inBlockComment = false;
        var nestedDepth = 0;

        while (lineStart < block.ClosingBrace)
        {
            var contentEnd = lineStart;
            while (contentEnd < block.ClosingBrace &&
                text[contentEnd] is not '\r' and not '\n')
            {
                contentEnd += 1;
            }

            var lineEnd = contentEnd;
            if (lineEnd < block.ClosingBrace && text[lineEnd] == '\r')
            {
                lineEnd += 1;
            }
            if (lineEnd < block.ClosingBrace && text[lineEnd] == '\n')
            {
                lineEnd += 1;
            }

            var line = text.Substring(lineStart, contentEnd - lineStart);
            var withoutComments = RemoveComments(
                line,
                ref inBlockComment,
                out var lineComment
            );
            var tokens = nestedDepth == 0
                ? TokenizeKeyValuesLine(withoutComments)
                : Array.Empty<string>();

            if (tokens.Count >= 2)
            {
                var key = tokens[0];
                var value = tokens[1].Replace('\\', '/').TrimEnd('/');
                var isGamePath = String.Equals(
                    key,
                    "Game",
                    StringComparison.OrdinalIgnoreCase
                ) &&
                (
                    String.Equals(
                        value,
                        "citadel",
                        StringComparison.OrdinalIgnoreCase
                    ) ||
                    String.Equals(
                        value,
                        "citadel/addons",
                        StringComparison.OrdinalIgnoreCase
                    )
                );
                var isBasePath =
                    (
                        String.Equals(
                            key,
                            "Mod",
                            StringComparison.OrdinalIgnoreCase
                        ) ||
                        String.Equals(
                            key,
                            "Write",
                            StringComparison.OrdinalIgnoreCase
                        )
                    ) &&
                    String.Equals(
                        value,
                        "citadel",
                        StringComparison.OrdinalIgnoreCase
                    );

                if (isGamePath || isBasePath)
                {
                    result.Add(new SearchPathLine(
                        lineStart,
                        lineEnd,
                        GetLeadingWhitespace(line),
                        key,
                        value,
                        tokens.Count == 2 &&
                        String.Equals(
                            lineComment,
                            ManagedSearchPathComment,
                            StringComparison.OrdinalIgnoreCase
                        )
                    ));
                }
            }

            nestedDepth += GetBraceDelta(withoutComments);
            if (nestedDepth < 0)
            {
                throw new InvalidDataException(
                    "SearchPaths has invalid nested braces."
                );
            }

            if (lineEnd == lineStart)
            {
                break;
            }
            lineStart = lineEnd;
        }

        return result;
    }

    private static int GetBraceDelta(string value)
    {
        var delta = 0;
        var inString = false;

        for (var index = 0; index < value.Length; index += 1)
        {
            var character = value[index];
            var next = index + 1 < value.Length
                ? value[index + 1]
                : '\0';

            if (inString)
            {
                if (character == '\\' && next != '\0')
                {
                    index += 1;
                }
                else if (character == '"')
                {
                    inString = false;
                }
                continue;
            }

            if (character == '"')
            {
                inString = true;
            }
            else if (character == '{')
            {
                delta += 1;
            }
            else if (character == '}')
            {
                delta -= 1;
            }
        }

        return delta;
    }

    private static string RemoveComments(
        string line,
        ref bool inBlockComment,
        out string? lineComment
    )
    {
        var result = new StringBuilder(line.Length);
        var inString = false;
        lineComment = null;

        for (var index = 0; index < line.Length; index += 1)
        {
            var character = line[index];
            var next = index + 1 < line.Length ? line[index + 1] : '\0';

            if (inBlockComment)
            {
                if (character == '*' && next == '/')
                {
                    inBlockComment = false;
                    index += 1;
                }
                continue;
            }

            if (inString)
            {
                result.Append(character);
                if (character == '\\' && next != '\0')
                {
                    index += 1;
                    result.Append(line[index]);
                }
                else if (character == '"')
                {
                    inString = false;
                }
                continue;
            }

            if (character == '/' && next == '/')
            {
                lineComment = line.Substring(index + 2).Trim();
                break;
            }
            if (character == '/' && next == '*')
            {
                inBlockComment = true;
                index += 1;
                continue;
            }

            if (character == '"')
            {
                inString = true;
            }
            result.Append(character);
        }

        return result.ToString();
    }

    private static IReadOnlyList<string> TokenizeKeyValuesLine(string line)
    {
        var tokens = new List<string>();
        var index = 0;

        while (index < line.Length)
        {
            while (index < line.Length && Char.IsWhiteSpace(line[index]))
            {
                index += 1;
            }
            if (index >= line.Length)
            {
                break;
            }

            var token = new StringBuilder();
            if (line[index] == '"')
            {
                index += 1;
                var closed = false;
                while (index < line.Length)
                {
                    var character = line[index];
                    var next = index + 1 < line.Length
                        ? line[index + 1]
                        : '\0';

                    if (character == '\\' && next is '\\' or '"')
                    {
                        token.Append(next);
                        index += 2;
                    }
                    else if (character == '"')
                    {
                        index += 1;
                        closed = true;
                        break;
                    }
                    else
                    {
                        token.Append(character);
                        index += 1;
                    }
                }

                if (!closed)
                {
                    return Array.Empty<string>();
                }
            }
            else
            {
                while (index < line.Length &&
                    !Char.IsWhiteSpace(line[index]))
                {
                    token.Append(line[index]);
                    index += 1;
                }
            }

            tokens.Add(token.ToString());
        }

        return tokens;
    }

    private static string RemoveLines(
        string text,
        IEnumerable<SearchPathLine> lines
    )
    {
        var result = text;
        foreach (var line in lines.OrderByDescending(value => value.Start))
        {
            result = result.Remove(line.Start, line.End - line.Start);
        }
        return result;
    }

    private static string GetLeadingWhitespace(string value)
    {
        var length = 0;
        while (length < value.Length && value[length] is ' ' or '\t')
        {
            length += 1;
        }
        return value.Substring(0, length);
    }

    private static string DetectNewline(string value)
    {
        if (value.Contains("\r\n", StringComparison.Ordinal))
        {
            return "\r\n";
        }
        if (value.Contains('\n'))
        {
            return "\n";
        }
        if (value.Contains('\r'))
        {
            return "\r";
        }
        return Environment.NewLine;
    }

    private static TextFileContent ReadTextFile(string path) =>
        DecodeTextFile(File.ReadAllBytes(path));

    private static TextFileContent DecodeTextFile(byte[] bytes)
    {
        var utf32Be = new UTF32Encoding(true, true, true);
        Encoding encoding;
        var preambleLength = 0;

        if (bytes.AsSpan().StartsWith(Encoding.UTF32.GetPreamble()))
        {
            encoding = new UTF32Encoding(false, true, true);
            preambleLength = Encoding.UTF32.GetPreamble().Length;
        }
        else if (bytes.AsSpan().StartsWith(utf32Be.GetPreamble()))
        {
            encoding = utf32Be;
            preambleLength = utf32Be.GetPreamble().Length;
        }
        else if (bytes.AsSpan().StartsWith(Encoding.UTF8.GetPreamble()))
        {
            encoding = new UTF8Encoding(true, true);
            preambleLength = Encoding.UTF8.GetPreamble().Length;
        }
        else if (bytes.AsSpan().StartsWith(Encoding.Unicode.GetPreamble()))
        {
            encoding = new UnicodeEncoding(false, true, true);
            preambleLength = Encoding.Unicode.GetPreamble().Length;
        }
        else if (bytes.AsSpan().StartsWith(
            Encoding.BigEndianUnicode.GetPreamble()
        ))
        {
            encoding = new UnicodeEncoding(true, true, true);
            preambleLength = Encoding.BigEndianUnicode.GetPreamble().Length;
        }
        else
        {
            encoding = new UTF8Encoding(false, true);
        }

        return new TextFileContent(
            encoding.GetString(
                bytes,
                preambleLength,
                bytes.Length - preambleLength
            ),
            encoding,
            preambleLength > 0
        );
    }

    private static byte[] EncodeTextFile(
        string text,
        TextFileContent source
    )
    {
        var body = source.Encoding.GetBytes(text);
        if (!source.HasPreamble)
        {
            return body;
        }

        var preamble = source.Encoding.GetPreamble();
        var result = new byte[preamble.Length + body.Length];
        preamble.CopyTo(result, 0);
        body.CopyTo(result, preamble.Length);
        return result;
    }

    private static void CreateBackupIfMissing(
        string backupPath,
        byte[] originalBytes
    )
    {
        if (File.Exists(backupPath))
        {
            return;
        }

        var temporaryPath = backupPath + $".{Guid.NewGuid():N}.tmp";
        try
        {
            WriteBytesToDisk(temporaryPath, originalBytes);
            try
            {
                File.Move(temporaryPath, backupPath, overwrite: false);
            }
            catch (IOException) when (File.Exists(backupPath))
            {
            }
        }
        finally
        {
            TryDeleteFile(temporaryPath);
        }
    }

    private static void WriteTextAtomically(string path, string value)
    {
        var temporaryPath = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            WriteBytesToDisk(
                temporaryPath,
                new UTF8Encoding(false).GetBytes(value)
            );
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            TryDeleteFile(temporaryPath);
        }
    }

    private static void WriteBytesToDisk(string path, byte[] bytes)
    {
        using var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            64 * 1024,
            FileOptions.SequentialScan
        );
        stream.Write(bytes, 0, bytes.Length);
        stream.Flush(flushToDisk: true);
    }

    private static void ReplaceFileAtomically(
        string sourcePath,
        string destinationPath
    )
    {
        if (OperatingSystem.IsWindows())
        {
            File.Replace(
                sourcePath,
                destinationPath,
                destinationBackupFileName: null,
                ignoreMetadataErrors: true
            );
            return;
        }

        File.Move(sourcePath, destinationPath, overwrite: true);
    }

    private static void EnsureDeadlockIsStopped()
    {
        var processes = Process.GetProcessesByName(DeadlockProcessName);
        try
        {
            if (processes.Length > 0)
            {
                throw new InvalidOperationException(
                    "Close Deadlock before installing or changing mod settings."
                );
            }
        }
        finally
        {
            foreach (var process in processes)
            {
                process.Dispose();
            }
        }
    }

    private async Task<T> RunExclusiveAsync<T>(
        Func<Task<T>> action,
        CancellationToken cancellationToken
    )
    {
        await _operationLock.WaitAsync(cancellationToken);

        try
        {
            return await action();
        }
        finally
        {
            _operationLock.Release();
        }
    }

    private async Task RunExclusiveAsync(
        Func<Task> action,
        CancellationToken cancellationToken
    )
    {
        await _operationLock.WaitAsync(cancellationToken);

        try
        {
            await action();
        }
        finally
        {
            _operationLock.Release();
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception error) when (
            error is IOException or UnauthorizedAccessException
        )
        {
        }
    }

    private sealed record DeadlockPaths(
        string GameInfoPath,
        string AddonsDirectory,
        string InstalledVpkPath,
        string InstalledHashPath
    );

    private sealed record InstalledVpkOwnership(
        bool Exists,
        bool IsOwned,
        bool IsCurrentPayload
    );

    private sealed record SearchPathsBlock(
        int OpeningBrace,
        int ClosingBrace
    );

    private sealed record SearchPathLine(
        int Start,
        int End,
        string Indent,
        string Key,
        string Value,
        bool IsManaged
    );

    private sealed record TextFileContent(
        string Text,
        Encoding Encoding,
        bool HasPreamble
    );
}
