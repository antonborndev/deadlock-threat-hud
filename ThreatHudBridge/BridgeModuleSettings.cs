using System.Text.Json;

internal readonly record struct BridgeModuleSettings
{
    public const byte WinrateFlag =
        1 << 0;

    public const byte RankFlag =
        1 << 1;

    public const byte AdviserFlag =
        1 << 2;

    public const byte HeroDamageFlag =
        1 << 3;

    public const byte AllMask =
        WinrateFlag |
        RankFlag |
        AdviserFlag |
        HeroDamageFlag;

    public static BridgeModuleSettings All =>
        new(AllMask);

    public BridgeModuleSettings(
        byte enabledMask
    )
    {
        if (
            (enabledMask & ~AllMask) !=
                0
        )
        {
            throw new ArgumentOutOfRangeException(
                nameof(enabledMask),
                enabledMask,
                "Module settings contain unknown bits."
            );
        }

        EnabledMask =
            enabledMask;
    }

    public byte EnabledMask
    {
        get;
    }

    public bool IsEnabled(
        BridgeServiceKind service
    )
    {
        return (
            EnabledMask &
            GetFlag(
                service
            )
        ) != 0;
    }

    public BridgeModuleSettings WithEnabled(
        BridgeServiceKind service,
        bool enabled
    )
    {
        var flag =
            GetFlag(
                service
            );

        var nextMask =
            enabled
                ? EnabledMask | flag
                : EnabledMask & ~flag;

        return new BridgeModuleSettings(
            (byte)nextMask
        );
    }

    private static byte GetFlag(
        BridgeServiceKind service
    )
    {
        return service switch
        {
            BridgeServiceKind.Winrate =>
                WinrateFlag,

            BridgeServiceKind.Rank =>
                RankFlag,

            BridgeServiceKind.Adviser =>
                AdviserFlag,

            BridgeServiceKind.HeroDamage =>
                HeroDamageFlag,

            _ =>
                throw new ArgumentOutOfRangeException(
                    nameof(service),
                    service,
                    "Unknown Bridge module."
                )
        };
    }
}

internal static class BridgeModuleSettingsPersistence
{
    private const string RuntimeDirectoryName =
        "DeadlockThreatHud";

    private const string SettingsFileName =
        "module-settings.json";

    private static readonly object Gate =
        new();

    private static readonly JsonSerializerOptions
        JsonOptions =
            new(JsonSerializerDefaults.Web)
            {
                WriteIndented =
                    true
            };

    private static BridgeModuleSettings
        _lastKnownSettings =
            BridgeModuleSettings.All;

    private static bool _hasLastKnownSettings;

    public static BridgeModuleSettings Load()
    {
        lock (Gate)
        {
            try
            {
                var path =
                    GetSettingsPath();

                using var stream =
                    new FileStream(
                        path,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.ReadWrite |
                        FileShare.Delete
                    );

                using var document =
                    JsonDocument.Parse(
                        stream
                    );

                if (
                    document.RootElement.ValueKind !=
                        JsonValueKind.Object ||
                    !document.RootElement
                        .TryGetProperty(
                            "enabledMask",
                            out var maskElement
                        ) ||
                    !maskElement.TryGetByte(
                        out var enabledMask
                    ) ||
                    (enabledMask &
                        ~BridgeModuleSettings.AllMask) !=
                            0
                )
                {
                    return GetLastKnownSettings();
                }

                _lastKnownSettings =
                    new BridgeModuleSettings(
                        enabledMask
                    );

                _hasLastKnownSettings =
                    true;

                return _lastKnownSettings;
            }
            catch (
                IOException
            )
            {
                return GetLastKnownSettings();
            }
            catch (
                UnauthorizedAccessException
            )
            {
                return GetLastKnownSettings();
            }
            catch (
                JsonException
            )
            {
                return GetLastKnownSettings();
            }
        }
    }

    public static void Save(
        BridgeModuleSettings settings
    )
    {
        lock (Gate)
        {
            var path =
                GetSettingsPath();

            var directory =
                Path.GetDirectoryName(
                    path
                ) ??
                throw new InvalidOperationException(
                    "Module settings directory is unavailable."
                );

            Directory.CreateDirectory(
                directory
            );

            var temporaryPath =
                Path.Combine(
                    directory,

                    SettingsFileName +
                    "." +
                    Environment.ProcessId +
                    "." +
                    Guid.NewGuid()
                        .ToString("N") +
                    ".tmp"
                );

            try
            {
                using (
                    var stream =
                        new FileStream(
                            temporaryPath,
                            FileMode.CreateNew,
                            FileAccess.Write,
                            FileShare.None
                        )
                )
                {
                    JsonSerializer.Serialize(
                        stream,
                        new StoredSettings(
                            settings.EnabledMask
                        ),
                        JsonOptions
                    );

                    stream.Flush(
                        flushToDisk:
                            true
                    );
                }

                File.Move(
                    temporaryPath,
                    path,
                    overwrite:
                        true
                );

                _lastKnownSettings =
                    settings;

                _hasLastKnownSettings =
                    true;
            }
            finally
            {
                try
                {
                    File.Delete(
                        temporaryPath
                    );
                }
                catch
                {
                    // A failed cleanup must not hide the original save error.
                }
            }
        }
    }

    private static BridgeModuleSettings
        GetLastKnownSettings()
    {
        if (!_hasLastKnownSettings)
        {
            _lastKnownSettings =
                BridgeModuleSettings.All;

            _hasLastKnownSettings =
                true;
        }

        return _lastKnownSettings;
    }

    private static string GetSettingsPath()
    {
        return Path.Combine(
            Environment.GetFolderPath(
                Environment.SpecialFolder
                    .LocalApplicationData
            ),
            RuntimeDirectoryName,
            SettingsFileName
        );
    }

    private sealed record StoredSettings(
        byte EnabledMask
    );
}

internal static class BridgeModuleSettingsTransport
{
    public const string Channel =
        "module-settings";

    public static byte[] BuildPacket(
        BridgeModuleSettings settings
    )
    {
        return BridgeProtocol.CreatePacket(
            BridgeMessageType.ModuleSettings,
            new byte[]
            {
                settings.EnabledMask
            }
        );
    }
}
