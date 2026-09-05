using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
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

    public bool IsUpdateAvailable =>
        IsInstalled &&
        !IsCurrentPayload;

    // Kept as an alias so UI code can describe the intent without changing
    // the positional record contract used by older Bridge sources.
    public string? VpkBlockReason => VpkError;
}

internal sealed class ThreatHudModManagerService
{
    private const string DeadlockProcessName = "deadlock";
    private const string DeadlockAppId = "1422450";
    private const int FirstVpkNumber = 1;
    private const int LastVpkNumber = 99;
    private const int LegacyVpkNumber = 57;
    private const string OwnershipMarkerSuffix =
        ".threathud.sha256";
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

    private static readonly Regex InstalledVpkFilePattern = new(
        @"\Apak(?<number>[0-9]{2})_dir\.vpk\z",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
    );

    private static readonly Regex OwnershipMarkerFilePattern = new(
        @"\Apak(?<number>[0-9]{2})_dir\.vpk\.threathud\.sha256\z",
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

    public Task UpdateAsync(
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

        var inventory = await Task.Run(
            () => InspectVpkInventory(paths, cancellationToken),
            cancellationToken
        );

        ThrowIfVpkBlocked(inventory);

        var installedMod =
            inventory.ManagedCandidates.SingleOrDefault();

        if (
            installedMod is not null &&
            installedMod.IsCurrentPayload
        )
        {
            if (!installedMod.HasOwnershipMarker)
            {
                await Task.Run(
                    () => AdoptLegacyOwnershipMarkerCore(
                        paths,
                        installedMod,
                        cancellationToken
                    ),
                    cancellationToken
                );
            }

            return;
        }

        var embeddedHash = await Task.Run(
            GetEmbeddedVpkHash,
            cancellationToken
        );
        var temporaryPath = Path.Combine(
            paths.AddonsDirectory,
            $".threathud.{Guid.NewGuid():N}.vpk.tmp"
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
            await Task.Run(
                () => CommitPreparedVpkCore(
                    paths,
                    temporaryPath,
                    installedMod,
                    embeddedHash,
                    cancellationToken
                ),
                cancellationToken
            );
        }
        finally
        {
            TryDeleteFile(temporaryPath);
        }
    }

    private void CommitPreparedVpkCore(
        DeadlockPaths paths,
        string preparedVpkPath,
        ManagedVpkCandidate? originallyInstalledMod,
        string embeddedHash,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureDeadlockIsStopped();

        // Re-scan immediately before the first rename. File.Move(false)
        // remains the final guard if another process wins the race after it.
        var currentInventory = InspectVpkInventory(
            paths,
            cancellationToken
        );
        ThrowIfVpkBlocked(currentInventory);

        if (originallyInstalledMod is null)
        {
            if (currentInventory.ManagedCandidates.Count != 0)
            {
                throw new InvalidOperationException(
                    "The VPK installation changed while the new mod was " +
                    "being prepared. No files were changed."
                );
            }

            InstallFreshVpkCore(
                paths,
                preparedVpkPath,
                currentInventory,
                embeddedHash,
                cancellationToken
            );
            return;
        }

        if (currentInventory.ManagedCandidates.Count != 1)
        {
            throw new InvalidOperationException(
                "The installed Threat HUD VPK changed while the update " +
                "was being prepared. No files were changed."
            );
        }

        var currentMod = currentInventory.ManagedCandidates[0];
        if (!IsSameManagedVpk(originallyInstalledMod, currentMod))
        {
            throw new InvalidOperationException(
                "The installed Threat HUD VPK changed while the update " +
                "was being prepared. No files were changed."
            );
        }

        if (currentMod.IsCurrentPayload)
        {
            return;
        }

        UpdateInstalledVpkCore(
            preparedVpkPath,
            currentMod,
            embeddedHash,
            cancellationToken
        );
    }

    private void InstallFreshVpkCore(
        DeadlockPaths paths,
        string preparedVpkPath,
        VpkInventory inventory,
        string embeddedHash,
        CancellationToken cancellationToken
    )
    {
        var targetNumber = inventory.NextInstallNumber ??
            throw new InvalidOperationException(
                CreateNoAvailableVpkSlotMessage()
            );
        var vpkPath = Path.Combine(
            paths.AddonsDirectory,
            FormatVpkFileName(targetNumber)
        );
        var markerPath = vpkPath + OwnershipMarkerSuffix;
        var rollbackPath = Path.Combine(
            paths.AddonsDirectory,
            $".{Path.GetFileName(vpkPath)}.{Guid.NewGuid():N}.rollback"
        );
        var payloadInstalled = false;

        cancellationToken.ThrowIfCancellationRequested();
        EnsureDeadlockIsStopped();

        // Never overwrite either another mod or another ownership marker.
        File.Move(preparedVpkPath, vpkPath, overwrite: false);
        payloadInstalled = true;

        try
        {
            WriteNewTextAtomically(
                markerPath,
                embeddedHash + Environment.NewLine
            );
        }
        catch (Exception operationError)
        {
            try
            {
                if (!payloadInstalled || !File.Exists(vpkPath))
                {
                    throw new FileNotFoundException(
                        "The newly installed VPK is missing.",
                        vpkPath
                    );
                }

                File.Move(vpkPath, rollbackPath, overwrite: false);
                var rollbackHash = CalculateFileHash(
                    rollbackPath,
                    CancellationToken.None
                );
                if (!String.Equals(
                    rollbackHash,
                    embeddedHash,
                    StringComparison.OrdinalIgnoreCase
                ))
                {
                    throw new IOException(
                        "The new VPK changed during marker rollback. " +
                        "The unexpected file was preserved at: " +
                        rollbackPath
                    );
                }

                File.Delete(rollbackPath);
            }
            catch (Exception rollbackError)
            {
                var preservedPath = File.Exists(rollbackPath)
                    ? rollbackPath
                    : vpkPath;
                throw new IOException(
                    "The ownership marker could not be created and the " +
                    "new VPK could not be rolled back completely. " +
                    "Preserved VPK location: " + preservedPath,
                    new AggregateException(operationError, rollbackError)
                );
            }

            throw;
        }
    }

    private void UpdateInstalledVpkCore(
        string preparedVpkPath,
        ManagedVpkCandidate installedMod,
        string embeddedHash,
        CancellationToken cancellationToken
    )
    {
        var expectedInstalledHash = installedMod.InstalledHash;

        var quarantinePath =
            Path.Combine(
                Path.GetDirectoryName(installedMod.VpkPath)!,
                $".{Path.GetFileName(installedMod.VpkPath)}." +
                $"{Guid.NewGuid():N}.updating"
            );
        var markerQuarantinePath =
            Path.Combine(
                Path.GetDirectoryName(installedMod.MarkerPath)!,
                $".{Path.GetFileName(installedMod.MarkerPath)}." +
                $"{Guid.NewGuid():N}.updating"
            );

        var failedPayloadPath =
            Path.Combine(
                Path.GetDirectoryName(installedMod.VpkPath)!,
                $".{Path.GetFileName(installedMod.VpkPath)}." +
                $"{Guid.NewGuid():N}.update-failed"
            );

        var markerQuarantined = false;
        var newPayloadInstalled = false;

        cancellationToken.ThrowIfCancellationRequested();
        EnsureDeadlockIsStopped();

        File.Move(
            installedMod.VpkPath,
            quarantinePath,
            overwrite: false
        );

        try
        {
            var quarantinedHash =
                CalculateFileHash(
                    quarantinePath,
                    cancellationToken
                );

            if (!String.Equals(
                quarantinedHash,
                expectedInstalledHash,
                StringComparison.OrdinalIgnoreCase
            ))
            {
                throw new InvalidOperationException(
                    "The installed VPK changed while the update was " +
                    "being prepared. The update was canceled."
                );
            }

            var currentRecordedHash = ReadRecordedHashStrict(
                installedMod.MarkerPath
            );
            if (!String.Equals(
                currentRecordedHash,
                expectedInstalledHash,
                StringComparison.OrdinalIgnoreCase
            ))
            {
                throw new InvalidOperationException(
                    "The ownership marker changed while the update was " +
                    "being prepared. The update was canceled."
                );
            }

            File.Move(
                installedMod.MarkerPath,
                markerQuarantinePath,
                overwrite: false
            );
            markerQuarantined = true;

            var quarantinedRecordedHash = ReadRecordedHashStrict(
                markerQuarantinePath
            );
            if (!String.Equals(
                quarantinedRecordedHash,
                expectedInstalledHash,
                StringComparison.OrdinalIgnoreCase
            ))
            {
                throw new InvalidOperationException(
                    "The ownership marker changed while the update was " +
                    "being prepared. The update was canceled."
                );
            }

            cancellationToken.ThrowIfCancellationRequested();
            EnsureDeadlockIsStopped();

            File.Move(
                preparedVpkPath,
                installedMod.VpkPath,
                overwrite: false
            );

            newPayloadInstalled = true;

            /*
             * Do not observe cancellation between installing the new VPK
             * and committing its ownership hash. Otherwise the next Bridge
             * build could mistake this payload for an unrelated mod.
             */
            WriteNewTextAtomically(
                installedMod.MarkerPath,
                embeddedHash + Environment.NewLine
            );

            TryDeleteFile(quarantinePath);
            TryDeleteFile(markerQuarantinePath);
        }
        catch (Exception operationError)
        {
            try
            {
                if (newPayloadInstalled)
                {
                    if (
                        !File.Exists(
                            installedMod.VpkPath
                        )
                    )
                    {
                        throw new IOException(
                            "The newly installed VPK is missing."
                        );
                    }

                    File.Move(
                        installedMod.VpkPath,
                        failedPayloadPath,
                        overwrite: false
                    );

                    var failedPayloadHash =
                        CalculateFileHash(
                            failedPayloadPath,
                            CancellationToken.None
                        );

                    if (
                        !String.Equals(
                            failedPayloadHash,
                            embeddedHash,
                            StringComparison.OrdinalIgnoreCase
                        )
                    )
                    {
                        throw new IOException(
                            "The VPK path changed during update rollback. " +
                            "The unexpected file was preserved at: " +
                            failedPayloadPath
                        );
                    }
                }

                if (
                    File.Exists(
                        installedMod.VpkPath
                    )
                )
                {
                    throw new IOException(
                        "The original VPK path is occupied."
                    );
                }

                if (!File.Exists(quarantinePath))
                {
                    throw new FileNotFoundException(
                        "The preserved previous VPK is missing.",
                        quarantinePath
                    );
                }

                File.Move(
                    quarantinePath,
                    installedMod.VpkPath,
                    overwrite: false
                );
                if (markerQuarantined)
                {
                    if (File.Exists(installedMod.MarkerPath))
                    {
                        throw new IOException(
                            "The original ownership marker path is occupied."
                        );
                    }

                    File.Move(
                        markerQuarantinePath,
                        installedMod.MarkerPath,
                        overwrite: false
                    );
                    markerQuarantined = false;
                }

                TryDeleteFile(
                    failedPayloadPath
                );
            }
            catch (Exception restoreError)
            {
                var previousVpkLocation =
                    File.Exists(quarantinePath)
                        ? quarantinePath
                        : installedMod.VpkPath;

                var previousMarkerLocation =
                    File.Exists(markerQuarantinePath)
                        ? markerQuarantinePath
                        : installedMod.MarkerPath;

                var failedPayloadDetail =
                    File.Exists(failedPayloadPath)
                        ? " The file found at the VPK path was preserved at: " +
                          failedPayloadPath
                        : String.Empty;

                throw new IOException(
                    "The update was aborted, but the previous mod state " +
                    "could not be restored completely. Previous VPK " +
                    "location: " +
                    previousVpkLocation +
                    ". Previous marker location: " +
                    previousMarkerLocation +
                    failedPayloadDetail,
                    new AggregateException(
                        operationError,
                        restoreError
                    )
                );
            }

            throw;
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
        var inventory = InspectVpkInventory(paths, cancellationToken);
        ThrowIfVpkBlocked(inventory);

        if (inventory.ManagedCandidates.Count == 0)
        {
            return;
        }

        var installedMod = inventory.ManagedCandidates.Single();

        if (!installedMod.HasOwnershipMarker)
        {
            AdoptLegacyOwnershipMarkerCore(
                paths,
                installedMod,
                cancellationToken
            );
            inventory = InspectVpkInventory(paths, cancellationToken);
            ThrowIfVpkBlocked(inventory);

            if (inventory.ManagedCandidates.Count != 1)
            {
                throw new InvalidOperationException(
                    "The installed Threat HUD VPK changed while its " +
                    "legacy ownership marker was being created."
                );
            }

            installedMod = inventory.ManagedCandidates[0];
            if (!installedMod.HasOwnershipMarker)
            {
                throw new InvalidOperationException(
                    "The legacy Threat HUD VPK ownership marker could " +
                    "not be verified."
                );
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        EnsureDeadlockIsStopped();

        // Re-scan immediately before renaming either owned file.
        var currentInventory = InspectVpkInventory(
            paths,
            cancellationToken
        );
        ThrowIfVpkBlocked(currentInventory);
        if (
            currentInventory.ManagedCandidates.Count != 1 ||
            !IsSameManagedVpk(
                installedMod,
                currentInventory.ManagedCandidates[0]
            )
        )
        {
            throw new InvalidOperationException(
                "The installed Threat HUD VPK changed while uninstall " +
                "was being prepared. No files were changed."
            );
        }

        var quarantinePath = Path.Combine(
            paths.AddonsDirectory,
            $".{Path.GetFileName(installedMod.VpkPath)}." +
            $"{Guid.NewGuid():N}.uninstalling"
        );
        var markerQuarantinePath = Path.Combine(
            paths.AddonsDirectory,
            $".{Path.GetFileName(installedMod.MarkerPath)}." +
            $"{Guid.NewGuid():N}.uninstalling"
        );

        File.Move(
            installedMod.VpkPath,
            quarantinePath,
            overwrite: false
        );

        var markerQuarantined = false;

        try
        {
            var quarantinedHash = CalculateFileHash(
                quarantinePath,
                cancellationToken
            );
            if (!String.Equals(
                quarantinedHash,
                installedMod.InstalledHash,
                StringComparison.OrdinalIgnoreCase
            ))
            {
                throw new InvalidOperationException(
                    "The installed Threat HUD VPK changed while it was " +
                    "being prepared for uninstall."
                );
            }

            var recordedHash = ReadRecordedHashStrict(
                installedMod.MarkerPath
            );
            if (!String.Equals(
                recordedHash,
                installedMod.InstalledHash,
                StringComparison.OrdinalIgnoreCase
            ))
            {
                throw new InvalidOperationException(
                    "The ownership marker changed while uninstall was " +
                    "being prepared."
                );
            }

            File.Move(
                installedMod.MarkerPath,
                markerQuarantinePath,
                overwrite: false
            );
            markerQuarantined = true;

            var quarantinedRecordedHash = ReadRecordedHashStrict(
                markerQuarantinePath
            );
            if (!String.Equals(
                quarantinedRecordedHash,
                installedMod.InstalledHash,
                StringComparison.OrdinalIgnoreCase
            ))
            {
                throw new InvalidOperationException(
                    "The ownership marker changed while uninstall was " +
                    "being prepared."
                );
            }

            cancellationToken.ThrowIfCancellationRequested();
            EnsureDeadlockIsStopped();

            // Once both files have unique quarantine names, the visible
            // install is gone. Cleanup failures can safely leave only hidden
            // Bridge quarantine files and must not touch foreign pakNN files.
            File.Delete(quarantinePath);
            TryDeleteFile(markerQuarantinePath);
        }
        catch (Exception operationError)
        {
            if (File.Exists(quarantinePath))
            {
                try
                {
                    if (File.Exists(installedMod.VpkPath))
                    {
                        throw new IOException(
                            "The original VPK path is occupied."
                        );
                    }

                    File.Move(
                        quarantinePath,
                        installedMod.VpkPath,
                        overwrite: false
                    );

                    if (markerQuarantined)
                    {
                        if (File.Exists(installedMod.MarkerPath))
                        {
                            throw new IOException(
                                "The ownership marker path is occupied."
                            );
                        }

                        File.Move(
                            markerQuarantinePath,
                            installedMod.MarkerPath,
                            overwrite: false
                        );
                    }
                }
                catch (Exception restoreError) when (
                    restoreError is IOException or
                    UnauthorizedAccessException
                )
                {
                    throw new IOException(
                        "The uninstall was aborted, but the VPK could not " +
                        "be restored automatically. The preserved file is: " +
                        quarantinePath +
                        (File.Exists(markerQuarantinePath)
                            ? ". Preserved marker: " + markerQuarantinePath
                            : String.Empty),
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
        VpkInventory? inventory = null;
        string? vpkError = null;
        var isActive = false;
        string? activationError = null;

        try
        {
            inventory = InspectVpkInventory(paths, cancellationToken);
            vpkError = inventory.BlockReason;
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

        try
        {
            // Validate the embedded payload during status refresh so a
            // missing/corrupt resource disables the button with a reason,
            // instead of failing only after the user clicks Install.
            _ = GetEmbeddedVpkHash();
        }
        catch (Exception error) when (
            error is IOException or
            InvalidDataException or
            InvalidOperationException or
            CryptographicException
        )
        {
            vpkError = CombineBlockReasons(vpkError, error.Message);
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

        var installedMod =
            inventory is { ManagedCandidates.Count: 1 }
                ? inventory.ManagedCandidates[0]
                : null;

        return new ThreatHudModStatus(
            deadlockDirectory,
            installedMod is not null,
            isActive,
            inventory?.HasOwnershipConflict == true,
            installedMod?.IsCurrentPayload == true,
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

    private VpkInventory InspectVpkInventory(
        DeadlockPaths paths,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (File.Exists(paths.AddonsDirectory))
        {
            throw new IOException(
                "The Deadlock addons path is occupied by a file: " +
                paths.AddonsDirectory
            );
        }

        if (!Directory.Exists(paths.AddonsDirectory))
        {
            return new VpkInventory(
                Array.Empty<NamedVpkFile>(),
                Array.Empty<ManagedVpkCandidate>(),
                FirstVpkNumber,
                false,
                null
            );
        }

        var allFiles = Directory.EnumerateFiles(
            paths.AddonsDirectory,
            "*",
            SearchOption.TopDirectoryOnly
        ).ToArray();
        cancellationToken.ThrowIfCancellationRequested();

        var vpkFiles = new List<NamedVpkFile>();
        var markerFiles = new List<NamedMarkerFile>();

        foreach (var path in allFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fileName = Path.GetFileName(path);
            if (String.IsNullOrEmpty(fileName))
            {
                continue;
            }

            if (TryParseVpkFileName(fileName, out var vpkNumber))
            {
                vpkFiles.Add(new NamedVpkFile(vpkNumber, path));
                continue;
            }

            if (TryParseOwnershipMarkerFileName(
                fileName,
                out var markerNumber
            ))
            {
                markerFiles.Add(new NamedMarkerFile(markerNumber, path));
            }
        }

        var problems = new List<string>();
        var managedCandidates = new List<ManagedVpkCandidate>();
        string? embeddedHash = null;

        foreach (var markerGroup in markerFiles
            .GroupBy(file => file.Number)
            .OrderBy(group => group.Key))
        {
            var markers = markerGroup.ToArray();
            var matchingVpks = vpkFiles
                .Where(file => file.Number == markerGroup.Key)
                .ToArray();
            var canonicalName = FormatVpkFileName(markerGroup.Key);

            if (markers.Length != 1)
            {
                problems.Add(
                    $"Multiple ownership markers were found for " +
                    $"{canonicalName}. Threat HUD Bridge will not " +
                    "modify any of them."
                );
                continue;
            }

            if (matchingVpks.Length == 0)
            {
                problems.Add(
                    $"The ownership marker for {canonicalName} exists, " +
                    "but the VPK is missing. Remove the stale marker " +
                    "before continuing."
                );
                continue;
            }

            if (matchingVpks.Length != 1)
            {
                problems.Add(
                    $"Multiple files match {canonicalName}. Threat HUD " +
                    "Bridge cannot safely determine which one is managed."
                );
                continue;
            }

            string recordedHash;
            try
            {
                recordedHash = ReadRecordedHashStrict(markers[0].Path);
            }
            catch (InvalidDataException error)
            {
                problems.Add(error.Message);
                continue;
            }

            var installedHash = CalculateFileHash(
                matchingVpks[0].Path,
                cancellationToken
            );
            if (!String.Equals(
                installedHash,
                recordedHash,
                StringComparison.OrdinalIgnoreCase
            ))
            {
                problems.Add(
                    $"The ownership marker for {canonicalName} does not " +
                    "match the VPK. Threat HUD Bridge will not overwrite " +
                    "or delete it."
                );
                continue;
            }

            embeddedHash ??= GetEmbeddedVpkHash();
            managedCandidates.Add(
                new ManagedVpkCandidate(
                    markerGroup.Key,
                    matchingVpks[0].Path,
                    markers[0].Path,
                    installedHash,
                    String.Equals(
                        installedHash,
                        embeddedHash,
                        StringComparison.OrdinalIgnoreCase
                    ),
                    true
                )
            );
        }

        // Bridge versions before ownership sidecars treated pak57_dir.vpk
        // as managed only when it was byte-for-byte identical to the
        // embedded payload. Preserve that narrow rule so an old install is
        // not duplicated, while every other markerless VPK remains foreign.
        if (!markerFiles.Any(file => file.Number == LegacyVpkNumber))
        {
            var legacyVpks = vpkFiles
                .Where(file => file.Number == LegacyVpkNumber)
                .ToArray();

            if (legacyVpks.Length == 1)
            {
                var legacyHash = CalculateFileHash(
                    legacyVpks[0].Path,
                    cancellationToken
                );
                embeddedHash ??= GetEmbeddedVpkHash();

                if (String.Equals(
                    legacyHash,
                    embeddedHash,
                    StringComparison.OrdinalIgnoreCase
                ))
                {
                    managedCandidates.Add(
                        new ManagedVpkCandidate(
                            LegacyVpkNumber,
                            legacyVpks[0].Path,
                            legacyVpks[0].Path + OwnershipMarkerSuffix,
                            legacyHash,
                            true,
                            false
                        )
                    );
                }
            }
        }

        if (managedCandidates.Count > 1)
        {
            problems.Add(
                "Multiple Threat HUD VPK installations were found (" +
                String.Join(
                    ", ",
                    managedCandidates
                        .OrderBy(candidate => candidate.Number)
                        .Select(candidate =>
                            FormatVpkFileName(candidate.Number))
                ) +
                "). Keep only one managed installation before continuing."
            );
        }

        var hasOwnershipConflict = problems.Count > 0;

        var nextInstallNumber = SelectNextVpkNumber(
            vpkFiles.Select(file => Path.GetFileName(file.Path)!)
        );

        if (
            managedCandidates.Count == 0 &&
            nextInstallNumber is null
        )
        {
            problems.Add(CreateNoAvailableVpkSlotMessage());
        }

        var distinctProblems = problems
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        return new VpkInventory(
            vpkFiles,
            managedCandidates,
            nextInstallNumber,
            hasOwnershipConflict,
            distinctProblems.Length == 0
                ? null
                : String.Join(Environment.NewLine, distinctProblems)
        );
    }

    internal static bool TryParseVpkFileName(
        string fileName,
        out int number
    )
    {
        var match = InstalledVpkFilePattern.Match(fileName);
        return TryParseVpkNumber(match, out number);
    }

    internal static int? SelectNextVpkNumber(
        IEnumerable<string> fileNames
    )
    {
        ArgumentNullException.ThrowIfNull(fileNames);
        var occupiedNumbers = new HashSet<int>();

        foreach (var fileName in fileNames)
        {
            if (TryParseVpkFileName(fileName, out var number))
            {
                occupiedNumbers.Add(number);
            }
        }

        if (occupiedNumbers.Count == 0)
        {
            return FirstVpkNumber;
        }

        var maximum = occupiedNumbers.Max();
        if (maximum < LastVpkNumber)
        {
            return maximum + 1;
        }

        // pak99 is already occupied, so use the highest remaining slot.
        // This preserves the highest possible load order and blocks only
        // when every supported number is genuinely occupied.
        for (
            var candidate = LastVpkNumber - 1;
            candidate >= FirstVpkNumber;
            candidate--
        )
        {
            if (!occupiedNumbers.Contains(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static bool TryParseOwnershipMarkerFileName(
        string fileName,
        out int number
    )
    {
        var match = OwnershipMarkerFilePattern.Match(fileName);
        return TryParseVpkNumber(match, out number);
    }

    private static bool TryParseVpkNumber(Match match, out int number)
    {
        number = 0;
        return match.Success &&
            Int32.TryParse(
                match.Groups["number"].Value,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out number
            ) &&
            number is >= FirstVpkNumber and <= LastVpkNumber;
    }

    private static string FormatVpkFileName(int number) =>
        "pak" +
        number.ToString("00", CultureInfo.InvariantCulture) +
        "_dir.vpk";

    private static string CreateNoAvailableVpkSlotMessage() =>
        "Cannot install the Threat HUD VPK because all supported VPK " +
        "slots (pak01_dir.vpk through pak99_dir.vpk) are occupied. " +
        "Remove an unused VPK from the Deadlock addons folder and try again.";

    private static string CombineBlockReasons(
        string? first,
        string second
    ) =>
        String.IsNullOrWhiteSpace(first)
            ? second
            : String.Equals(first, second, StringComparison.Ordinal)
                ? first
                : first + Environment.NewLine + second;

    private static void ThrowIfVpkBlocked(VpkInventory inventory)
    {
        if (!String.IsNullOrWhiteSpace(inventory.BlockReason))
        {
            throw new InvalidOperationException(inventory.BlockReason);
        }
    }

    private static bool IsSameManagedVpk(
        ManagedVpkCandidate left,
        ManagedVpkCandidate right
    ) =>
        IsSameManagedVpkPayload(left, right) &&
        left.HasOwnershipMarker == right.HasOwnershipMarker;

    private static bool IsSameManagedVpkPayload(
        ManagedVpkCandidate left,
        ManagedVpkCandidate right
    ) =>
        left.Number == right.Number &&
        String.Equals(
            left.VpkPath,
            right.VpkPath,
            StringComparison.OrdinalIgnoreCase
        ) &&
        String.Equals(
            left.MarkerPath,
            right.MarkerPath,
            StringComparison.OrdinalIgnoreCase
        ) &&
        String.Equals(
            left.InstalledHash,
            right.InstalledHash,
            StringComparison.OrdinalIgnoreCase
        );

    private void AdoptLegacyOwnershipMarkerCore(
        DeadlockPaths paths,
        ManagedVpkCandidate expectedMod,
        CancellationToken cancellationToken
    )
    {
        if (expectedMod.HasOwnershipMarker)
        {
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();
        EnsureDeadlockIsStopped();

        // Re-scan immediately before writing. A marker that appeared and is
        // valid is accepted; every invalid or unrelated change is blocked.
        var inventory = InspectVpkInventory(paths, cancellationToken);
        ThrowIfVpkBlocked(inventory);
        if (inventory.ManagedCandidates.Count != 1)
        {
            throw new InvalidOperationException(
                "The legacy Threat HUD VPK changed while its ownership " +
                "marker was being prepared. No files were changed."
            );
        }

        var currentMod = inventory.ManagedCandidates[0];
        if (!IsSameManagedVpkPayload(expectedMod, currentMod))
        {
            throw new InvalidOperationException(
                "The legacy Threat HUD VPK changed while its ownership " +
                "marker was being prepared. No files were changed."
            );
        }

        if (currentMod.HasOwnershipMarker)
        {
            return;
        }

        var embeddedHash = GetEmbeddedVpkHash();

        // ThreatHudBridge is Windows-only. Holding the VPK without sharing
        // closes the hash-to-marker race while the CreateNew sidecar is
        // committed, without renaming or overwriting the legacy payload.
        using var lockedVpk = new FileStream(
            currentMod.VpkPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.None,
            128 * 1024,
            FileOptions.SequentialScan
        );
        var lockedHash = CalculateStreamHash(
            lockedVpk,
            cancellationToken
        );
        if (
            !String.Equals(
                lockedHash,
                currentMod.InstalledHash,
                StringComparison.OrdinalIgnoreCase
            ) ||
            !String.Equals(
                lockedHash,
                embeddedHash,
                StringComparison.OrdinalIgnoreCase
            )
        )
        {
            throw new InvalidOperationException(
                "The legacy pak57_dir.vpk changed while its ownership " +
                "marker was being prepared. No files were changed."
            );
        }

        // Do not observe cancellation after this point: the marker write is
        // the commit. File.Move(overwrite: false) prevents adopting over a
        // marker created by another process.
        WriteNewTextAtomically(
            currentMod.MarkerPath,
            embeddedHash + Environment.NewLine
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

    private static string ReadRecordedHashStrict(string hashPath)
    {
        if (!File.Exists(hashPath))
        {
            throw new FileNotFoundException(
                "The Threat HUD ownership marker is missing.",
                hashPath
            );
        }

        var value = File.ReadAllText(hashPath, Encoding.ASCII).Trim();
        if (value.Length != 64 || !value.All(Uri.IsHexDigit))
        {
            throw new InvalidDataException(
                "The ownership marker " + Path.GetFileName(hashPath) +
                " is invalid. It must contain exactly one SHA-256 hash."
            );
        }

        return value;
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
        return CalculateStreamHash(stream, cancellationToken);
    }

    private static string CalculateStreamHash(
        Stream stream,
        CancellationToken cancellationToken
    )
    {
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
            addonsDirectory
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

    private static void WriteNewTextAtomically(string path, string value)
    {
        var temporaryPath = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            WriteBytesToDisk(
                temporaryPath,
                new UTF8Encoding(false).GetBytes(value)
            );
            File.Move(temporaryPath, path, overwrite: false);
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
        string AddonsDirectory
    );

    private sealed record NamedVpkFile(
        int Number,
        string Path
    );

    private sealed record NamedMarkerFile(
        int Number,
        string Path
    );

    private sealed record ManagedVpkCandidate(
        int Number,
        string VpkPath,
        string MarkerPath,
        string InstalledHash,
        bool IsCurrentPayload,
        bool HasOwnershipMarker
    );

    private sealed record VpkInventory(
        IReadOnlyList<NamedVpkFile> VpkFiles,
        IReadOnlyList<ManagedVpkCandidate> ManagedCandidates,
        int? NextInstallNumber,
        bool HasOwnershipConflict,
        string? BlockReason
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
