using System.Buffers.Binary;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.Text.Json;
using System.Windows.Forms;

internal enum MatchPlayerDetailsUiState
{
    Waiting,
    Loading,
    Ready,
    Error
}

internal sealed class MainForm : Form
{
    private const ulong SteamId64IndividualBase =
        76561197960265728UL;

    private const string CurrentMatchContextDiagnosticsAddress =
        "http://127.0.0.1:28741/current-match-context-diagnostics";

    private const string HeroDamageModuleStateChangedAddress =
        "http://127.0.0.1:28741/hero-damage-module-state-changed";

    private const int CurrentMatchDamagePollIntervalMilliseconds =
        1_000;

    private const int CurrentMatchLaneCount =
        3;

    private const int CurrentMatchLaneStatsColumnWidth =
        94;

    private const int CompactMatchWindowHeight =
        560;

    private const int StandardContentWindowHeight =
        650;

    private const int MatchGridHeightSafetyMargin =
        2;

    private const int HeroIconPixels =
        48;

    private const string RankImageAddress =
        "http://127.0.0.1:28741/rank-image.png";

    private const string RankRefreshAddress =
        "http://127.0.0.1:28741/match-player-ranks-refresh";

    private const int RankIconPixels =
        38;

    private const int MatchStatsHeaderIconPixels =
        16;

    private const int MaximumHeroIconBytes =
        2 * 1024 * 1024;

    private const int MaximumHeroIconSourceDimension =
        2_048;

    private const int MaximumHeroIconCacheEntries =
        96;

    private const int MaximumRankIconBytes =
        1024 * 1024;

    private const int MaximumRankIconSourceDimension =
        512;

    private const int MaximumRankIconCacheEntries =
        66;

    private const decimal CurrentMatchExceptionalDamageMultiplier =
        1.20M;

    private static readonly Color CurrentMatchTopDamageColor =
        Color.LightGreen;

    private static readonly Color CurrentMatchLowestDamageColor =
        Color.LightCoral;

    private static readonly Color CurrentMatchExceptionalDamageColor =
        Color.FromArgb(
            204,
            128,
            255
        );

    private static readonly JsonSerializerOptions
        CurrentMatchDamageJsonOptions =
            new()
            {
                PropertyNameCaseInsensitive =
                    true
            };

    private readonly Label _runtimeStateValue;
    private readonly Label _runtimeDetailValue;

    private readonly Label _deadlockValue;
    private readonly Label _steamValue;
    private readonly Label _httpValue;
    private readonly Label _accountIdValue;

    private readonly BridgeServiceStatusControl
        _serviceStatusControl;

    private BridgeModuleSettings
        _persistedModuleSettings;

    private readonly Button _installModButton;
    private readonly Button _activateModButton;
    private readonly Label _modInstallBlockLabel;

    private readonly ToolTip _modToolTip =
        new();

    private readonly ThreatHudModManagerService
        _modManager =
            new();

    private readonly CancellationTokenSource
        _modManagementCancellation =
            new();

    private ThreatHudModStatus? _modStatus;
    private string? _modStatusError;
    private int _modStatusGeneration;
    private bool _modStatusInspectionRunning =
        true;
    private bool _modOperationRunning;
    private bool _deadlockRunning;

    /*
     * The supervisor polls health frequently. Remember requested values before
     * posting to WinForms so identical snapshots do not create an unbounded
     * stream of BeginInvoke delegates and native control updates.
     */
    private readonly object _requestedUiStateGate =
        new();

    private BridgeRuntimeState? _requestedRuntimeState;
    private string _requestedRuntimeDetail =
        String.Empty;

    private bool? _requestedDeadlockRunning;
    private bool? _requestedSteamInitialized;
    private bool? _requestedHttpRunning;
    private string _requestedHttpAddress =
        String.Empty;

    private bool _requestedAccountIdInitialized;
    private uint? _requestedAccountId;

    private bool? _requestedHasCurrentMatch;

    private MatchPlayerDetailsUiState?
        _requestedMatchPlayerDetailsState;

    private string _requestedMatchPlayerDetailsDetail =
        String.Empty;

    private readonly Label _matchStatusValue;
    private readonly CurrentMatchLaneStatsPanel
        _alliesLaneStatsPanel;
    private readonly DataGridView _alliesMatchGrid;
    private readonly DataGridView _enemiesMatchGrid;

    private readonly Panel _contentFrame;
    private readonly Label _noActiveMatchLabel;
    private readonly Control _matchPanel;
    private readonly PlayerReactionListControl
        _reactionList;

    private readonly MatchHistoryStore
        _matchHistoryStore =
            new();

    private readonly MatchHistoryListControl
        _matchHistoryList;

    private MainSection _selectedSection =
        MainSection.Match;

    private bool _hasCurrentMatch;
    private bool _matchContentHeightAdjusted;
    private int _windowContentLayoutGeneration;

    private FormWindowState _lastObservedWindowState =
        FormWindowState.Normal;

    private readonly HttpClient _currentMatchDamageHttpClient =
        new()
        {
            Timeout =
                TimeSpan.FromSeconds(2)
        };

    private readonly System.Windows.Forms.Timer
        _currentMatchDamageTimer =
            new()
            {
                Interval =
                    CurrentMatchDamagePollIntervalMilliseconds
            };

    private readonly CancellationTokenSource
        _currentMatchDamageCancellation =
            new();

    private int _currentMatchDamagePollRunning;

    private readonly Dictionary<
        uint,
        CurrentMatchDamageValue
    > _currentMatchDamageByAccountId =
        new();

    private readonly HttpClient _heroImageHttpClient =
        new()
        {
            Timeout =
                TimeSpan.FromSeconds(10)
        };

    private readonly Dictionary<string, Image>
        _heroIconCache =
            new(
                StringComparer.OrdinalIgnoreCase
            );

    private readonly HttpClient _rankImageHttpClient =
        new()
        {
            Timeout =
                TimeSpan.FromSeconds(10)
        };

    private readonly Dictionary<int, Image>
        _rankIconCache =
            new();

    private readonly Image _rankUnavailableIcon =
        CreateRankUnavailableIcon();

    private int _matchIconGeneration;
    private int _matchRankIconGeneration;
    private int _rankRefreshRequestGeneration;

    private readonly RichTextBox _logBox;

    private bool _allowClose;

    public event EventHandler? ShutdownRequested;

    public MainForm()
    {
        _persistedModuleSettings =
            BridgeModuleSettingsPersistence
                .Load();

        Text = "Threat HUD Bridge";

        StartPosition =
            FormStartPosition.CenterScreen;

        MinimumSize =
            new Size(
                940,
                560
            );

        Size =
            new Size(
                1120,
                650
            );

        BackColor =
            BridgeUiTheme.Window;

        ForeColor =
            BridgeUiTheme.Text;

        Font =
            new Font(
                "Segoe UI",
                9F,
                FontStyle.Regular,
                GraphicsUnit.Point
            );

        var root =
            new TableLayoutPanel
            {
                Dock =
                    DockStyle.Fill,

                ColumnCount =
                    1,

                RowCount =
                    4,

                Padding =
                    new Padding(
                        18
                    ),

                BackColor =
                    BackColor
            };

        root.RowStyles.Add(
            new RowStyle(
                SizeType.AutoSize
            )
        );

        root.RowStyles.Add(
            new RowStyle(
                SizeType.AutoSize
            )
        );

        root.RowStyles.Add(
            new RowStyle(
                SizeType.Percent,
                100F
            )
        );

        root.RowStyles.Add(
            new RowStyle(
                SizeType.AutoSize
            )
        );

        Controls.Add(
            root
        );

        var header =
            CreateHeaderPanel(
                out _runtimeStateValue,
                out _runtimeDetailValue
            );

        root.Controls.Add(
            header,
            0,
            0
        );

        var statusPanel =
            CreateStatusPanel(
                out _deadlockValue,
                out _steamValue,
                out _httpValue,
                out _accountIdValue,
                _persistedModuleSettings,
                out _serviceStatusControl,
                out _installModButton,
                out _activateModButton,
                out _modInstallBlockLabel
            );

        root.Controls.Add(
            statusPanel,
            0,
            1
        );

        /*
         * The standard WinForms TabControl is
         * intentionally not used here.
         *
         * Its border and tabs are partially drawn by
         * the Windows theme and do not fit well
         * into a fully dark interface.
         */
        var contentRoot =
            new TableLayoutPanel
            {
                Dock =
                    DockStyle.Fill,

                ColumnCount =
                    1,

                RowCount =
                    2,

                Margin =
                    new Padding(
                        0
                    ),

                BackColor =
                    BackColor
            };

        contentRoot.RowStyles.Add(
            new RowStyle(
                SizeType.AutoSize
            )
        );

        contentRoot.RowStyles.Add(
            new RowStyle(
                SizeType.Percent,
                100F
            )
        );

        var navigation =
            new FlowLayoutPanel
            {
                Dock =
                    DockStyle.Fill,

                AutoSize =
                    true,

                FlowDirection =
                    FlowDirection.LeftToRight,

                WrapContents =
                    false,

                BackColor =
                    BackColor,

                Margin =
                    new Padding(
                        0,
                        0,
                        0,
                        6
                    )
            };

        var matchButton =
            BridgeUiTheme.CreateButton(
                "Match",
                80
            );

        var reactionsButton =
            BridgeUiTheme.CreateButton(
                "Reactions",
                100
            );

        reactionsButton.Margin =
            new Padding(
                6,
                0,
                0,
                0
            );

        var historyButton =
            BridgeUiTheme.CreateButton(
                "History",
                90
            );

        historyButton.Margin =
            new Padding(
                6,
                0,
                0,
                0
            );

        var logButton =
            BridgeUiTheme.CreateButton(
                "Log",
                80
            );

        logButton.Margin =
            new Padding(
                6,
                0,
                0,
                0
            );

        navigation.Controls.Add(
            matchButton
        );

        navigation.Controls.Add(
            historyButton
        );

        navigation.Controls.Add(
            reactionsButton
        );

        navigation.Controls.Add(
            logButton
        );

        contentRoot.Controls.Add(
            navigation,
            0,
            0
        );

        /*
         * The outer panel is used only
         * as a thin dark border.
         */
        _contentFrame =
            new Panel
            {
                Dock =
                    DockStyle.Fill,

                BackColor =
                    BridgeUiTheme.Border,

                Padding =
                    new Padding(
                        1
                    ),

                Margin =
                    new Padding(
                        0
                    )
            };

        var contentHost =
            new Panel
            {
                Dock =
                    DockStyle.Fill,

                BackColor =
                    BridgeUiTheme.Surface
            };

        _contentFrame.Controls.Add(
            contentHost
        );

        contentRoot.Controls.Add(
            _contentFrame,
            0,
            1
        );

        _noActiveMatchLabel =
            new Label
            {
                Dock =
                    DockStyle.Fill,

                Text =
                    "Waiting for an active match...",

                TextAlign =
                    ContentAlignment.MiddleCenter,

                Font =
                    new Font(
                        "Segoe UI",
                        11F,
                        FontStyle.Regular,
                        GraphicsUnit.Point
                    ),

                ForeColor =
                    BridgeUiTheme.TextMuted,

                BackColor =
                    BridgeUiTheme.Surface,

                Visible =
                    false
            };

        _matchPanel =
            CreateMatchPanel(
                out _matchStatusValue,
                out _alliesLaneStatsPanel,
                out _alliesMatchGrid,
                out _enemiesMatchGrid
            );

        _alliesMatchGrid.CellContentClick +=
            OnMatchGridCellContentClick;

        _enemiesMatchGrid.CellContentClick +=
            OnMatchGridCellContentClick;

        _logBox =
            CreateLogBox();

        _logBox.Visible =
            false;

        _reactionList =
            new PlayerReactionListControl
            {
                Dock =
                    DockStyle.Fill,

                Visible =
                    false
            };

        _matchHistoryList =
            new MatchHistoryListControl(
                _matchHistoryStore
            )
            {
                Dock =
                    DockStyle.Fill,

                Visible =
                    false
            };

        contentHost.Controls.Add(
            _matchPanel
        );

        contentHost.Controls.Add(
            _reactionList
        );

        contentHost.Controls.Add(
            _matchHistoryList
        );

        contentHost.Controls.Add(
            _logBox
        );

        contentHost.Controls.Add(
            _noActiveMatchLabel
        );

        void SelectSection(
            MainSection section
        )
        {
            var previousSection =
                _selectedSection;

            _selectedSection =
                section;

            if (
                section ==
                    MainSection.Match &&
                previousSection !=
                    MainSection.Match
            )
            {
                _matchContentHeightAdjusted =
                    false;
            }

            var matchSelected =
                section ==
                    MainSection.Match;

            var reactionsSelected =
                section ==
                    MainSection.Reactions;

            var historySelected =
                section ==
                    MainSection.History;

            var logSelected =
                section ==
                    MainSection.Log;

            ApplySelectedSectionVisibility();

            BridgeUiTheme.SetNavigationSelected(
                matchButton,
                matchSelected
            );

            BridgeUiTheme.SetNavigationSelected(
                reactionsButton,
                reactionsSelected
            );

            BridgeUiTheme.SetNavigationSelected(
                historyButton,
                historySelected
            );

            BridgeUiTheme.SetNavigationSelected(
                logButton,
                logSelected
            );

            ScheduleWindowHeightForCurrentContent();
        }

        matchButton.Click +=
            (_, _) =>
            {
                SelectSection(
                    MainSection.Match
                );
            };

        reactionsButton.Click +=
            async (_, _) =>
            {
                SelectSection(
                    MainSection.Reactions
                );

                await _reactionList.RefreshAsync();
            };

        historyButton.Click +=
            async (_, _) =>
            {
                SelectSection(
                    MainSection.History
                );

                await _matchHistoryList.RefreshAsync();
            };

        logButton.Click +=
            (_, _) =>
            {
                SelectSection(
                    MainSection.Log
                );
            };

        SelectSection(
            MainSection.Match
        );

        root.Controls.Add(
            contentRoot,
            0,
            2
        );

        var footer =
            CreateFooter();

        root.Controls.Add(
            footer,
            0,
            3
        );

        SetRuntimeState(
            BridgeRuntimeState.WaitingForDeadlock,
            "Waiting for Deadlock to start"
        );

        SetDeadlockRunning(
            false
        );

        SetSteamInitialized(
            false
        );

        SetHttpServerRunning(
            false
        );

        SetAccountId(
            null
        );

        SetServiceStates(
            null
        );

        SetMatchPlayerDetails(
            MatchPlayerDetailsUiState.Waiting
        );

        _installModButton.Click +=
            OnInstallModButtonClick;

        _activateModButton.Click +=
            OnActivateModButtonClick;

        _serviceStatusControl.SettingsChanged +=
            OnModuleSettingsChanged;

        Shown +=
            OnModManagementShown;

        UpdateModButtons();

        _currentMatchDamageTimer.Tick +=
            OnCurrentMatchDamageTimerTick;
    }

    public void SetRuntimeState(
        BridgeRuntimeState state,
        string? detail = null
    )
    {
        var normalizedDetail =
            String.IsNullOrWhiteSpace(
                detail
            )
                ? String.Empty
                : detail;

        lock (_requestedUiStateGate)
        {
            if (
                _requestedRuntimeState ==
                    state &&
                String.Equals(
                    _requestedRuntimeDetail,
                    normalizedDetail,
                    StringComparison.Ordinal
                )
            )
            {
                return;
            }

            _requestedRuntimeState =
                state;

            _requestedRuntimeDetail =
                normalizedDetail;
        }

        RunOnUiThread(
            () =>
            {
                _runtimeStateValue.Text =
                    GetStateText(
                        state
                    );

                _runtimeStateValue.ForeColor =
                    GetStateColor(
                        state
                    );

                _runtimeDetailValue.Text =
                    normalizedDetail;
            }
        );
    }

    public void SetDeadlockRunning(
        bool running
    )
    {
        lock (_requestedUiStateGate)
        {
            if (
                _requestedDeadlockRunning ==
                    running
            )
            {
                return;
            }

            _requestedDeadlockRunning =
                running;
        }

        RunOnUiThread(
            () =>
            {
                var wasRunning =
                    _deadlockRunning;

                _deadlockRunning =
                    running;

                _deadlockValue.Text =
                    running
                        ? "Running"
                        : "Not running";

                _deadlockValue.ForeColor =
                    running
                        ? Color.LightGreen
                        : Color.Gainsboro;

                UpdateModButtons();

                if (
                    wasRunning &&
                    !running
                )
                {
                    _ =
                        RefreshModStatusAsync();
                }
            }
        );
    }

    private async void OnModManagementShown(
        object? sender,
        EventArgs e
    )
    {
        ScheduleWindowHeightForCurrentContent();

        await RefreshModStatusAsync(
            logFailure:
                true
        );
    }

    private async void OnInstallModButtonClick(
        object? sender,
        EventArgs e
    )
    {
        var status =
            _modStatus;

        if (
            status is null ||
            GetModFileActionBlockReason(
                status
            ) is not null
        )
        {
            return;
        }

        if (status.IsUpdateAvailable)
        {
            await ExecuteModOperationAsync(
                "update",
                cancellationToken =>
                    _modManager.UpdateAsync(
                        cancellationToken
                    ),
                "Threat HUD mod was updated."
            );

            return;
        }

        if (
            status.IsInstalled &&
            MessageBox.Show(
                this,
                "Remove the Threat HUD VPK from Deadlock?" +
                Environment.NewLine +
                Environment.NewLine +
                "Addon loading will remain activated until you " +
                "press Deactivate mod.",
                "Uninstall Threat HUD mod",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2
            ) != DialogResult.Yes
        )
        {
            return;
        }

        if (status.IsInstalled)
        {
            await ExecuteModOperationAsync(
                "uninstall",
                cancellationToken =>
                    _modManager.UninstallAsync(
                        cancellationToken
                    ),
                "Threat HUD mod was uninstalled."
            );
        }
        else
        {
            await ExecuteModOperationAsync(
                "install",
                cancellationToken =>
                    _modManager.InstallAsync(
                        cancellationToken
                    ),
                "Threat HUD mod was installed."
            );
        }
    }

    private async void OnActivateModButtonClick(
        object? sender,
        EventArgs e
    )
    {
        var status =
            _modStatus;

        if (
            status is null ||
            GetModActivationBlockReason(
                status
            ) is not null
        )
        {
            return;
        }

        if (
            status.IsActive &&
            MessageBox.Show(
                this,
                "Disable Deadlock addon loading?" +
                Environment.NewLine +
                Environment.NewLine +
                "This affects every VPK mod that uses the " +
                "citadel/addons search path.",
                "Deactivate Deadlock mods",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2
            ) != DialogResult.Yes
        )
        {
            return;
        }

        var activate =
            !status.IsActive;

        await ExecuteModOperationAsync(
            activate
                ? "activate"
                : "deactivate",
            cancellationToken =>
                _modManager.SetActiveAsync(
                    activate,
                    cancellationToken
                ),
            activate
                ? "Deadlock mod loading was activated."
                : "Deadlock mod loading was deactivated."
        );
    }

    private async Task ExecuteModOperationAsync(
        string operation,
        Func<CancellationToken, Task> action,
        string successMessage
    )
    {
        if (
            _modOperationRunning ||
            _deadlockRunning
        )
        {
            return;
        }

        _modOperationRunning =
            true;

        UpdateModButtons();

        try
        {
            await action(
                _modManagementCancellation.Token
            );

            AppendLog(
                successMessage
            );
        }
        catch (
            OperationCanceledException
        ) when (
            _modManagementCancellation
                .IsCancellationRequested
        )
        {
        }
        catch (Exception error)
        {
            AppendLog(
                $"Mod {operation} failed: " +
                error.Message
            );

            if (
                !IsDisposed &&
                !Disposing
            )
            {
                MessageBox.Show(
                    this,
                    error.Message,
                    "Threat HUD mod",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error
                );
            }
        }
        finally
        {
            await RefreshModStatusAsync();

            _modOperationRunning =
                false;

            UpdateModButtons();
        }
    }

    private async Task RefreshModStatusAsync(
        bool logFailure = false
    )
    {
        var generation =
            ++_modStatusGeneration;

        _modStatusInspectionRunning =
            true;

        UpdateModButtons();

        try
        {
            var status =
                await _modManager.GetStatusAsync(
                    _modManagementCancellation.Token
                );

            if (
                generation !=
                    _modStatusGeneration ||
                IsDisposed ||
                Disposing
            )
            {
                return;
            }

            _modStatus =
                status;

            _modStatusError =
                null;

            _modStatusInspectionRunning =
                false;

            UpdateModButtons();

            if (
                logFailure &&
                !status.IsDeadlockLocated
            )
            {
                AppendLog(
                    "Mod management is unavailable: Deadlock " +
                    "was not found in the configured Steam libraries."
                );
            }
        }
        catch (
            OperationCanceledException
        ) when (
            _modManagementCancellation
                .IsCancellationRequested
        )
        {
        }
        catch (Exception error)
        {
            if (
                generation !=
                    _modStatusGeneration ||
                IsDisposed ||
                Disposing
            )
            {
                return;
            }

            _modStatus =
                null;

            _modStatusError =
                error.Message;

            _modStatusInspectionRunning =
                false;

            UpdateModButtons();

            if (logFailure)
            {
                AppendLog(
                    "Could not inspect the Deadlock mod state: " +
                    error.Message
                );
            }
        }
    }

    private void UpdateModButtons()
    {
        if (
            IsDisposed ||
            Disposing
        )
        {
            return;
        }

        var status =
            _modStatus;

        _installModButton.Text =
            status?.IsInstalled == true
                ? status.IsCurrentPayload
                    ? "Uninstall mod"
                    : "Update mod"
                : "Install mod";

        _activateModButton.Text =
            status?.IsActive == true
                ? "Deactivate mod"
                : "Activate mod";

        var installBlockReason =
            GetModFileActionBlockReason(
                status
            );

        _installModButton.Enabled =
            installBlockReason is null;

        var blockLabelVisible =
            !_installModButton.Enabled;

        var blockLabelText =
            blockLabelVisible
                ? installBlockReason ??
                  "Mod files cannot be changed right now."
                : String.Empty;

        var blockLabelChanged =
            _modInstallBlockLabel.Visible !=
                blockLabelVisible ||
            !String.Equals(
                _modInstallBlockLabel.Text,
                blockLabelText,
                StringComparison.Ordinal
            );

        _modInstallBlockLabel.Text =
            blockLabelText;

        _modInstallBlockLabel.Visible =
            blockLabelVisible;

        if (blockLabelChanged)
        {
            _matchContentHeightAdjusted =
                false;

            ScheduleWindowHeightForCurrentContent();
        }

        var activationBlockReason =
            GetModActivationBlockReason(
                status
            );

        _activateModButton.Enabled =
            activationBlockReason is null;

        var installHint =
            installBlockReason ??
            (
                status!.IsInstalled
                    ? status.IsCurrentPayload
                        ? "Remove the Threat HUD VPK from Deadlock."
                        : "Replace the installed Threat HUD VPK with " +
                          "the version embedded in " +
                          "ThreatHudBridge.exe. Mod activation will " +
                          "remain unchanged."
                    : "Install the VPK embedded in ThreatHudBridge.exe."
            );

        var activateHint =
            activationBlockReason ??
            (
                status!.IsActive
                    ? "Disable the citadel/addons search path."
                    : "Enable the citadel/addons search path."
            );

        _modToolTip.SetToolTip(
            _installModButton,
            installHint
        );

        _modToolTip.SetToolTip(
            _activateModButton,
            activateHint
        );
    }

    private string? GetModFileActionBlockReason(
        ThreatHudModStatus? status
    )
    {
        var statusInspectionError =
            _modStatusError;

        if (_modOperationRunning)
        {
            return "A mod operation is in progress.";
        }

        if (_deadlockRunning)
        {
            return "Close Deadlock before changing mod files.";
        }

        if (_modStatusInspectionRunning)
        {
            return "Checking the Deadlock mod status...";
        }

        if (
            !String.IsNullOrWhiteSpace(
                statusInspectionError
            )
        )
        {
            return statusInspectionError.Trim();
        }

        if (
            status?.IsDeadlockLocated != true
        )
        {
            return "Deadlock was not found in the configured Steam libraries.";
        }

        var vpkBlockReason =
            status.VpkBlockReason;

        if (
            !String.IsNullOrWhiteSpace(
                vpkBlockReason
            )
        )
        {
            return vpkBlockReason.Trim();
        }

        var vpkError =
            status.VpkError;

        if (
            !String.IsNullOrWhiteSpace(
                vpkError
            )
        )
        {
            return vpkError.Trim();
        }

        if (status.HasVpkConflict)
        {
            return "The installed VPK ownership could not be verified " +
                   "safely. Threat HUD Bridge will not change mod files.";
        }

        return null;
    }

    private string? GetModActivationBlockReason(
        ThreatHudModStatus? status
    )
    {
        var statusInspectionError =
            _modStatusError;

        if (_modOperationRunning)
        {
            return "A mod operation is in progress.";
        }

        if (_deadlockRunning)
        {
            return "Close Deadlock before changing mod settings.";
        }

        if (_modStatusInspectionRunning)
        {
            return "Checking the Deadlock mod status...";
        }

        if (
            !String.IsNullOrWhiteSpace(
                statusInspectionError
            )
        )
        {
            return statusInspectionError.Trim();
        }

        if (
            status?.IsDeadlockLocated != true
        )
        {
            return "Deadlock was not found in the configured Steam libraries.";
        }

        var activationError =
            status.ActivationError;

        if (
            !String.IsNullOrWhiteSpace(
                activationError
            )
        )
        {
            return activationError.Trim();
        }

        if (
            !status.IsInstalled &&
            !status.IsActive
        )
        {
            return "Install the Threat HUD VPK before activation.";
        }

        return null;
    }

    public void SetSteamInitialized(
        bool initialized
    )
    {
        lock (_requestedUiStateGate)
        {
            if (
                _requestedSteamInitialized ==
                    initialized
            )
            {
                return;
            }

            _requestedSteamInitialized =
                initialized;
        }

        RunOnUiThread(
            () =>
            {
                _steamValue.Text =
                    initialized
                        ? "Initialized"
                        : "Not initialized";

                _steamValue.ForeColor =
                    initialized
                        ? Color.LightGreen
                        : Color.Gainsboro;
            }
        );
    }

    public void SetHttpServerRunning(
        bool running,
        string address =
            "http://127.0.0.1:28741"
    )
    {
        var normalizedAddress =
            running
                ? address
                : String.Empty;

        lock (_requestedUiStateGate)
        {
            if (
                _requestedHttpRunning ==
                    running &&
                String.Equals(
                    _requestedHttpAddress,
                    normalizedAddress,
                    StringComparison.Ordinal
                )
            )
            {
                return;
            }

            _requestedHttpRunning =
                running;

            _requestedHttpAddress =
                normalizedAddress;
        }

        RunOnUiThread(
            () =>
            {
                _httpValue.Text =
                    running
                        ? normalizedAddress
                        : "Stopped";

                _httpValue.ForeColor =
                    running
                        ? Color.LightGreen
                        : Color.Gainsboro;
            }
        );
    }

    public void SetAccountId(
        uint? accountId
    )
    {
        lock (_requestedUiStateGate)
        {
            if (
                _requestedAccountIdInitialized &&
                _requestedAccountId ==
                    accountId
            )
            {
                return;
            }

            _requestedAccountIdInitialized =
                true;

            _requestedAccountId =
                accountId;
        }

        RunOnUiThread(
            () =>
            {
                _accountIdValue.Text =
                    accountId.HasValue
                        ? accountId.Value
                            .ToString()
                        : "—";

                foreach (
                    var grid in
                        new[]
                        {
                            _alliesMatchGrid,
                            _enemiesMatchGrid
                        }
                )
                {
                    foreach (
                        DataGridViewRow row in
                            grid.Rows
                    )
                    {
                        if (
                            row.Tag is
                                DeadlockMatchPlayerDetailsEntry player
                        )
                        {
                            ApplyLocalPlayerRowStyle(
                                row,
                                player.AccountId,
                                accountId
                            );
                        }
                    }
                }
            }
        );
    }

    public void SetServiceStates(
        BridgeServiceStatusSnapshot? snapshot
    )
    {
        RunOnUiThread(
            () =>
            {
                _serviceStatusControl
                    .SetServiceStates(
                        snapshot
                    );
            }
        );
    }

    public void SetHasCurrentMatch(
        bool hasCurrentMatch
    )
    {
        lock (_requestedUiStateGate)
        {
            if (
                _requestedHasCurrentMatch ==
                    hasCurrentMatch
            )
            {
                return;
            }

            _requestedHasCurrentMatch =
                hasCurrentMatch;
        }

        RunOnUiThread(
            () =>
            {
                _hasCurrentMatch =
                    hasCurrentMatch;

                _matchContentHeightAdjusted =
                    false;

                ApplySelectedSectionVisibility();
                ScheduleWindowHeightForCurrentContent();
            }
        );
    }

    private void OnModuleSettingsChanged(
        object? sender,
        BridgeModuleSettingsChangedEventArgs e
    )
    {
        try
        {
            var heroDamageWasEnabled =
                _persistedModuleSettings.IsEnabled(
                    BridgeServiceKind.HeroDamage
                );

            var heroDamageEnabled =
                e.Settings.IsEnabled(
                    BridgeServiceKind.HeroDamage
                );

            BridgeModuleSettingsPersistence
                .Save(
                    e.Settings
                );

            var heroDamageChangedAtUtcTicks =
                DateTimeOffset.UtcNow
                    .UtcDateTime
                    .Ticks;

            _persistedModuleSettings =
                e.Settings;

            _rankRefreshRequestGeneration +=
                1;

            if (
                heroDamageWasEnabled !=
                    heroDamageEnabled
            )
            {
                /*
                 * The worker is a separate process. Notify it immediately
                 * after persistence succeeds so an active URL probe/parser is
                 * stopped without waiting for another Panorama request.
                 * Enabling is intentionally only a notification: the worker's
                 * per-match latch will not start Hero Damage mid-match.
                 */
                _ =
                    NotifyHeroDamageModuleStateChangedAsync(
                        heroDamageEnabled,
                        heroDamageChangedAtUtcTicks
                    );
            }

            if (
                !e.Settings.IsEnabled(
                    BridgeServiceKind.Adviser
                )
            )
            {
                _alliesLaneStatsPanel
                    .ClearLaneStats();
            }

            if (
                !e.Settings.IsEnabled(
                    BridgeServiceKind.Rank
                )
            )
            {
                SetDisplayedRanksUnavailable(
                    "Rank module is disabled."
                );
            }
            else
            {
                RefreshDisplayedRanks();

                _ = RequestRankRefreshAsync(
                    _rankRefreshRequestGeneration
                );
            }

            AppendLog(
                "Module settings saved" +
                " | enabledMask=" +
                e.Settings.EnabledMask
            );
        }
        catch (Exception error)
        {
            _serviceStatusControl
                .SetSettings(
                    _persistedModuleSettings
                );

            AppendLog(
                "Module settings save ERROR:" +
                Environment.NewLine +
                error
            );
        }
    }

    private async Task
        NotifyHeroDamageModuleStateChangedAsync(
            bool enabled,
            long changedAtUtcTicks
        )
    {
        var maximumAttempts =
            enabled
                ? 1
                : 3;

        for (
            var attempt = 1;
            attempt <= maximumAttempts;
            attempt++
        )
        {
            try
            {
                using var request =
                    new HttpRequestMessage(
                        HttpMethod.Post,
                        HeroDamageModuleStateChangedAddress +
                        "?enabled=" +
                        (
                            enabled
                                ? "1"
                                : "0"
                        ) +
                        "&changedAtUtcTicks=" +
                        changedAtUtcTicks.ToString(
                            CultureInfo.InvariantCulture
                        )
                    );

                using var response =
                    await _currentMatchDamageHttpClient
                        .SendAsync(
                            request,
                            HttpCompletionOption
                                .ResponseHeadersRead,
                            _currentMatchDamageCancellation
                                .Token
                        );

                if (response.IsSuccessStatusCode)
                {
                    return;
                }
            }
            catch (
                OperationCanceledException
            )
            when (
                _currentMatchDamageCancellation
                    .IsCancellationRequested
            )
            {
                return;
            }
            catch (
                OperationCanceledException
            )
            {
                // HttpClient timeout: retry an Off transition below.
            }
            catch (
                HttpRequestException
            )
            {
            }
            catch (
                ObjectDisposedException
            )
            {
                return;
            }

            if (attempt >= maximumAttempts)
            {
                return;
            }

            try
            {
                await Task.Delay(
                    200,
                    _currentMatchDamageCancellation
                        .Token
                );
            }
            catch (
                OperationCanceledException
            )
            {
                return;
            }
        }
    }

    public void SetMatchPlayerDetails(
        MatchPlayerDetailsUiState state,
        IReadOnlyList<
            DeadlockMatchPlayerDetailsEntry
        >? players = null,
        string? detail = null
    )
    {
        var normalizedDetail =
            String.IsNullOrWhiteSpace(
                detail
            )
                ? String.Empty
                : detail;

        lock (_requestedUiStateGate)
        {
            if (
                state !=
                    MatchPlayerDetailsUiState.Ready &&
                _requestedMatchPlayerDetailsState ==
                    state &&
                String.Equals(
                    _requestedMatchPlayerDetailsDetail,
                    normalizedDetail,
                    StringComparison.Ordinal
                )
            )
            {
                return;
            }

            _requestedMatchPlayerDetailsState =
                state;

            _requestedMatchPlayerDetailsDetail =
                normalizedDetail;
        }

        RunOnUiThread(
            () =>
            {
                _currentMatchDamageTimer.Stop();

                _matchIconGeneration +=
                    1;

                _matchRankIconGeneration +=
                    1;

                _alliesLaneStatsPanel
                    .ClearLaneStats();

                _alliesMatchGrid.Rows.Clear();
                _enemiesMatchGrid.Rows.Clear();

                if (
                    state ==
                        MatchPlayerDetailsUiState.Loading
                )
                {
                    _matchContentHeightAdjusted =
                        false;
                }

                ScheduleWindowHeightForCurrentContent();

                if (
                    state !=
                        MatchPlayerDetailsUiState.Ready
                )
                {
                    _currentMatchDamageByAccountId.Clear();
                    ClearHeroIconCache();
                    ClearRankIconCache();
                }

                switch (state)
                {
                    case MatchPlayerDetailsUiState.Waiting:
                        _matchStatusValue.Text =
                            "Waiting for current match player stats...";

                        _matchStatusValue.ForeColor =
                            BridgeUiTheme.TextMuted;

                        return;

                    case MatchPlayerDetailsUiState.Loading:
                        _matchStatusValue.Text =
                            "Loading current hero statistics...";

                        _matchStatusValue.ForeColor =
                            Color.LightSkyBlue;

                        return;

                    case MatchPlayerDetailsUiState.Error:
                        _matchStatusValue.Text =
                            String.IsNullOrWhiteSpace(
                                normalizedDetail
                            )
                                ? "Failed to load current hero statistics."
                                : "Failed to load current hero statistics: " +
                                    normalizedDetail;

                        _matchStatusValue.ForeColor =
                            Color.LightCoral;

                        return;

                    case MatchPlayerDetailsUiState.Ready:
                        break;

                    default:
                        return;
                }

                if (
                    players is null ||
                    players.Count !=
                        12
                )
                {
                    _matchStatusValue.Text =
                        "Invalid current match player statistics.";

                    _matchStatusValue.ForeColor =
                        Color.LightCoral;

                    return;
                }

                _matchStatusValue.Text =
                    "Current-match damage with lifetime hero statistics";

                _matchStatusValue.ForeColor =
                    Color.LightGreen;

                uint? localPlayerAccountId;

                lock (_requestedUiStateGate)
                {
                    localPlayerAccountId =
                        _requestedAccountIdInitialized
                            ? _requestedAccountId
                            : null;
                }

                PopulateMatchGrid(
                    _alliesMatchGrid,
                    players
                        .Take(6)
                        .ToArray(),
                    _matchIconGeneration,
                    _matchRankIconGeneration,
                    localPlayerAccountId
                );

                PopulateMatchGrid(
                    _enemiesMatchGrid,
                    players
                        .Skip(6)
                        .Take(6)
                        .ToArray(),
                    _matchIconGeneration,
                    _matchRankIconGeneration,
                    localPlayerAccountId
                );

                ApplyCurrentMatchDamage(
                    _alliesMatchGrid,
                    _currentMatchDamageByAccountId
                );

                ApplyCurrentMatchDamage(
                    _enemiesMatchGrid,
                    _currentMatchDamageByAccountId
                );

                _currentMatchDamageTimer.Start();
            }
        );
    }

    private void PopulateMatchGrid(
        DataGridView grid,
        IReadOnlyList<
            DeadlockMatchPlayerDetailsEntry
        > players,
        int heroIconGeneration,
        int rankIconGeneration,
        uint? localPlayerAccountId
    )
    {
        foreach (
            var player in players
                .OrderBy(
                    player =>
                        player.Index
                )
        )
        {
            var hasStats =
                String.Equals(
                    player.Status,
                    "ok",
                    StringComparison.Ordinal
                ) &&
                player.MatchesPlayed >
                    0;

            var currentDamage =
                GetCurrentMatchDamage(
                    player
                );

            var rankModuleEnabled =
                _persistedModuleSettings.IsEnabled(
                    BridgeServiceKind.Rank
                );

            var hasRank =
                TryGetDisplayRank(
                    player,
                    out var rank,
                    out var subrank
                ) &&
                rankModuleEnabled;

            var rowIndex =
                grid.Rows.Add();

            var row =
                grid.Rows[
                    rowIndex
                ];

            row.Tag =
                player;

            ApplyLocalPlayerRowStyle(
                row,
                player.AccountId,
                localPlayerAccountId
            );

            row.Cells["Hero"].Value =
                null;

            row.Cells["Rank"].Value =
                _rankUnavailableIcon;

            row.Cells["WinRate"].Value =
                hasStats
                    ? FormatPercent(
                        player.WinRatePercent
                    )
                    : "—";

            if (hasStats)
            {
                var winRateColor =
                    GetHeroWinRateColor(
                        player.WinRatePercent
                    );

                row.Cells["WinRate"]
                    .Style.ForeColor =
                        winRateColor;

                row.Cells["WinRate"]
                    .Style.SelectionForeColor =
                        winRateColor;
            }

            row.Cells["Damage"].Value =
                currentDamage.ToString(
                    CultureInfo.InvariantCulture
                );

            row.Cells["SoulsPerMinute"].Value =
                hasStats
                    ? Math.Round(
                        player.SoulsPerMinute
                    )
                        .ToString(
                            "0",
                            CultureInfo.InvariantCulture
                        )
                    : "—";

            row.Cells["Headshots"].Value =
                hasStats
                    ? FormatPercent(
                        player.HeadshotRatePercent
                    )
                    : "—";

            row.Cells["Accuracy"].Value =
                hasStats
                    ? FormatPercent(
                        player.AccuracyPercent
                    )
                    : "—";

            if (
                ReferenceEquals(
                    grid,
                    _alliesMatchGrid
                ) &&
                rowIndex % 2 == 1 &&
                rowIndex <
                    players.Count -
                    1
            )
            {
                row.DividerHeight =
                    2;
            }

            row.Cells["Hero"].ToolTipText =
                CanOpenSteamProfile(
                    player
                )
                    ? player.HeroName +
                        Environment.NewLine +
                        "Click to open Steam profile."
                    : player.HeroName;

            row.Cells["Rank"].ToolTipText =
                rankModuleEnabled
                    ? GetRankToolTipText(
                        player,
                        hasRank,
                        rank,
                        subrank
                    )
                    : "Rank module is disabled.";

            row.Cells["WinRate"].ToolTipText =
                hasStats
                    ? "Lifetime win rate with " +
                        player.HeroName +
                        " (" +
                        player.MatchesPlayed.ToString(
                            CultureInfo.InvariantCulture
                        ) +
                        " matches)"
                    : "Lifetime hero win rate is unavailable.";

            if (
                !String.IsNullOrWhiteSpace(
                    player.HeroIconUrl
                )
            )
            {
                QueueHeroIconLoad(
                    grid,
                    rowIndex,
                    player.HeroIconUrl!,
                    heroIconGeneration
                );
            }

            if (hasRank)
            {
                QueueRankIconLoad(
                    grid,
                    rowIndex,
                    rank,
                    subrank,
                    rankIconGeneration
                );
            }
        }
    }

    internal static void ApplyLocalPlayerRowStyle(
        DataGridViewRow row,
        uint playerAccountId,
        uint? localPlayerAccountId
    )
    {
        ArgumentNullException.ThrowIfNull(
            row
        );

        var backgroundColor =
            playerAccountId != 0 &&
            localPlayerAccountId.HasValue &&
            playerAccountId ==
                localPlayerAccountId.Value
                ? BridgeUiTheme.SurfaceHover
                : BridgeUiTheme.SurfaceRaised;

        if (
            row.DefaultCellStyle.BackColor !=
                backgroundColor
        )
        {
            row.DefaultCellStyle.BackColor =
                backgroundColor;
        }

        if (
            row.DefaultCellStyle.SelectionBackColor !=
                backgroundColor
        )
        {
            row.DefaultCellStyle.SelectionBackColor =
                backgroundColor;
        }
    }

    private void OnMatchGridCellContentClick(
        object? sender,
        DataGridViewCellEventArgs e
    )
    {
        if (
            sender is not DataGridView grid ||
            e.RowIndex < 0 ||
            e.RowIndex >=
                grid.Rows.Count ||
            e.ColumnIndex < 0 ||
            e.ColumnIndex >=
                grid.Columns.Count ||
            !String.Equals(
                grid.Columns[
                    e.ColumnIndex
                ].Name,
                "Hero",
                StringComparison.Ordinal
            ) ||
            grid.Rows[
                e.RowIndex
            ].Tag is not
                DeadlockMatchPlayerDetailsEntry player ||
            !CanOpenSteamProfile(
                player
            )
        )
        {
            return;
        }

        var steamId64 =
            SteamId64IndividualBase +
            player.AccountId;

        try
        {
            using var process =
                Process.Start(
                    new ProcessStartInfo
                    {
                        FileName =
                            "https://steamcommunity.com/" +
                            "profiles/" +
                            steamId64.ToString(
                                CultureInfo.InvariantCulture
                            ),

                        UseShellExecute =
                            true
                    }
                );
        }
        catch (Exception error)
        {
            AppendLog(
                "Failed to open the Steam profile: " +
                error.Message
            );

            if (
                !IsDisposed &&
                !Disposing
            )
            {
                MessageBox.Show(
                    this,
                    error.Message,
                    "Failed to open the Steam profile",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error
                );
            }
        }
    }

    private static bool CanOpenSteamProfile(
        DeadlockMatchPlayerDetailsEntry player
    )
    {
        return
            player.AccountId != 0 &&
            !String.Equals(
                player.Status,
                "bot",
                StringComparison.OrdinalIgnoreCase
            );
    }

    private static bool TryGetDisplayRank(
        DeadlockMatchPlayerDetailsEntry player,
        out byte rank,
        out byte subrank
    )
    {
        rank =
            player.Rank;

        subrank =
            player.Subrank;

        return
            player.AccountId != 0 &&
            String.Equals(
                player.RankStatus,
                "ok",
                StringComparison.OrdinalIgnoreCase
            ) &&
            rank >= 1 &&
            rank <= 11 &&
            subrank >= 1 &&
            subrank <= 6;
    }

    private static string GetRankToolTipText(
        DeadlockMatchPlayerDetailsEntry player,
        bool hasRank,
        byte rank,
        byte subrank
    )
    {
        if (hasRank)
        {
            return
                "Rank " +
                rank.ToString(
                    CultureInfo.InvariantCulture
                ) +
                ", subrank " +
                subrank.ToString(
                    CultureInfo.InvariantCulture
                );
        }

        if (
            String.Equals(
                player.RankStatus,
                "disabled",
                StringComparison.OrdinalIgnoreCase
            )
        )
        {
            return "Rank module is disabled.";
        }

        if (
            String.Equals(
                player.Status,
                "bot",
                StringComparison.OrdinalIgnoreCase
            )
        )
        {
            return "Rank is unavailable for bots.";
        }

        if (player.AccountId == 0)
        {
            return "Player identity is unresolved.";
        }

        if (
            String.Equals(
                player.RankStatus,
                "loading",
                StringComparison.OrdinalIgnoreCase
            )
        )
        {
            return "Loading rank...";
        }

        if (
            String.Equals(
                player.RankStatus,
                "unranked",
                StringComparison.OrdinalIgnoreCase
            )
        )
        {
            return "Player is unranked.";
        }

        if (
            String.Equals(
                player.RankStatus,
                "protected",
                StringComparison.OrdinalIgnoreCase
            )
        )
        {
            return "Player rank is protected.";
        }

        if (
            String.Equals(
                player.RankStatus,
                "not_found",
                StringComparison.OrdinalIgnoreCase
            )
        )
        {
            return "Player rank was not found.";
        }

        if (
            String.Equals(
                player.RankStatus,
                "error",
                StringComparison.OrdinalIgnoreCase
            )
        )
        {
            return "Failed to load player rank.";
        }

        if (
            String.Equals(
                player.RankStatus,
                "ok",
                StringComparison.OrdinalIgnoreCase
            )
        )
        {
            return "Player rank data is invalid.";
        }

        return "Player rank is unavailable.";
    }

    private long GetCurrentMatchDamage(
        DeadlockMatchPlayerDetailsEntry player
    )
    {
        if (
            !TryGetCurrentMatchDamage(
                player,
                _currentMatchDamageByAccountId,
                out var liveDamage
            )
        )
        {
            return 0;
        }

        return liveDamage.HeroDamage;
    }

    private static bool TryGetCurrentMatchDamage(
        DeadlockMatchPlayerDetailsEntry player,
        IReadOnlyDictionary<
            uint,
            CurrentMatchDamageValue
        > damageByAccountId,
        out CurrentMatchDamageValue liveDamage
    )
    {
        if (
            player.AccountId != 0 &&
            damageByAccountId.TryGetValue(
                player.AccountId,
                out var accountDamage
            ) &&
            (
                player.HeroId == 0 ||
                accountDamage.HeroId == 0 ||
                player.HeroId ==
                    accountDamage.HeroId
            )
        )
        {
            liveDamage =
                accountDamage;

            return true;
        }

        if (player.HeroId == 0)
        {
            liveDamage =
                default;

            return false;
        }

        var found =
            false;

        var heroDamage =
            default(CurrentMatchDamageValue);

        foreach (
            var candidate in
            damageByAccountId.Values
        )
        {
            if (
                candidate.HeroId !=
                    player.HeroId
            )
            {
                continue;
            }

            if (found)
            {
                liveDamage =
                    default;

                return false;
            }

            heroDamage =
                candidate;

            found =
                true;
        }

        liveDamage =
            heroDamage;

        return found;
    }

    private async void OnCurrentMatchDamageTimerTick(
        object? sender,
        EventArgs e
    )
    {
        if (
            Interlocked.Exchange(
                ref _currentMatchDamagePollRunning,
                1
            ) != 0
        )
        {
            return;
        }

        try
        {
            if (
                _alliesMatchGrid.Rows.Count == 0 &&
                _enemiesMatchGrid.Rows.Count == 0
            )
            {
                return;
            }

            var rowGeneration =
                _matchIconGeneration;

            var cancellationToken =
                _currentMatchDamageCancellation.Token;

            using var response =
                await _currentMatchDamageHttpClient.GetAsync(
                    CurrentMatchContextDiagnosticsAddress,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken
                );

            if (!response.IsSuccessStatusCode)
            {
                return;
            }

            await using var stream =
                await response.Content.ReadAsStreamAsync(
                    cancellationToken
                );

            var diagnostics =
                await JsonSerializer.DeserializeAsync<
                    CurrentMatchDamageDiagnosticsResponse
                >(
                    stream,
                    CurrentMatchDamageJsonOptions,
                    cancellationToken
                );

            if (diagnostics?.Snapshot is null)
            {
                return;
            }

            var damageByAccountId =
                BuildCurrentMatchDamageIndex(
                    diagnostics.Snapshot
                );

            if (
                rowGeneration !=
                    _matchIconGeneration ||
                IsDisposed ||
                Disposing ||
                cancellationToken.IsCancellationRequested
            )
            {
                return;
            }

            ApplyCurrentMatchDamage(
                damageByAccountId
            );

            ApplyCurrentLaneStats(
                diagnostics.CurrentLaneStats
            );
        }
        catch (OperationCanceledException)
        {
            // Form shutdown and local HTTP timeouts are non-fatal.
        }
        catch (HttpRequestException)
        {
            // The worker may be restarting. Keep the last displayed values.
        }
        catch (JsonException)
        {
            // Ignore one malformed diagnostics response and retry next tick.
        }
        catch (ObjectDisposedException)
        {
        }
        finally
        {
            Volatile.Write(
                ref _currentMatchDamagePollRunning,
                0
            );
        }
    }

    private static IReadOnlyDictionary<
        uint,
        CurrentMatchDamageValue
    > BuildCurrentMatchDamageIndex(
        CurrentMatchDamageDiagnosticsSnapshot snapshot
    )
    {
        var result =
            new Dictionary<
                uint,
                CurrentMatchDamageValue
            >();

        var liveDamage =
            snapshot.LiveDamage;

        if (
            snapshot.MatchId == 0 ||
            liveDamage is null ||
            liveDamage.MatchId !=
                snapshot.MatchId ||
            liveDamage.Players is null
        )
        {
            return result;
        }

        foreach (var player in liveDamage.Players)
        {
            if (
                player.AccountId == 0 ||
                player.HeroDamage < 0
            )
            {
                continue;
            }

            var value =
                new CurrentMatchDamageValue(
                    HeroId:
                        player.HeroId,
                    HeroDamage:
                        player.HeroDamage,
                    Tick:
                        player.Tick
                );

            if (
                !result.TryGetValue(
                    player.AccountId,
                    out var previous
                ) ||
                value.Tick >=
                    previous.Tick
            )
            {
                result[player.AccountId] =
                    value;
            }
        }

        return result;
    }

    private void ApplyCurrentLaneStats(
        CurrentMatchLaneStatsDiagnostics?
            snapshot
    )
    {
        if (
            !_persistedModuleSettings
                .IsEnabled(
                    BridgeServiceKind.Adviser
                ) ||
            !TryBuildCurrentLaneDisplayValues(
                snapshot,
                out var laneValues
            )
        )
        {
            _alliesLaneStatsPanel
                .ClearLaneStats();

            return;
        }

        _alliesLaneStatsPanel
            .SetLaneStats(
                laneValues
            );
    }

    private bool TryBuildCurrentLaneDisplayValues(
        CurrentMatchLaneStatsDiagnostics?
            snapshot,
        out IReadOnlyList<
            CurrentMatchLaneDisplayValue
        > laneValues
    )
    {
        laneValues =
            Array.Empty<
                CurrentMatchLaneDisplayValue
            >();

        if (
            snapshot?.HeroIds is null ||
            snapshot.Lanes is null ||
            snapshot.Lanes.Count !=
                CurrentMatchLaneCount ||
            !CurrentLaneStatsMatchDisplayedRoster(
                snapshot.HeroIds
            )
        )
        {
            return false;
        }

        var result =
            new CurrentMatchLaneDisplayValue[
                CurrentMatchLaneCount
            ];

        var assigned =
            new bool[
                CurrentMatchLaneCount
            ];

        foreach (var lane in snapshot.Lanes)
        {
            if (
                lane is null ||
                lane.LaneIndex < 0 ||
                lane.LaneIndex >=
                    CurrentMatchLaneCount ||
                assigned[
                    lane.LaneIndex
                ]
            )
            {
                return false;
            }

            double? winRatePercent =
                null;

            if (lane.HasMatchData)
            {
                if (
                    lane.Matches == 0 ||
                    !lane.WinRatePercent.HasValue ||
                    !double.IsFinite(
                        lane.WinRatePercent.Value
                    ) ||
                    lane.WinRatePercent.Value < 0 ||
                    lane.WinRatePercent.Value > 100
                )
                {
                    return false;
                }

                winRatePercent =
                    lane.WinRatePercent.Value;
            }

            double? netWorthDiff15 =
                null;

            if (lane.HasNetWorthData)
            {
                if (
                    lane.NetWorthMatches == 0 ||
                    !lane.NetWorthDiff15.HasValue ||
                    !double.IsFinite(
                        lane.NetWorthDiff15.Value
                    )
                )
                {
                    return false;
                }

                netWorthDiff15 =
                    lane.NetWorthDiff15.Value;
            }

            result[
                lane.LaneIndex
            ] =
                new CurrentMatchLaneDisplayValue(
                    WinRatePercent:
                        winRatePercent,

                    Matches:
                        lane.Matches,

                    NetWorthDiff15:
                        netWorthDiff15,

                    NetWorthMatches:
                        lane.NetWorthMatches
                );

            assigned[
                lane.LaneIndex
            ] =
                true;
        }

        if (
            assigned.Any(
                value =>
                    !value
            )
        )
        {
            return false;
        }

        laneValues =
            result;

        return true;
    }

    private bool CurrentLaneStatsMatchDisplayedRoster(
        IReadOnlyList<uint> heroIds
    )
    {
        var teamSize =
            CurrentMatchLaneCount *
            2;

        if (
            heroIds.Count !=
                teamSize *
                2 ||
            _alliesMatchGrid.Rows.Count !=
                teamSize ||
            _enemiesMatchGrid.Rows.Count !=
                teamSize
        )
        {
            return false;
        }

        for (
            var index = 0;
            index < teamSize;
            index++
        )
        {
            if (
                _alliesMatchGrid.Rows[
                    index
                ].Tag is not
                    DeadlockMatchPlayerDetailsEntry
                        ally ||
                ally.HeroId !=
                    heroIds[
                        index
                    ] ||
                _enemiesMatchGrid.Rows[
                    index
                ].Tag is not
                    DeadlockMatchPlayerDetailsEntry
                        enemy ||
                enemy.HeroId !=
                    heroIds[
                        teamSize +
                        index
                    ]
            )
            {
                return false;
            }
        }

        return true;
    }

    private void ApplyCurrentMatchDamage(
        IReadOnlyDictionary<
            uint,
            CurrentMatchDamageValue
        > damageByAccountId
    )
    {
        _currentMatchDamageByAccountId.Clear();

        foreach (
            var pair in
            damageByAccountId
        )
        {
            _currentMatchDamageByAccountId[
                pair.Key
            ] =
                pair.Value;
        }

        ApplyCurrentMatchDamage(
            _alliesMatchGrid,
            _currentMatchDamageByAccountId
        );

        ApplyCurrentMatchDamage(
            _enemiesMatchGrid,
            _currentMatchDamageByAccountId
        );
    }

    private static void ApplyCurrentMatchDamage(
        DataGridView grid,
        IReadOnlyDictionary<
            uint,
            CurrentMatchDamageValue
        > damageByAccountId
    )
    {
        var damageColumn =
            grid.Columns["Damage"];

        if (damageColumn is null)
        {
            return;
        }

        var displayValues =
            new List<(
                DataGridViewCell Cell,
                long Damage,
                bool HasLiveDamage
            )>(
                grid.Rows.Count
            );

        foreach (
            DataGridViewRow row in
            grid.Rows
        )
        {
            var damage =
                0L;

            var hasLiveDamage =
                false;

            if (
                row.Tag is
                    DeadlockMatchPlayerDetailsEntry player &&
                TryGetCurrentMatchDamage(
                    player,
                    damageByAccountId,
                    out var liveDamage
                )
            )
            {
                damage =
                    liveDamage.HeroDamage;

                hasLiveDamage =
                    true;
            }

            var damageCell =
                row.Cells[
                    damageColumn.Index
                ];

            displayValues.Add(
                (
                    damageCell,
                    damage,
                    hasLiveDamage
                )
            );
        }

        var useComparisonColors =
            displayValues.Count >= 2 &&
            displayValues.All(
                value =>
                    value.HasLiveDamage
            );

        var minimumDamage =
            0L;

        var maximumDamage =
            0L;

        var averageDamage =
            0M;

        var exceptionalDamageThreshold =
            0M;

        if (useComparisonColors)
        {
            minimumDamage =
                displayValues.Min(
                    value =>
                        value.Damage
                );

            maximumDamage =
                displayValues.Max(
                    value =>
                        value.Damage
                );

            if (
                minimumDamage ==
                    maximumDamage
            )
            {
                useComparisonColors =
                    false;
            }
            else
            {
                averageDamage =
                    displayValues.Sum(
                        value =>
                            (decimal)value.Damage
                    ) /
                    displayValues.Count;

                exceptionalDamageThreshold =
                    averageDamage *
                    CurrentMatchExceptionalDamageMultiplier;
            }
        }

        foreach (var value in displayValues)
        {
            var color =
                BridgeUiTheme.Text;

            if (useComparisonColors)
            {
                color =
                    averageDamage > 0M &&
                    value.Damage >=
                        exceptionalDamageThreshold
                        ? CurrentMatchExceptionalDamageColor
                        : value.Damage ==
                            maximumDamage
                            ? CurrentMatchTopDamageColor
                            : value.Damage ==
                                minimumDamage
                                ? CurrentMatchLowestDamageColor
                                : BridgeUiTheme.Text;
            }

            var damageText =
                value.Damage.ToString(
                    CultureInfo.InvariantCulture
                );

            if (
                !String.Equals(
                    value.Cell.Value as string,
                    damageText,
                    StringComparison.Ordinal
                )
            )
            {
                value.Cell.Value =
                    damageText;
            }

            SetCurrentMatchDamageCellColor(
                value.Cell,
                color
            );
        }
    }

    private static void SetCurrentMatchDamageCellColor(
        DataGridViewCell cell,
        Color color
    )
    {
        if (
            cell.Style.ForeColor !=
                color
        )
        {
            cell.Style.ForeColor =
                color;
        }

        if (
            cell.Style.SelectionForeColor !=
                color
        )
        {
            cell.Style.SelectionForeColor =
                color;
        }
    }

    private void QueueHeroIconLoad(
        DataGridView grid,
        int rowIndex,
        string heroIconUrl,
        int iconGeneration
    )
    {
        if (
            _heroIconCache.TryGetValue(
                heroIconUrl,
                out var cachedImage
            )
        )
        {
            grid.Rows[rowIndex]
                .Cells["Hero"]
                .Value =
                    cachedImage;

            return;
        }

        _ = LoadHeroIconAsync(
            grid,
            rowIndex,
            heroIconUrl,
            iconGeneration
        );
    }

    private async Task LoadHeroIconAsync(
        DataGridView grid,
        int rowIndex,
        string heroIconUrl,
        int iconGeneration
    )
    {
        try
        {
            using var response =
                await _heroImageHttpClient.GetAsync(
                    heroIconUrl,
                    HttpCompletionOption.ResponseHeadersRead
                );

            if (!response.IsSuccessStatusCode)
            {
                return;
            }

            if (
                response.Content.Headers.ContentLength is
                    long declaredLength &&
                declaredLength >
                    MaximumHeroIconBytes
            )
            {
                return;
            }

            var bytes =
                await ReadLimitedHeroIconBytesAsync(
                    response.Content
                );

            if (
                bytes is null ||
                !TryReadHeroIconDimensions(
                    bytes,
                    out var encodedWidth,
                    out var encodedHeight
                )
            )
            {
                return;
            }

            using var stream =
                new MemoryStream(
                    bytes
                );

            using var sourceImage =
                Image.FromStream(
                    stream,
                    useEmbeddedColorManagement:
                        false,
                    validateImageData:
                        true
                );

            if (
                sourceImage.Width !=
                    encodedWidth ||
                sourceImage.Height !=
                    encodedHeight
            )
            {
                return;
            }

            using var renderedIcon =
                new Bitmap(
                    HeroIconPixels,
                    HeroIconPixels,
                    PixelFormat.Format32bppPArgb
                );

            using (
                var graphics =
                    Graphics.FromImage(
                        renderedIcon
                    )
            )
            {
                graphics.Clear(
                    Color.Transparent
                );

                graphics.CompositingMode =
                    CompositingMode.SourceOver;

                graphics.CompositingQuality =
                    CompositingQuality.HighQuality;

                graphics.InterpolationMode =
                    InterpolationMode.HighQualityBicubic;

                graphics.PixelOffsetMode =
                    PixelOffsetMode.HighQuality;

                graphics.SmoothingMode =
                    SmoothingMode.HighQuality;

                var scale =
                    Math.Min(
                        HeroIconPixels /
                            (double)sourceImage.Width,
                        HeroIconPixels /
                            (double)sourceImage.Height
                    );

                var drawWidth =
                    Math.Max(
                        1,
                        (int)Math.Round(
                            sourceImage.Width * scale,
                            MidpointRounding.AwayFromZero
                        )
                    );

                var drawHeight =
                    Math.Max(
                        1,
                        (int)Math.Round(
                            sourceImage.Height * scale,
                            MidpointRounding.AwayFromZero
                        )
                    );

                var destination =
                    new Rectangle(
                        (HeroIconPixels - drawWidth) / 2,
                        (HeroIconPixels - drawHeight) / 2,
                        drawWidth,
                        drawHeight
                    );

                graphics.DrawImage(
                    sourceImage,
                    destination,
                    0,
                    0,
                    sourceImage.Width,
                    sourceImage.Height,
                    GraphicsUnit.Pixel
                );
            }

            if (
                iconGeneration !=
                    _matchIconGeneration ||
                IsDisposed ||
                Disposing ||
                rowIndex < 0 ||
                rowIndex >=
                    grid.Rows.Count
            )
            {
                return;
            }

            Image icon;

            if (
                _heroIconCache.TryGetValue(
                    heroIconUrl,
                    out var existingImage
                )
            )
            {
                icon =
                    existingImage;
            }
            else
            {
                if (
                    _heroIconCache.Count >=
                        MaximumHeroIconCacheEntries
                )
                {
                    return;
                }

                /*
                 * The cache owns an independent 48x48 bitmap. renderedIcon
                 * remains scoped to this async operation and is disposed even
                 * if GDI+ or a later grid update throws.
                 */
                icon =
                    new Bitmap(
                        renderedIcon
                    );

                _heroIconCache[
                    heroIconUrl
                ] =
                    icon;
            }

            var row =
                grid.Rows[
                    rowIndex
                ];

            if (
                row.Tag is not
                    DeadlockMatchPlayerDetailsEntry
                        rowPlayer ||
                !String.Equals(
                    rowPlayer.HeroIconUrl,
                    heroIconUrl,
                    StringComparison.OrdinalIgnoreCase
                )
            )
            {
                return;
            }

            row.Cells["Hero"].Value =
                icon;
        }
        catch
        {
            // Missing hero art must not break Match statistics.
        }
    }

    private void QueueRankIconLoad(
        DataGridView grid,
        int rowIndex,
        byte rank,
        byte subrank,
        int iconGeneration
    )
    {
        var cacheKey =
            rank *
                10 +
            subrank;

        if (
            _rankIconCache.TryGetValue(
                cacheKey,
                out var cachedImage
            )
        )
        {
            grid.Rows[rowIndex]
                .Cells["Rank"]
                .Value =
                    cachedImage;

            return;
        }

        _ = LoadRankIconAsync(
            grid,
            rowIndex,
            rank,
            subrank,
            iconGeneration
        );
    }

    private async Task LoadRankIconAsync(
        DataGridView grid,
        int rowIndex,
        byte rank,
        byte subrank,
        int iconGeneration
    )
    {
        try
        {
            var rankImageUrl =
                RankImageAddress +
                "?rank=" +
                rank.ToString(
                    CultureInfo.InvariantCulture
                ) +
                "&subrank=" +
                subrank.ToString(
                    CultureInfo.InvariantCulture
                );

            using var response =
                await _rankImageHttpClient.GetAsync(
                    rankImageUrl,
                    HttpCompletionOption.ResponseHeadersRead
                );

            if (!response.IsSuccessStatusCode)
            {
                return;
            }

            if (
                response.Content.Headers.ContentLength is
                    long declaredLength &&
                declaredLength >
                    MaximumRankIconBytes
            )
            {
                return;
            }

            var bytes =
                await ReadLimitedRankIconBytesAsync(
                    response.Content
                );

            if (
                bytes is null ||
                !TryReadRankIconDimensions(
                    bytes,
                    out var encodedWidth,
                    out var encodedHeight
                )
            )
            {
                return;
            }

            using var stream =
                new MemoryStream(
                    bytes
                );

            using var sourceImage =
                Image.FromStream(
                    stream,
                    useEmbeddedColorManagement:
                        false,
                    validateImageData:
                        true
                );

            if (
                sourceImage.Width !=
                    encodedWidth ||
                sourceImage.Height !=
                    encodedHeight
            )
            {
                return;
            }

            using var renderedIcon =
                new Bitmap(
                    RankIconPixels,
                    RankIconPixels,
                    PixelFormat.Format32bppPArgb
                );

            using (
                var graphics =
                    Graphics.FromImage(
                        renderedIcon
                    )
            )
            {
                graphics.Clear(
                    Color.Transparent
                );

                graphics.CompositingMode =
                    CompositingMode.SourceOver;

                graphics.CompositingQuality =
                    CompositingQuality.HighQuality;

                graphics.InterpolationMode =
                    InterpolationMode.HighQualityBicubic;

                graphics.PixelOffsetMode =
                    PixelOffsetMode.HighQuality;

                graphics.SmoothingMode =
                    SmoothingMode.HighQuality;

                var scale =
                    Math.Min(
                        RankIconPixels /
                            (double)sourceImage.Width,
                        RankIconPixels /
                            (double)sourceImage.Height
                    );

                var drawWidth =
                    Math.Max(
                        1,
                        (int)Math.Round(
                            sourceImage.Width * scale,
                            MidpointRounding.AwayFromZero
                        )
                    );

                var drawHeight =
                    Math.Max(
                        1,
                        (int)Math.Round(
                            sourceImage.Height * scale,
                            MidpointRounding.AwayFromZero
                        )
                    );

                var destination =
                    new Rectangle(
                        (RankIconPixels - drawWidth) / 2,
                        (RankIconPixels - drawHeight) / 2,
                        drawWidth,
                        drawHeight
                    );

                graphics.DrawImage(
                    sourceImage,
                    destination,
                    0,
                    0,
                    sourceImage.Width,
                    sourceImage.Height,
                    GraphicsUnit.Pixel
                );
            }

            if (
                iconGeneration !=
                    _matchRankIconGeneration ||
                IsDisposed ||
                Disposing ||
                rowIndex < 0 ||
                rowIndex >=
                    grid.Rows.Count
            )
            {
                return;
            }

            var cacheKey =
                rank *
                    10 +
                subrank;

            Image icon;

            if (
                _rankIconCache.TryGetValue(
                    cacheKey,
                    out var existingImage
                )
            )
            {
                icon =
                    existingImage;
            }
            else
            {
                if (
                    _rankIconCache.Count >=
                        MaximumRankIconCacheEntries
                )
                {
                    return;
                }

                icon =
                    new Bitmap(
                        renderedIcon
                    );

                _rankIconCache[
                    cacheKey
                ] =
                    icon;
            }

            var row =
                grid.Rows[
                    rowIndex
                ];

            if (
                row.Tag is not
                    DeadlockMatchPlayerDetailsEntry
                        rowPlayer ||
                !TryGetDisplayRank(
                    rowPlayer,
                    out var rowRank,
                    out var rowSubrank
                ) ||
                rowRank != rank ||
                rowSubrank != subrank
            )
            {
                return;
            }

            row.Cells["Rank"].Value =
                icon;
        }
        catch
        {
            // Missing rank art must not break Match statistics.
        }
    }

    private static async Task<byte[]?>
        ReadLimitedHeroIconBytesAsync(
            HttpContent content
        )
    {
        await using var input =
            await content.ReadAsStreamAsync();

        using var output =
            new MemoryStream();

        var buffer =
            new byte[64 * 1024];

        while (true)
        {
            var bytesRead =
                await input.ReadAsync(
                    buffer.AsMemory()
                );

            if (bytesRead == 0)
            {
                break;
            }

            if (
                output.Length +
                    bytesRead >
                MaximumHeroIconBytes
            )
            {
                return null;
            }

            await output.WriteAsync(
                buffer.AsMemory(
                    0,
                    bytesRead
                )
            );
        }

        return output.ToArray();
    }

    private static async Task<byte[]?>
        ReadLimitedRankIconBytesAsync(
            HttpContent content
        )
    {
        await using var input =
            await content.ReadAsStreamAsync();

        using var output =
            new MemoryStream();

        var buffer =
            new byte[32 * 1024];

        while (true)
        {
            var bytesRead =
                await input.ReadAsync(
                    buffer.AsMemory()
                );

            if (bytesRead == 0)
            {
                break;
            }

            if (
                output.Length +
                    bytesRead >
                MaximumRankIconBytes
            )
            {
                return null;
            }

            await output.WriteAsync(
                buffer.AsMemory(
                    0,
                    bytesRead
                )
            );
        }

        return output.ToArray();
    }

    private static bool TryReadHeroIconDimensions(
        byte[] bytes,
        out int width,
        out int height
    )
    {
        width =
            0;

        height =
            0;

        if (
            bytes.Length < 24 ||
            bytes[0] != 137 ||
            bytes[1] != 80 ||
            bytes[2] != 78 ||
            bytes[3] != 71 ||
            bytes[4] != 13 ||
            bytes[5] != 10 ||
            bytes[6] != 26 ||
            bytes[7] != 10 ||
            bytes[12] != (byte)'I' ||
            bytes[13] != (byte)'H' ||
            bytes[14] != (byte)'D' ||
            bytes[15] != (byte)'R'
        )
        {
            return false;
        }

        width =
            BinaryPrimitives.ReadInt32BigEndian(
                bytes.AsSpan(
                    16,
                    4
                )
            );

        height =
            BinaryPrimitives.ReadInt32BigEndian(
                bytes.AsSpan(
                    20,
                    4
                )
            );

        return
            width > 0 &&
            height > 0 &&
            width <=
                MaximumHeroIconSourceDimension &&
            height <=
                MaximumHeroIconSourceDimension &&
            (long)width *
                height <=
                (long)MaximumHeroIconSourceDimension *
                MaximumHeroIconSourceDimension;
    }

    private static bool TryReadRankIconDimensions(
        byte[] bytes,
        out int width,
        out int height
    )
    {
        width =
            0;

        height =
            0;

        if (
            bytes.Length < 24 ||
            bytes[0] != 137 ||
            bytes[1] != 80 ||
            bytes[2] != 78 ||
            bytes[3] != 71 ||
            bytes[4] != 13 ||
            bytes[5] != 10 ||
            bytes[6] != 26 ||
            bytes[7] != 10 ||
            bytes[12] != (byte)'I' ||
            bytes[13] != (byte)'H' ||
            bytes[14] != (byte)'D' ||
            bytes[15] != (byte)'R'
        )
        {
            return false;
        }

        width =
            BinaryPrimitives.ReadInt32BigEndian(
                bytes.AsSpan(
                    16,
                    4
                )
            );

        height =
            BinaryPrimitives.ReadInt32BigEndian(
                bytes.AsSpan(
                    20,
                    4
                )
            );

        return
            width > 0 &&
            height > 0 &&
            width <=
                MaximumRankIconSourceDimension &&
            height <=
                MaximumRankIconSourceDimension &&
            (long)width *
                height <=
                (long)MaximumRankIconSourceDimension *
                MaximumRankIconSourceDimension;
    }

    private void ClearHeroIconCache()
    {
        foreach (var image in _heroIconCache.Values)
        {
            image.Dispose();
        }

        _heroIconCache.Clear();
    }

    private void ClearRankIconCache()
    {
        foreach (var image in _rankIconCache.Values)
        {
            image.Dispose();
        }

        _rankIconCache.Clear();
    }

    private void SetDisplayedRanksUnavailable(
        string toolTipText
    )
    {
        _matchRankIconGeneration +=
            1;

        SetDisplayedRanksUnavailable(
            _alliesMatchGrid,
            toolTipText
        );

        SetDisplayedRanksUnavailable(
            _enemiesMatchGrid,
            toolTipText
        );

        ClearRankIconCache();
    }

    private void RefreshDisplayedRanks()
    {
        _matchRankIconGeneration +=
            1;

        RefreshDisplayedRanks(
            _alliesMatchGrid,
            _matchRankIconGeneration
        );

        RefreshDisplayedRanks(
            _enemiesMatchGrid,
            _matchRankIconGeneration
        );
    }

    private void RefreshDisplayedRanks(
        DataGridView grid,
        int rankIconGeneration
    )
    {
        var rankColumn =
            grid.Columns["Rank"];

        if (rankColumn is null)
        {
            return;
        }

        foreach (
            DataGridViewRow row in
                grid.Rows
        )
        {
            var rankCell =
                row.Cells[
                    rankColumn.Index
                ];

            rankCell.Value =
                _rankUnavailableIcon;

            if (
                row.Tag is not
                    DeadlockMatchPlayerDetailsEntry
                        player
            )
            {
                rankCell.ToolTipText =
                    "Player rank is unavailable.";

                continue;
            }

            var hasRank =
                TryGetDisplayRank(
                    player,
                    out var rank,
                    out var subrank
                );

            rankCell.ToolTipText =
                GetRankToolTipText(
                    player,
                    hasRank,
                    rank,
                    subrank
                );

            if (hasRank)
            {
                QueueRankIconLoad(
                    grid,
                    row.Index,
                    rank,
                    subrank,
                    rankIconGeneration
                );
            }
        }
    }

    private async Task RequestRankRefreshAsync(
        int requestGeneration
    )
    {
        const int maximumAttempts =
            60;

        for (
            var attempt = 0;
            attempt < maximumAttempts;
            attempt++
        )
        {
            if (
                requestGeneration !=
                    _rankRefreshRequestGeneration ||
                !_persistedModuleSettings.IsEnabled(
                    BridgeServiceKind.Rank
                )
            )
            {
                return;
            }

            var shouldRetry =
                true;

            try
            {
                using var response =
                    await _rankImageHttpClient.PostAsync(
                        RankRefreshAddress,
                        content:
                            null,
                        cancellationToken:
                            _modManagementCancellation.Token
                    );

                if (response.IsSuccessStatusCode)
                {
                    await using var responseStream =
                        await response.Content
                            .ReadAsStreamAsync(
                                _modManagementCancellation.Token
                            );

                    using var document =
                        await JsonDocument.ParseAsync(
                            responseStream,
                            cancellationToken:
                                _modManagementCancellation.Token
                        );

                    var root =
                        document.RootElement;

                    if (
                        root.TryGetProperty(
                            "started",
                            out var startedElement
                        ) &&
                        startedElement.ValueKind ==
                            JsonValueKind.True
                    )
                    {
                        return;
                    }

                    shouldRetry =
                        root.TryGetProperty(
                            "retry",
                            out var retryElement
                        ) &&
                        retryElement.ValueKind ==
                            JsonValueKind.True;
                }
            }
            catch (
                OperationCanceledException
            )
            when (
                _modManagementCancellation
                    .IsCancellationRequested
            )
            {
                return;
            }
            catch
            {
                // The worker may be restarting; retry while Rank stays enabled.
            }

            if (!shouldRetry)
            {
                return;
            }

            try
            {
                await Task.Delay(
                    500,
                    _modManagementCancellation.Token
                );
            }
            catch (
                OperationCanceledException
            )
            {
                return;
            }
        }
    }

    private void SetDisplayedRanksUnavailable(
        DataGridView grid,
        string toolTipText
    )
    {
        var rankColumn =
            grid.Columns["Rank"];

        if (rankColumn is null)
        {
            return;
        }

        foreach (
            DataGridViewRow row in
                grid.Rows
        )
        {
            var rankCell =
                row.Cells[
                    rankColumn.Index
                ];

            rankCell.Value =
                _rankUnavailableIcon;

            rankCell.ToolTipText =
                toolTipText;
        }
    }

    private static Image CreateRankUnavailableIcon()
    {
        var image =
            new Bitmap(
                RankIconPixels,
                RankIconPixels,
                PixelFormat.Format32bppPArgb
            );

        using var graphics =
            Graphics.FromImage(
                image
            );

        graphics.Clear(
            Color.Transparent
        );

        graphics.TextRenderingHint =
            System.Drawing.Text.TextRenderingHint
                .AntiAliasGridFit;

        using var font =
            new Font(
                "Segoe UI",
                9F,
                FontStyle.Regular,
                GraphicsUnit.Point
            );

        using var brush =
            new SolidBrush(
                BridgeUiTheme.TextMuted
            );

        using var format =
            new StringFormat
            {
                Alignment =
                    StringAlignment.Center,

                LineAlignment =
                    StringAlignment.Center,

                FormatFlags =
                    StringFormatFlags.NoWrap
            };

        graphics.DrawString(
            "—",
            font,
            brush,
            new RectangleF(
                0F,
                0F,
                RankIconPixels,
                RankIconPixels
            ),
            format
        );

        return image;
    }

    private static string FormatPercent(
        double value
    )
    {
        return value.ToString(
            "0.0",
            CultureInfo.InvariantCulture
        ) +
        "%";
    }

    private static Color GetHeroWinRateColor(
        double winRatePercent
    )
    {
        if (winRatePercent >= 55)
        {
            return BridgeUiTheme
                .ServiceCompleted;
        }

        if (winRatePercent < 45)
        {
            return BridgeUiTheme
                .ServiceError;
        }

        return BridgeUiTheme.Text;
    }

    public void AppendLog(
        string message
    )
    {
        RunOnUiThread(
            () =>
            {
                var timestamp =
                    DateTime.Now.ToString(
                        "HH:mm:ss"
                    );

                _logBox.AppendText(
                    $"[{timestamp}] {message}" +
                    Environment.NewLine
                );

                /*
                 * Do not allow the diagnostic
                 * log to grow indefinitely.
                 */
                if (
                    _logBox.TextLength >
                    200_000
                )
                {
                    _logBox.Select(
                        0,
                        50_000
                    );

                    _logBox.SelectedText =
                        String.Empty;

                    /*
                     * RichEdit may retain programmatically deleted text in
                     * its native undo buffer even though TextLength is capped.
                     */
                    _logBox.ClearUndo();
                }

                _logBox.SelectionStart =
                    _logBox.TextLength;

                _logBox.ScrollToCaret();
            }
        );
    }

    /*
     * Called by runtime after full shutdown:
     *
     * - Kestrel stopped;
     * - callback task completed;
     * - SteamAPI.Shutdown() completed.
     */
    public void CloseAfterShutdown()
    {
        RunOnUiThread(
            () =>
            {
                _allowClose =
                    true;

                Close();
            }
        );
    }

    protected override void Dispose(
        bool disposing
    )
    {
        if (disposing)
        {
            Shown -=
                OnModManagementShown;

            _installModButton.Click -=
                OnInstallModButtonClick;

            _activateModButton.Click -=
                OnActivateModButtonClick;

            _serviceStatusControl.SettingsChanged -=
                OnModuleSettingsChanged;

            _alliesMatchGrid.Paint -=
                OnAlliesMatchGridPaint;

            _alliesMatchGrid.CellContentClick -=
                OnMatchGridCellContentClick;

            _enemiesMatchGrid.CellContentClick -=
                OnMatchGridCellContentClick;

            try
            {
                _modManagementCancellation.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }

            _matchIconGeneration +=
                1;

            _matchRankIconGeneration +=
                1;

            _currentMatchDamageTimer.Stop();

            _currentMatchDamageTimer.Tick -=
                OnCurrentMatchDamageTimerTick;

            try
            {
                _currentMatchDamageCancellation.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }

            _currentMatchDamageTimer.Dispose();
            _currentMatchDamageHttpClient.Dispose();
            _currentMatchDamageCancellation.Dispose();
            _currentMatchDamageByAccountId.Clear();
            _heroImageHttpClient.Dispose();
            _rankImageHttpClient.Dispose();
            _modToolTip.Dispose();
            _modManagementCancellation.Dispose();

            ClearHeroIconCache();
            ClearRankIconCache();
            _rankUnavailableIcon.Dispose();
        }

        base.Dispose(
            disposing
        );
    }

    protected override void OnFormClosing(
        FormClosingEventArgs e
    )
    {
        if (
            !_allowClose &&
            ShutdownRequested is not null
        )
        {
            e.Cancel =
                true;

            ShutdownRequested.Invoke(
                this,
                EventArgs.Empty
            );

            return;
        }

        base.OnFormClosing(
            e
        );
    }

    protected override void OnResize(
        EventArgs e
    )
    {
        var previousWindowState =
            _lastObservedWindowState;

        base.OnResize(
            e
        );

        _lastObservedWindowState =
            WindowState;

        if (
            previousWindowState !=
                FormWindowState.Normal &&
            WindowState ==
                FormWindowState.Normal
        )
        {
            _matchContentHeightAdjusted =
                false;

            ScheduleWindowHeightForCurrentContent();
        }
    }

    private Panel CreateHeaderPanel(
        out Label stateValue,
        out Label detailValue
    )
    {
        var panel =
            new Panel
            {
                Dock =
                    DockStyle.Top,

                Height =
                    78,

                Margin =
                    new Padding(
                        0,
                        0,
                        0,
                        14
                    )
            };

        var title =
            new Label
            {
                AutoSize =
                    true,

                Text =
                    "Threat HUD Bridge",

                Font =
                    new Font(
                        "Segoe UI",
                        17F,
                        FontStyle.Bold
                    ),

                ForeColor =
                    Color.White,

                Location =
                    new Point(
                        0,
                        0
                    )
            };

        stateValue =
            new Label
            {
                AutoSize =
                    true,

                Font =
                    new Font(
                        "Segoe UI",
                        10F,
                        FontStyle.Bold
                    ),

                Location =
                    new Point(
                        2,
                        39
                    )
            };

        detailValue =
            new Label
            {
                AutoSize =
                    true,

                ForeColor =
                    BridgeUiTheme.TextMuted,

                Location =
                    new Point(
                        155,
                        41
                    )
            };

        panel.Controls.Add(
            title
        );

        panel.Controls.Add(
            stateValue
        );

        panel.Controls.Add(
            detailValue
        );

        return panel;
    }

    private TableLayoutPanel CreateStatusPanel(
        out Label deadlockValue,
        out Label steamValue,
        out Label httpValue,
        out Label accountIdValue,
        BridgeModuleSettings moduleSettings,
        out BridgeServiceStatusControl
            serviceStatusControl,
        out Button installModButton,
        out Button activateModButton,
        out Label modInstallBlockLabel
    )
    {
        var panel =
            new TableLayoutPanel
            {
                Dock =
                    DockStyle.Top,

                AutoSize =
                    true,

                ColumnCount =
                    4,

                RowCount =
                    4,

                Padding =
                    new Padding(
                        12
                    ),

                Margin =
                    new Padding(
                        0,
                        0,
                        0,
                        14
                    ),

                BackColor =
                    BridgeUiTheme.SurfaceRaised
            };

        panel.ColumnStyles.Add(
            new ColumnStyle(
                SizeType.Absolute,
                145
            )
        );

        panel.ColumnStyles.Add(
            new ColumnStyle(
                SizeType.Absolute,
                170
            )
        );

        panel.ColumnStyles.Add(
            new ColumnStyle(
                SizeType.Percent,
                100F
            )
        );

        panel.ColumnStyles.Add(
            new ColumnStyle(
                SizeType.Absolute,
                315
            )
        );

        deadlockValue =
            AddStatusRow(
                panel,
                0,
                "Deadlock:"
            );

        steamValue =
            AddStatusRow(
                panel,
                1,
                "Steam API:"
            );

        httpValue =
            AddStatusRow(
                panel,
                2,
                "HTTP API:"
            );

        accountIdValue =
            AddStatusRow(
                panel,
                3,
                "Account ID:"
            );

        serviceStatusControl =
            new BridgeServiceStatusControl(
                moduleSettings
            );

        panel.Controls.Add(
            serviceStatusControl,
            2,
            0
        );

        panel.SetRowSpan(
            serviceStatusControl,
            4
        );

        var modControls =
            new TableLayoutPanel
            {
                Dock =
                    DockStyle.Fill,

                AutoSize =
                    true,

                ColumnCount =
                    1,

                RowCount =
                    3,

                BackColor =
                    BridgeUiTheme.SurfaceRaised,

                Margin =
                    new Padding(
                        12,
                        0,
                        0,
                        0
                    ),

                Padding =
                    new Padding(
                        0
                    )
            };

        modControls.RowStyles.Add(
            new RowStyle(
                SizeType.AutoSize
            )
        );

        modControls.RowStyles.Add(
            new RowStyle(
                SizeType.AutoSize
            )
        );

        modControls.RowStyles.Add(
            new RowStyle(
                SizeType.AutoSize
            )
        );

        var modTitle =
            new Label
            {
                AutoSize =
                    true,

                Text =
                    "MOD MANAGEMENT",

                ForeColor =
                    BridgeUiTheme.TextMuted,

                Font =
                    new Font(
                        "Segoe UI",
                        8.25F,
                        FontStyle.Bold,
                        GraphicsUnit.Point
                    ),

                Margin =
                    new Padding(
                        0,
                        0,
                        0,
                        7
                    )
            };

        var modButtons =
            new FlowLayoutPanel
            {
                Dock =
                    DockStyle.Top,

                AutoSize =
                    true,

                AutoSizeMode =
                    AutoSizeMode.GrowAndShrink,

                FlowDirection =
                    FlowDirection.LeftToRight,

                WrapContents =
                    false,

                BackColor =
                    BridgeUiTheme.SurfaceRaised,

                Margin =
                    new Padding(
                        0
                    )
            };

        installModButton =
            BridgeUiTheme.CreateActionButton(
                "Install mod",
                130,
                BridgeActionButtonTone.Blue
            );

        activateModButton =
            BridgeUiTheme.CreateActionButton(
                "Activate mod",
                145,
                BridgeActionButtonTone.Purple
            );

        activateModButton.Margin =
            new Padding(
                10,
                0,
                0,
                0
            );

        modInstallBlockLabel =
            new Label
            {
                AutoSize =
                    true,

                MaximumSize =
                    new Size(
                        285,
                        0
                    ),

                Text =
                    String.Empty,

                ForeColor =
                    BridgeUiTheme.ServiceError,

                BackColor =
                    BridgeUiTheme.SurfaceRaised,

                Font =
                    new Font(
                        "Segoe UI",
                        8.25F,
                        FontStyle.Bold,
                        GraphicsUnit.Point
                    ),

                Margin =
                    new Padding(
                        0,
                        8,
                        0,
                        0
                    ),

                UseMnemonic =
                    false,

                Visible =
                    false
            };

        modButtons.Controls.Add(
            installModButton
        );

        modButtons.Controls.Add(
            activateModButton
        );

        modControls.Controls.Add(
            modTitle,
            0,
            0
        );

        modControls.Controls.Add(
            modButtons,
            0,
            1
        );

        modControls.Controls.Add(
            modInstallBlockLabel,
            0,
            2
        );

        panel.Controls.Add(
            modControls,
            3,
            0
        );

        panel.SetRowSpan(
            modControls,
            4
        );

        return panel;
    }

    private Label AddStatusRow(
        TableLayoutPanel panel,
        int row,
        string title
    )
    {
        panel.RowStyles.Add(
            new RowStyle(
                SizeType.AutoSize
            )
        );

        var titleLabel =
            new Label
            {
                AutoSize =
                    true,

                Text =
                    title,

                ForeColor =
                    BridgeUiTheme.TextMuted,

                Margin =
                    new Padding(
                        0,
                        4,
                        12,
                        4
                    )
            };

        var valueLabel =
            new Label
            {
                AutoSize =
                    true,

                Text =
                    "—",

                ForeColor =
                    BridgeUiTheme.Text,

                Margin =
                    new Padding(
                        0,
                        4,
                        0,
                        4
                    )
            };

        panel.Controls.Add(
            titleLabel,
            0,
            row
        );

        panel.Controls.Add(
            valueLabel,
            1,
            row
        );

        return valueLabel;
    }

    internal static Control CreateMatchPanel(
        out Label statusValue,
        out CurrentMatchLaneStatsPanel
            alliesLaneStatsPanel,
        out DataGridView alliesGrid,
        out DataGridView enemiesGrid,
        string titleText =
            "CURRENT HERO STATS",
        string? titleBadgeText =
            null,
        Color? titleBadgeColor =
            null
    )
    {
        var root =
            new TableLayoutPanel
            {
                Dock =
                    DockStyle.Fill,

                ColumnCount =
                    1,

                RowCount =
                    3,

                Padding =
                    new Padding(
                        16
                    ),

                BackColor =
                    BridgeUiTheme.Surface
            };

        root.RowStyles.Add(
            new RowStyle(
                SizeType.AutoSize
            )
        );

        root.RowStyles.Add(
            new RowStyle(
                SizeType.AutoSize
            )
        );

        root.RowStyles.Add(
            new RowStyle(
                SizeType.Percent,
                100F
            )
        );

        var titleRow =
            new FlowLayoutPanel
            {
                AutoSize =
                    true,

                AutoSizeMode =
                    AutoSizeMode.GrowAndShrink,

                Dock =
                    DockStyle.Fill,

                FlowDirection =
                    FlowDirection.LeftToRight,

                WrapContents =
                    false,

                BackColor =
                    BridgeUiTheme.Surface,

                Margin =
                    new Padding(0)
            };

        var title =
            new Label
            {
                AutoSize =
                    true,

                Text =
                    titleText,

                Font =
                    new Font(
                        "Segoe UI",
                        13F,
                        FontStyle.Bold
                    ),

                ForeColor =
                    Color.White,

                Margin =
                    new Padding(
                        0,
                        0,
                        0,
                        4
                    )
            };

        titleRow.Controls.Add(
            title
        );

        if (
            !string.IsNullOrWhiteSpace(
                titleBadgeText
            )
        )
        {
            titleRow.Controls.Add(
                new Label
                {
                    AutoSize =
                        true,

                    Text =
                        titleBadgeText.Trim(),

                    Font =
                        new Font(
                            "Segoe UI",
                            10F,
                            FontStyle.Bold
                        ),

                    ForeColor =
                        titleBadgeColor ??
                        BridgeUiTheme.Text,

                    Margin =
                        new Padding(
                            12,
                            3,
                            0,
                            4
                        )
                }
            );
        }

        root.Controls.Add(
            titleRow,
            0,
            0
        );

        statusValue =
            new Label
            {
                AutoSize =
                    true,

                Text =
                    "Waiting for current match player stats...",

                ForeColor =
                    BridgeUiTheme.TextMuted,

                Margin =
                    new Padding(
                        0,
                        0,
                        0,
                        12
                    )
            };

        root.Controls.Add(
            statusValue,
            0,
            1
        );

        var teams =
            new TableLayoutPanel
            {
                Dock =
                    DockStyle.Fill,

                ColumnCount =
                    3,

                RowCount =
                    1,

                BackColor =
                    BridgeUiTheme.Surface,

                Margin =
                    new Padding(
                        0
                    )
            };

        teams.ColumnStyles.Add(
            new ColumnStyle(
                SizeType.Absolute,
                CurrentMatchLaneStatsColumnWidth
            )
        );

        teams.ColumnStyles.Add(
            new ColumnStyle(
                SizeType.Percent,
                50F
            )
        );

        teams.ColumnStyles.Add(
            new ColumnStyle(
                SizeType.Percent,
                50F
            )
        );

        var alliesPanel =
            CreateMatchTeamStatsPanel(
                "ALLIES",
                includeCurrentLaneStats:
                    true,
                out alliesGrid,
                out var createdLaneStatsPanel
            );

        alliesLaneStatsPanel =
            createdLaneStatsPanel ??
            throw new InvalidOperationException(
                "The allies lane stats panel was not created."
            );

        alliesPanel.Margin =
            new Padding(
                0,
                0,
                6,
                0
            );

        var enemiesPanel =
            CreateMatchTeamStatsPanel(
                "ENEMIES",
                includeCurrentLaneStats:
                    false,
                out enemiesGrid,
                out _
            );

        enemiesPanel.Margin =
            new Padding(
                6,
                0,
                0,
                0
            );

        teams.Controls.Add(
            alliesPanel,
            0,
            0
        );

        teams.SetColumnSpan(
            alliesPanel,
            2
        );

        teams.Controls.Add(
            enemiesPanel,
            2,
            0
        );

        root.Controls.Add(
            teams,
            0,
            2
        );

        return root;
    }

    private static Control CreateMatchTeamStatsPanel(
        string title,
        bool includeCurrentLaneStats,
        out DataGridView grid,
        out CurrentMatchLaneStatsPanel?
            laneStatsPanel
    )
    {
        var root =
            new TableLayoutPanel
            {
                Dock =
                    DockStyle.Fill,

                ColumnCount =
                    includeCurrentLaneStats
                        ? 2
                        : 1,

                RowCount =
                    2,

                BackColor =
                    BridgeUiTheme.SurfaceRaised,

                Padding =
                    new Padding(
                        8
                    )
            };

        if (includeCurrentLaneStats)
        {
            root.ColumnStyles.Add(
                new ColumnStyle(
                    SizeType.Absolute,
                    CurrentMatchLaneStatsColumnWidth
                )
            );
        }

        root.ColumnStyles.Add(
            new ColumnStyle(
                SizeType.Percent,
                100F
            )
        );

        root.RowStyles.Add(
            new RowStyle(
                SizeType.Absolute,
                28
            )
        );

        root.RowStyles.Add(
            new RowStyle(
                SizeType.Percent,
                100F
            )
        );

        var titleLabel =
            new Label
            {
                Dock =
                    DockStyle.Fill,

                Text =
                    title,

                TextAlign =
                    ContentAlignment.MiddleCenter,

                ForeColor =
                    BridgeUiTheme.TextMuted,

                Font =
                    new Font(
                        "Segoe UI",
                        9F,
                        FontStyle.Bold
                    )
            };

        grid =
            CreateMatchStatsGrid();

        if (includeCurrentLaneStats)
        {
            grid.Paint +=
                OnAlliesMatchGridPaint;
        }

        laneStatsPanel =
            includeCurrentLaneStats
                ? new CurrentMatchLaneStatsPanel(
                    grid
                )
                : null;

        var contentColumn =
            includeCurrentLaneStats
                ? 1
                : 0;

        root.Controls.Add(
            titleLabel,
            contentColumn,
            0
        );

        root.Controls.Add(
            grid,
            contentColumn,
            1
        );

        if (laneStatsPanel is not null)
        {
            laneStatsPanel.Margin =
                new Padding(
                    0,
                    3,
                    3,
                    3
                );

            root.Controls.Add(
                laneStatsPanel,
                0,
                1
            );
        }

        return root;
    }

    private static void OnAlliesMatchGridPaint(
        object? sender,
        PaintEventArgs e
    )
    {
        if (
            sender is not DataGridView grid ||
            grid.Rows.Count <
                CurrentMatchLaneCount *
                2 ||
            grid.ClientSize.Width <= 1
        )
        {
            return;
        }

        using var pairBorderPen =
            new Pen(
                Color.FromArgb(
                    150,
                    BridgeUiTheme.ActionPurple
                ),
                1F
            );

        var dataRight =
            0;

        foreach (
            DataGridViewColumn column in
                grid.Columns
        )
        {
            if (!column.Visible)
            {
                continue;
            }

            var columnBounds =
                grid.GetColumnDisplayRectangle(
                    column.Index,
                    cutOverflow:
                        false
                );

            dataRight =
                Math.Max(
                    dataRight,
                    Math.Min(
                        grid.ClientSize.Width,
                        columnBounds.Right
                    )
                );
        }

        var dataTop =
            grid.ColumnHeadersVisible
                ? grid.ColumnHeadersHeight
                : 0;

        if (
            dataRight <= 1 ||
            dataTop >=
                grid.ClientSize.Height
        )
        {
            return;
        }

        var graphicsState =
            e.Graphics.Save();

        try
        {
            e.Graphics.SetClip(
                Rectangle.FromLTRB(
                    0,
                    dataTop,
                    dataRight,
                    grid.ClientSize.Height
                ),
                CombineMode.Intersect
            );

            for (
                var laneIndex = 0;
                laneIndex <
                    CurrentMatchLaneCount;
                laneIndex++
            )
            {
                var firstRow =
                    grid.GetRowDisplayRectangle(
                        laneIndex *
                            2,
                        cutOverflow:
                            false
                    );

                var secondRow =
                    grid.GetRowDisplayRectangle(
                        laneIndex *
                            2 +
                            1,
                        cutOverflow:
                            false
                    );

                var top =
                    Math.Min(
                        firstRow.Top,
                        secondRow.Top
                    );

                var bottom =
                    Math.Max(
                        firstRow.Bottom,
                        secondRow.Bottom
                    );

                if (
                    bottom <= dataTop ||
                    top >=
                        grid.ClientSize.Height ||
                    bottom <= top
                )
                {
                    continue;
                }

                var pairBorder =
                    Rectangle.FromLTRB(
                        0,
                        top,
                        dataRight -
                            1,
                        bottom -
                            1
                    );

                if (
                    pairBorder.Width > 0 &&
                    pairBorder.Height > 0
                )
                {
                    e.Graphics.DrawRectangle(
                        pairBorderPen,
                        pairBorder
                    );
                }
            }
        }
        finally
        {
            e.Graphics.Restore(
                graphicsState
            );
        }
    }

    private static DataGridView CreateMatchStatsGrid()
    {
        var grid =
            new FlickerFreeDataGridView
            {
                Dock =
                    DockStyle.Fill,

                ReadOnly =
                    true,

                AllowUserToAddRows =
                    false,

                AllowUserToDeleteRows =
                    false,

                AllowUserToResizeRows =
                    false,

                AllowUserToResizeColumns =
                    false,

                AllowUserToOrderColumns =
                    false,

                MultiSelect =
                    false,

                RowHeadersVisible =
                    false,

                ColumnHeadersVisible =
                    true,

                EnableHeadersVisualStyles =
                    false,

                BorderStyle =
                    BorderStyle.None,

                CellBorderStyle =
                    DataGridViewCellBorderStyle.SingleHorizontal,

                ColumnHeadersBorderStyle =
                    DataGridViewHeaderBorderStyle.None,

                ColumnHeadersHeight =
                    32,

                ColumnHeadersHeightSizeMode =
                    DataGridViewColumnHeadersHeightSizeMode
                        .DisableResizing,

                GridColor =
                    BridgeUiTheme.Border,

                BackgroundColor =
                    BridgeUiTheme.SurfaceRaised,

                SelectionMode =
                    DataGridViewSelectionMode.FullRowSelect,

                AutoSizeColumnsMode =
                    DataGridViewAutoSizeColumnsMode.Fill,

                AutoSizeRowsMode =
                    DataGridViewAutoSizeRowsMode.None,

                RowTemplate =
                {
                    Height =
                        44
                }
            };

        grid.ColumnHeadersDefaultCellStyle =
            new DataGridViewCellStyle
            {
                BackColor =
                    BridgeUiTheme.Surface,

                ForeColor =
                    BridgeUiTheme.TextMuted,

                SelectionBackColor =
                    BridgeUiTheme.Surface,

                SelectionForeColor =
                    BridgeUiTheme.TextMuted,

                Font =
                    new Font(
                        "Segoe UI",
                        8F,
                        FontStyle.Bold
                    ),

                Alignment =
                    DataGridViewContentAlignment.MiddleCenter,

                WrapMode =
                    DataGridViewTriState.False
            };

        grid.DefaultCellStyle =
            new DataGridViewCellStyle
            {
                BackColor =
                    BridgeUiTheme.SurfaceRaised,

                ForeColor =
                    BridgeUiTheme.Text,

                SelectionBackColor =
                    BridgeUiTheme.SurfaceRaised,

                SelectionForeColor =
                    BridgeUiTheme.Text,

                Font =
                    new Font(
                        "Segoe UI",
                        8.5F,
                        FontStyle.Regular
                    ),

                Alignment =
                    DataGridViewContentAlignment.MiddleCenter
            };

        var heroColumn =
            new DataGridViewImageColumn
            {
                Name =
                    "Hero",

                HeaderText =
                    "HERO",

                Width =
                    48,

                MinimumWidth =
                    48,

                AutoSizeMode =
                    DataGridViewAutoSizeColumnMode.None,

                ImageLayout =
                    DataGridViewImageCellLayout.Zoom,

                DefaultCellStyle =
                    new DataGridViewCellStyle
                    {
                        NullValue =
                            null,

                        Alignment =
                            DataGridViewContentAlignment.MiddleCenter
                    },

                ToolTipText =
                    "Current hero",

                SortMode =
                    DataGridViewColumnSortMode.NotSortable
            };

        grid.Columns.Add(
            heroColumn
        );

        var rankColumn =
            new DataGridViewImageColumn
            {
                Name =
                    "Rank",

                HeaderText =
                    "RANK",

                Width =
                    44,

                MinimumWidth =
                    44,

                AutoSizeMode =
                    DataGridViewAutoSizeColumnMode.None,

                ImageLayout =
                    DataGridViewImageCellLayout.Normal,

                DefaultCellStyle =
                    new DataGridViewCellStyle
                    {
                        NullValue =
                            null,

                        Alignment =
                            DataGridViewContentAlignment.MiddleCenter
                    },

                ToolTipText =
                    "Lifetime player rank",

                SortMode =
                    DataGridViewColumnSortMode.NotSortable
            };

        grid.Columns.Add(
            rankColumn
        );

        AddMatchTextColumn(
            grid,
            "WinRate",
            "WR",
            0.72F,
            "Lifetime win rate with the current hero"
        );

        AddMatchTextColumn(
            grid,
            "Damage",
            "DMG",
            1.1F,
            "Hero damage in the current match. " +
            "Icon: Screen impact by Lorc / " +
            "Game-icons.net (CC BY 3.0)."
        );

        AddMatchTextColumn(
            grid,
            "SoulsPerMinute",
            "SPM",
            0.9F,
            "Average souls per minute"
        );

        AddMatchTextColumn(
            grid,
            "Headshots",
            "HS%",
            0.82F,
            "Critical/headshot rate against heroes"
        );

        AddMatchTextColumn(
            grid,
            "Accuracy",
            "ACC%",
            0.9F,
            "Average shooting accuracy"
        );

        /*
         * The renderer is retained by the DataGridView event delegates and
         * releases its bitmaps when the grid is disposed. CreateMatchPanel is
         * shared by the live match and match-history details windows, so both
         * views always receive the same header icons.
         */
        _ =
            new MatchStatsHeaderIconRenderer(
                grid
            );

        return grid;
    }

    private static void AddMatchTextColumn(
        DataGridView grid,
        string name,
        string headerText,
        float fillWeight,
        string toolTipText
    )
    {
        grid.Columns.Add(
            new DataGridViewTextBoxColumn
            {
                Name =
                    name,

                HeaderText =
                    headerText,

                FillWeight =
                    fillWeight,

                MinimumWidth =
                    46,

                ToolTipText =
                    toolTipText,

                SortMode =
                    DataGridViewColumnSortMode.NotSortable
            }
        );
    }

    private static Dictionary<string, Bitmap>
        CreateMatchStatsHeaderIcons()
    {
        var icons =
            new Dictionary<string, Bitmap>(
                StringComparer.Ordinal
            );

        try
        {
            icons.Add(
                "Hero",
                CreateMatchStatsHeaderIcon(
                    MatchHeaderPersonFillPngBase64
                )
            );

            icons.Add(
                "Rank",
                CreateMatchStatsHeaderIcon(
                    MatchHeaderAwardFillPngBase64
                )
            );

            icons.Add(
                "WinRate",
                CreateMatchStatsHeaderIcon(
                    MatchHeaderTrophyFillPngBase64
                )
            );

            icons.Add(
                "Damage",
                CreateMatchStatsHeaderIcon(
                    MatchHeaderScreenImpactPngBase64
                )
            );

            icons.Add(
                "SoulsPerMinute",
                CreateMatchStatsHeaderIcon(
                    MatchHeaderCashStackPngBase64
                )
            );

            icons.Add(
                "Headshots",
                CreateMatchStatsHeaderIcon(
                    MatchHeaderHeadshotPngBase64
                )
            );

            icons.Add(
                "Accuracy",
                CreateMatchStatsHeaderIcon(
                    MatchHeaderBullseyePngBase64
                )
            );

            return icons;
        }
        catch
        {
            foreach (var icon in icons.Values)
            {
                icon.Dispose();
            }

            throw;
        }
    }

    private static Bitmap CreateMatchStatsHeaderIcon(
        string pngBase64
    )
    {
        var bytes =
            Convert.FromBase64String(
                pngBase64
            );

        using var stream =
            new MemoryStream(
                bytes,
                writable:
                    false
            );

        using var sourceImage =
            Image.FromStream(
                stream,
                useEmbeddedColorManagement:
                    false,
                validateImageData:
                    true
            );

        if (
            sourceImage.Width <= 0 ||
            sourceImage.Height <= 0 ||
            sourceImage.Width > 64 ||
            sourceImage.Height > 64
        )
        {
            throw new InvalidDataException(
                "Embedded match header icon has invalid dimensions."
            );
        }

        var renderedIcon =
            new Bitmap(
                MatchStatsHeaderIconPixels,
                MatchStatsHeaderIconPixels,
                PixelFormat.Format32bppPArgb
            );

        var scale =
            Math.Min(
                MatchStatsHeaderIconPixels /
                    (float)sourceImage.Width,

                MatchStatsHeaderIconPixels /
                    (float)sourceImage.Height
            );

        var drawWidth =
            Math.Max(
                1,
                (int)Math.Round(
                    sourceImage.Width *
                        scale,
                    MidpointRounding.AwayFromZero
                )
            );

        var drawHeight =
            Math.Max(
                1,
                (int)Math.Round(
                    sourceImage.Height *
                        scale,
                    MidpointRounding.AwayFromZero
                )
            );

        var destination =
            new Rectangle(
                (
                    MatchStatsHeaderIconPixels -
                    drawWidth
                ) /
                    2,

                (
                    MatchStatsHeaderIconPixels -
                    drawHeight
                ) /
                    2,

                drawWidth,
                drawHeight
            );

        try
        {
            using var graphics =
                Graphics.FromImage(
                    renderedIcon
                );

            graphics.Clear(
                Color.Transparent
            );

            graphics.CompositingMode =
                CompositingMode.SourceOver;

            graphics.CompositingQuality =
                CompositingQuality.HighQuality;

            graphics.InterpolationMode =
                InterpolationMode.HighQualityBicubic;

            graphics.PixelOffsetMode =
                PixelOffsetMode.HighQuality;

            graphics.SmoothingMode =
                SmoothingMode.HighQuality;

            using var imageAttributes =
                new ImageAttributes();

            imageAttributes.SetColorMatrix(
                new ColorMatrix
                {
                    Matrix00 =
                        BridgeUiTheme.TextMuted.R /
                        255F,

                    Matrix11 =
                        BridgeUiTheme.TextMuted.G /
                        255F,

                    Matrix22 =
                        BridgeUiTheme.TextMuted.B /
                        255F,

                    Matrix33 =
                        BridgeUiTheme.TextMuted.A /
                        255F,

                    Matrix44 =
                        1F
                },
                ColorMatrixFlag.Default,
                ColorAdjustType.Bitmap
            );

            graphics.DrawImage(
                sourceImage,
                destination,
                0,
                0,
                sourceImage.Width,
                sourceImage.Height,
                GraphicsUnit.Pixel,
                imageAttributes
            );

            return renderedIcon;
        }
        catch
        {
            renderedIcon.Dispose();
            throw;
        }
    }

    private sealed class MatchStatsHeaderIconRenderer :
        IDisposable
    {
        private readonly DataGridView _grid;

        private readonly Dictionary<string, Bitmap>
            _icons;

        private readonly Pen _headerSeparatorPen;

        private bool _disposed;

        public MatchStatsHeaderIconRenderer(
            DataGridView grid
        )
        {
            _grid =
                grid ??
                throw new ArgumentNullException(
                    nameof(grid)
                );

            _icons =
                CreateMatchStatsHeaderIcons();

            _headerSeparatorPen =
                new Pen(
                    BridgeUiTheme.Border
                );

            _grid.CellPainting +=
                OnCellPainting;

            _grid.Disposed +=
                OnGridDisposed;
        }

        private void OnCellPainting(
            object? sender,
            DataGridViewCellPaintingEventArgs e
        )
        {
            if (
                _disposed ||
                !ReferenceEquals(
                    sender,
                    _grid
                ) ||
                e.RowIndex != -1 ||
                e.ColumnIndex < 0 ||
                e.ColumnIndex >=
                    _grid.Columns.Count ||
                !_icons.TryGetValue(
                    _grid.Columns[
                        e.ColumnIndex
                    ].Name,
                    out var icon
                )
            )
            {
                return;
            }

            var graphics =
                e.Graphics;

            if (graphics is null)
            {
                return;
            }

            e.PaintBackground(
                e.CellBounds,
                false
            );

            graphics.DrawLine(
                _headerSeparatorPen,
                e.CellBounds.Left,
                e.CellBounds.Bottom -
                    1,
                e.CellBounds.Right,
                e.CellBounds.Bottom -
                    1
            );

            graphics.DrawImageUnscaled(
                icon,
                e.CellBounds.Left +
                    (
                        e.CellBounds.Width -
                        icon.Width
                    ) /
                    2,
                e.CellBounds.Top +
                    (
                        e.CellBounds.Height -
                        icon.Height
                    ) /
                    2
            );

            e.Handled =
                true;
        }

        private void OnGridDisposed(
            object? sender,
            EventArgs e
        )
        {
            Dispose();
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed =
                true;

            _grid.CellPainting -=
                OnCellPainting;

            _grid.Disposed -=
                OnGridDisposed;

            foreach (var icon in _icons.Values)
            {
                icon.Dispose();
            }

            _icons.Clear();
            _headerSeparatorPen.Dispose();
        }
    }

    /*
     * Embedded monochrome header artwork. See THIRD-PARTY-NOTICES.txt.
     *
     * Bootstrap Icons (MIT): person-fill, award-fill, trophy-fill,
     * cash-stack and bullseye.
     *
     * Game-icons.net (CC BY 3.0): "Screen impact" by Lorc. Modified by
     * rasterizing, resizing and recoloring for the UI.
     *
     * The headshot target is original project artwork, optically simplified
     * for a readable 16x16 rendering.
     */
    private const string MatchHeaderPersonFillPngBase64 =
        "iVBORw0KGgoAAAANSUhEUgAAABAAAAAQCAYAAAAf8/9hAAAACXBIWXMAAA7DAAAOwwHHb6hkAAAAGXRFWHRTb2Z0d2FyZQB3d3cuaW5rc2NhcGUub3Jnm+48GgAAAJxJREFUOI2tkTEKwkAQRV8Em/RioTfKsfQG2UNovJMsCFvYW3+bSWEyG0bjgw/LZ2bg/20ksYbNqu2FAy2QgGJK5s2R5KnXnN6bbSodPIGd4+2jEcLUDlwdb3AnKx20kpKkYkrmhTv4OUIDdMAFyMDLlM3rliJsJd2c75sy2CySPg6cA8sjJ6+DB3AMRi/AYdpBDi4D3MfH33/ha95pWPptV/aqZQAAAABJRU5ErkJggg==";

    private const string MatchHeaderAwardFillPngBase64 =
        "iVBORw0KGgoAAAANSUhEUgAAABAAAAAQCAYAAAAf8/9hAAAACXBIWXMAAA7DAAAOwwHHb6hkAAAAGXRFWHRTb2Z0d2FyZQB3d3cuaW5rc2NhcGUub3Jnm+48GgAAALRJREFUOI2tkj0KwkAQhV+CSK5hE0EPkC7iVQPiKUQ8hp2FhUcIRPgsnJhNdk3WnwfDLPN+itkRoEDNgRI4WJU287Qh8x5o8NEYNxpQBIxDFO8CcuAaEXAD1qGAKsLcomp9qTqcFY+XNh1TxcANWH7g67RfLnEVWuLP3/iXQxKwADL8U86MmwwAqIEdz73k9q6N6+kTYLhhd3C3PnNmiSt2iRCm+KDgIukk6WhdkjaSttZ7eABSRTGsUsBCFAAAAABJRU5ErkJggg==";

    private const string MatchHeaderTrophyFillPngBase64 =
        "iVBORw0KGgoAAAANSUhEUgAAABAAAAAQCAYAAAAf8/9hAAAACXBIWXMAAA7DAAAOwwHHb6hkAAAAGXRFWHRTb2Z0d2FyZQB3d3cuaW5rc2NhcGUub3Jnm+48GgAAAPVJREFUOI2Vk7FOAkEURQ9DYqzAwlj7E2rBz2AjmlDQ2WIkxNJEP0Dj59hQQiiMiQ2EClrxWLCrm2XZHW7yksnk3TN3XmZQSaprvLqpL/CvJvFq/K3UujpUF3skWKgDNdTUAXAKvAOPkQl6wAUwRf1UG+r5HgnO1CP1IwACdWACrCNOXwNj2MwvAK/AE3AAjCIAI+AQeAZeUIN6r36pq4j4q6T3Tg3pG0jrWF1WmE+ynjwA9boEcJXvLwKg3qjzjHGudop6dwFQ+xlAf1dfqJh4tQqoLfVNnWUSzJK9VtkVauqt+l0yxB/1wc3/2QJclhjzaqe+X+FhfbsjxlT7AAAAAElFTkSuQmCC";

    private const string MatchHeaderScreenImpactPngBase64 =
        "iVBORw0KGgoAAAANSUhEUgAAABAAAAAQCAYAAAAf8/9hAAAACXBIWXMAAAB2AAAAdgFOeyYIAAAAGXRFWHRTb2Z0d2FyZQB3d3cuaW5rc2NhcGUub3Jnm+48GgAAAX9JREFUOI11kztIHVEQhr+7CiYRH4hdmgTtRUgnAR+EgK2IjUkXWxsb67QWKilshMRG0CZBEovYWYaAhBSCSgIRmxS+Mcq9finurB6WvQPLmZ2Z/f/5z8xW1Aogja0FuAYeAP+KySyengYf9wGT4b8oK8iAGvAUeFySnwDeBEF/KYWKmqkLarM6oXZEfM+6fVGn1Sm1O3KoVPRO/ktgCLgBXgFzwLuE6y/wEGgnvbMUTf2qrtvYvhXqyQKnCVgCVoGxEqXncf6McxjoLXawpNbUarD9UW/D31Av1AF1Sz1XH6UdEOxZdANwBKwAv4AD4COwDowAn4EroDUF2AYOgWPgPXABzAIL1BfpGNhNJM0D1eIlzkXLazHSNDeTSDpTx9XOvINF4DvwPN7HgeWkuwy45H6V26iP+jRH71J/BPqt+kkdTthfqwfqftQc5guVFzyLGRftibqpjqq/I1ZTB3PwVGNXjCu1ncQ/ifOtDVYZoBL6O0uWCeo/3gegmgf+A3l3uB9W3D+NAAAAAElFTkSuQmCC";

    private const string MatchHeaderCashStackPngBase64 =
        "iVBORw0KGgoAAAANSUhEUgAAABAAAAAQCAYAAAAf8/9hAAAACXBIWXMAAA7DAAAOwwHHb6hkAAAAGXRFWHRTb2Z0d2FyZQB3d3cuaW5rc2NhcGUub3Jnm+48GgAAAHVJREFUOI2tkkkKgDAQBFtfp3gSf2zwI8blGeXFkEFQYpKGvqVrMksDqERtUdoAFkn8tJOkpkYLOdXjL4haASXah5BMuPsB6ANEBvT0BBzADowvbz4Bp2lvfwMU30G1Fvw9mNQhDsBmAWETWWt05GuucspFugArd9bSiM/kqAAAAABJRU5ErkJggg==";

    private const string MatchHeaderHeadshotPngBase64 =
        "iVBORw0KGgoAAAANSUhEUgAAABAAAAAQCAQAAAC1+jfqAAAAIGNIUk0AAHomAACAhAAA+gAAAIDoAAB1MAAA6mAAADqYAAAXcJy6UTwAAAACYktHRAD/h4/MvwAAAAlwSFlzAAAOwwAADsMBx2+oZAAAAAd0SU1FB+oJAxA5NFe4haQAAAEhSURBVCjPhdGxahQBAITh2V05wtokVjZpvX0GIZJCm7yD9mkt4kJA38B06S/3DGefCFfkFSy8WwhyXjBwEEMId5/FQiwsnHZgmJm/kBSRMkWSdaZJXqZKIpsUEYUoVWKotbTUGopKKYqIUjw1RmdlpcNY3Tt9Qu3SL/vi2LHYd+NS3SdU4szCtmdOTU2d2rFtYSSqiAavbZljY4PvtrxBIxGtTnzCytraCh9Fp5U4t8ChL66UvrpQujJxiIXzJ+kldxlkNw9JdjPIXTw6WnPxDpwZgbei86Hv0OBAnODePU7EAYbSzxz5aWDPzMTEzJ6BZT+zUETqXOR5PudFdpLc5Fve50de5Xcer66NMXPr1vzfq3tYjSPXrh1p/sIq/of7D+tvLlnCUHKWAAAAGXRFWHRTb2Z0d2FyZQB3d3cuaW5rc2NhcGUub3Jnm+48GgAAAABJRU5ErkJggg==";

    private const string MatchHeaderBullseyePngBase64 =
        "iVBORw0KGgoAAAANSUhEUgAAABAAAAAQCAYAAAAf8/9hAAAACXBIWXMAAA7DAAAOwwHHb6hkAAAAGXRFWHRTb2Z0d2FyZQB3d3cuaW5rc2NhcGUub3Jnm+48GgAAAZ1JREFUOI2Vk01LVWEUhZ97LSNKyLgWwp0108w+oA/0FzRsEoLmOPIXNAgaZ2EoGVTDJoXRTMVmklKgQVf7ARVp5iR1VIPHyTp6uJNyw+LAe/berL3X2qg0oUd9rK6oO8GKOqaebc6vsh9HgCfALLABDAKngyFgE5gDJoDWoqhaKp4GasA5YBt4CvwMJoHfQE8aTu81CZVJ9ZVaV5fVZ+pl9VhwRX2Rf3X1tTquUsz8Q62pn9SBFM+q28GMekkdTJMOdU3trqhjwK/QPg+MAvPAPeBNRrwB3Af6gbvAR6AdaEddVXvVD+pV9a06oraot4KWvE2pfeqiekFtEIrH8z2qboTisPsxrJ5S17OTLbVN3aoCAhUOHhXAQ8A34AzwBegFFoCbkc4kvwRuA++zp9XUfC2WuBmdLwIP/nOJJ4ETxJ5rkXH5HzIONcnYVRhpIuaoq0vq8yhSGOlajLSUnKncy54TW9V3aVKLZIslBgt560jxnHq4fEx/gOvAOtCISe4AncFIZv4MfE/u3/ItlNGtPlIbJQYN9aHa1Zy/C7vjH4RWTcTnAAAAAElFTkSuQmCC";

    private RichTextBox CreateLogBox()
    {
        return new RichTextBox
        {
            Dock =
                DockStyle.Fill,

            ReadOnly =
                true,

            BorderStyle =
                BorderStyle.None,

            BackColor =
                BridgeUiTheme.Surface,

            ForeColor =
                Color.Gainsboro,

            Font =
                new Font(
                    "Consolas",
                    9F,
                    FontStyle.Regular
                ),

            DetectUrls =
                false,

            WordWrap =
                false,

            Margin =
                new Padding(
                    0
                )
        };
    }

    private FlowLayoutPanel CreateFooter()
    {
        var panel =
            new FlowLayoutPanel
            {
                Dock =
                    DockStyle.Fill,

                AutoSize =
                    true,

                FlowDirection =
                    FlowDirection.RightToLeft,

                WrapContents =
                    false,

                Padding =
                    new Padding(
                        0,
                        12,
                        0,
                        0
                    )
            };

        var closeButton =
            BridgeUiTheme.CreateButton(
                "Close",
                110
            );

        closeButton.Click +=
            (_, _) =>
                Close();

        panel.Controls.Add(
            closeButton
        );

        return panel;
    }

    private void ApplySelectedSectionVisibility()
    {
        var matchSelected =
            _selectedSection ==
                MainSection.Match;

        var reactionsSelected =
            _selectedSection ==
                MainSection.Reactions;

        var historySelected =
            _selectedSection ==
                MainSection.History;

        var logSelected =
            _selectedSection ==
                MainSection.Log;

        _contentFrame.Visible =
            true;

        _matchPanel.Visible =
            matchSelected &&
            _hasCurrentMatch;

        _noActiveMatchLabel.Visible =
            matchSelected &&
            !_hasCurrentMatch;

        _reactionList.Visible =
            reactionsSelected;

        _matchHistoryList.Visible =
            historySelected;

        _logBox.Visible =
            logSelected;

        if (
            matchSelected &&
            _hasCurrentMatch
        )
        {
            _matchPanel.BringToFront();
        }
        else if (
            matchSelected &&
            !_hasCurrentMatch
        )
        {
            _noActiveMatchLabel
                .BringToFront();
        }
        else if (reactionsSelected)
        {
            _reactionList.BringToFront();
        }
        else if (historySelected)
        {
            _matchHistoryList.BringToFront();
        }
        else if (logSelected)
        {
            _logBox.BringToFront();
        }
    }

    private void ScheduleWindowHeightForCurrentContent()
    {
        var generation =
            ++_windowContentLayoutGeneration;

        if (
            IsDisposed ||
            Disposing ||
            !IsHandleCreated
        )
        {
            return;
        }

        BeginInvoke(
            (Action)(
                () =>
                {
                    if (
                        generation !=
                            _windowContentLayoutGeneration ||
                        IsDisposed ||
                        Disposing
                    )
                    {
                        return;
                    }

                    AdjustWindowHeightForCurrentContent();
                }
            )
        );
    }

    private void AdjustWindowHeightForCurrentContent()
    {
        if (
            WindowState !=
                FormWindowState.Normal
        )
        {
            return;
        }

        if (
            _selectedSection !=
                MainSection.Match
        )
        {
            SetAutomaticWindowHeight(
                StandardContentWindowHeight,
                allowShrink:
                    false
            );

            return;
        }

        if (!_hasCurrentMatch)
        {
            SetAutomaticWindowHeight(
                CompactMatchWindowHeight,
                allowShrink:
                    true
            );

            return;
        }

        if (
            _alliesMatchGrid.Rows.Count !=
                6 ||
            _enemiesMatchGrid.Rows.Count !=
                6
        )
        {
            SetAutomaticWindowHeight(
                StandardContentWindowHeight,
                allowShrink:
                    false
            );

            return;
        }

        if (_matchContentHeightAdjusted)
        {
            return;
        }

        PerformLayout();
        _matchPanel.PerformLayout();
        _alliesMatchGrid.PerformLayout();
        _enemiesMatchGrid.PerformLayout();

        var missingAlliesHeight =
            GetMissingMatchGridHeight(
                _alliesMatchGrid
            );

        var missingEnemiesHeight =
            GetMissingMatchGridHeight(
                _enemiesMatchGrid
            );

        var missingHeight =
            Math.Max(
                missingAlliesHeight,
                missingEnemiesHeight
            );

        var desiredHeight =
            Math.Max(
                StandardContentWindowHeight,
                Height +
                    missingHeight
            );

        SetAutomaticWindowHeight(
            desiredHeight,
            allowShrink:
                false
        );

        _matchContentHeightAdjusted =
            true;
    }

    private static int GetMissingMatchGridHeight(
        DataGridView grid
    )
    {
        var requiredHeight =
            grid.ColumnHeadersVisible
                ? grid.ColumnHeadersHeight
                : 0;

        requiredHeight +=
            grid.Rows.GetRowsHeight(
                DataGridViewElementStates.Visible
            );

        requiredHeight +=
            MatchGridHeightSafetyMargin;

        return Math.Max(
            0,
            requiredHeight -
                grid.ClientSize.Height
        );
    }

    private void SetAutomaticWindowHeight(
        int requestedHeight,
        bool allowShrink
    )
    {
        var workingArea =
            Screen.FromControl(
                this
            )
                .WorkingArea;

        var workingMinimumHeight =
            Math.Min(
                CompactMatchWindowHeight,
                workingArea.Height
            );

        if (
            MinimumSize.Height !=
                workingMinimumHeight
        )
        {
            MinimumSize =
                new Size(
                    MinimumSize.Width,
                    workingMinimumHeight
                );
        }

        var minimumHeight =
            MinimumSize.Height;

        var boundedRequestedHeight =
            Math.Min(
                workingArea.Height,
                Math.Max(
                    minimumHeight,
                    requestedHeight
                )
            );

        var targetHeight =
            allowShrink ||
            Height >
                workingArea.Height
                ? boundedRequestedHeight
                : Math.Max(
                    Height,
                    boundedRequestedHeight
                );

        var targetLeft =
            Width <=
                workingArea.Width
                ? Math.Clamp(
                    Left,
                    workingArea.Left,
                    workingArea.Right -
                        Width
                )
                : workingArea.Left;

        var targetTop =
            Math.Clamp(
                Top,
                workingArea.Top,
                workingArea.Bottom -
                    targetHeight
            );

        if (
            targetHeight ==
                Height &&
            targetLeft ==
                Left &&
            targetTop ==
                Top
        )
        {
            return;
        }

        SetBounds(
            targetLeft,
            targetTop,
            Width,
            targetHeight
        );
    }

    private void RunOnUiThread(
        Action action
    )
    {
        if (
            IsDisposed ||
            Disposing
        )
        {
            return;
        }

        if (InvokeRequired)
        {
            if (!IsHandleCreated)
            {
                return;
            }

            BeginInvoke(
                action
            );

            return;
        }

        action();
    }

    private sealed class FlickerFreeDataGridView :
        DataGridView
    {
        public FlickerFreeDataGridView()
        {
            DoubleBuffered =
                true;

            SetStyle(
                ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer,
                true
            );
        }
    }

    internal sealed class CurrentMatchLaneStatsPanel :
        Control
    {
        private readonly DataGridView _grid;

        private readonly CurrentMatchLaneDisplayValue?[]
            _laneValues =
                new CurrentMatchLaneDisplayValue?[
                    CurrentMatchLaneCount
                ];

        private readonly Font _metricFont;

        private readonly Font _metricValueFont;

        private readonly Font _metricLabelFont;

        public CurrentMatchLaneStatsPanel(
            DataGridView grid
        )
        {
            _grid =
                grid ??
                throw new ArgumentNullException(
                    nameof(grid)
                );

            _metricFont =
                new Font(
                    "Segoe UI",
                    8F,
                    FontStyle.Bold,
                    GraphicsUnit.Point
                );

            _metricLabelFont =
                new Font(
                    "Segoe UI",
                    6F,
                    FontStyle.Bold,
                    GraphicsUnit.Point
                );

            _metricValueFont =
                new Font(
                    "Segoe UI",
                    6.5F,
                    FontStyle.Bold,
                    GraphicsUnit.Point
                );

            Dock =
                DockStyle.Fill;

            BackColor =
                BridgeUiTheme.SurfaceRaised;

            ForeColor =
                BridgeUiTheme.Text;

            TabStop =
                false;

            AccessibleName =
                "Current lane statistics";

            SetStyle(
                ControlStyles.UserPaint |
                ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.ResizeRedraw,
                true
            );

            SetStyle(
                ControlStyles.Selectable,
                false
            );

            _grid.Layout +=
                OnGridLayout;

            _grid.Scroll +=
                OnGridScroll;
        }

        public void SetLaneStats(
            IReadOnlyList<
                CurrentMatchLaneDisplayValue
            > values
        )
        {
            ArgumentNullException.ThrowIfNull(
                values
            );

            if (
                values.Count !=
                    CurrentMatchLaneCount
            )
            {
                throw new ArgumentException(
                    "Current lane display requires exactly " +
                    CurrentMatchLaneCount +
                    " values.",
                    nameof(values)
                );
            }

            var changed =
                false;

            for (
                var index = 0;
                index <
                    CurrentMatchLaneCount;
                index++
            )
            {
                if (
                    _laneValues[index] is { } currentValue &&
                    currentValue.Equals(
                        values[index]
                    )
                )
                {
                    continue;
                }

                _laneValues[index] =
                    values[index];

                changed =
                    true;
            }

            if (changed)
            {
                Invalidate();
            }
        }

        public void ClearLaneStats()
        {
            if (
                !_laneValues.Any(
                    value =>
                        value.HasValue
                )
            )
            {
                return;
            }

            Array.Clear(
                _laneValues,
                0,
                _laneValues.Length
            );

            Invalidate();
        }

        protected override void OnPaint(
            PaintEventArgs e
        )
        {
            ArgumentNullException.ThrowIfNull(
                e
            );

            e.Graphics.Clear(
                BackColor
            );

            using var laneBackground =
                new SolidBrush(
                    BridgeUiTheme.Surface
                );

            using var headerBackground =
                new SolidBrush(
                    BridgeUiTheme.Surface
                );

            using var borderPen =
                new Pen(
                    BridgeUiTheme.Border
                );

            using var accentPen =
                new Pen(
                    BridgeUiTheme.ActionPurple,
                    3F
                );

            if (
                ClientSize.Width > 1 &&
                _grid.Rows.Count >=
                    CurrentMatchLaneCount *
                    2
            )
            {
                for (
                    var laneIndex = 0;
                    laneIndex <
                        CurrentMatchLaneCount;
                    laneIndex++
                )
                {
                    var firstRow =
                        _grid.GetRowDisplayRectangle(
                            laneIndex *
                                2,
                            cutOverflow:
                                false
                        );

                    var secondRow =
                        _grid.GetRowDisplayRectangle(
                            laneIndex *
                                2 +
                                1,
                            cutOverflow:
                                false
                        );

                    var blockTop =
                        Math.Min(
                            firstRow.Top,
                            secondRow.Top
                        );

                    var blockBottom =
                        Math.Max(
                            firstRow.Bottom,
                            secondRow.Bottom
                        );

                    if (
                        blockBottom <= 0 ||
                        blockTop >=
                            ClientSize.Height ||
                        blockBottom <=
                            blockTop
                    )
                    {
                        continue;
                    }

                    var block =
                        Rectangle.FromLTRB(
                            0,
                            blockTop,
                            ClientSize.Width,
                            blockBottom
                        );

                    e.Graphics.FillRectangle(
                        laneBackground,
                        block
                    );

                    var border =
                        new Rectangle(
                            block.Left,
                            block.Top,
                            Math.Max(
                                0,
                                block.Width -
                                    1
                            ),
                            Math.Max(
                                0,
                                block.Height -
                                    1
                            )
                        );

                    if (
                        border.Width > 0 &&
                        border.Height > 0
                    )
                    {
                        e.Graphics.DrawRectangle(
                            borderPen,
                            border
                        );

                        e.Graphics.DrawLine(
                            accentPen,
                            border.Left +
                                2,
                            border.Top +
                                2,
                            border.Left +
                                2,
                            border.Bottom -
                                1
                        );
                    }

                    DrawLaneMetrics(
                        e.Graphics,
                        block,
                        _laneValues[
                            laneIndex
                        ]
                    );
                }
            }

            var headerHeight =
                _grid.ColumnHeadersVisible
                    ? Math.Min(
                        _grid.ColumnHeadersHeight,
                        ClientSize.Height
                    )
                    : 0;

            if (
                headerHeight > 0 &&
                ClientSize.Width > 0
            )
            {
                var header =
                    new Rectangle(
                        0,
                        0,
                        ClientSize.Width,
                        headerHeight
                    );

                e.Graphics.FillRectangle(
                    headerBackground,
                    header
                );

                TextRenderer.DrawText(
                    e.Graphics,
                    "LANE",
                    _metricFont,
                    header,
                    BridgeUiTheme.TextMuted,
                    TextFormatFlags.HorizontalCenter |
                    TextFormatFlags.VerticalCenter |
                    TextFormatFlags.SingleLine |
                    TextFormatFlags.NoPadding
                );

                e.Graphics.DrawLine(
                    borderPen,
                    header.Left,
                    header.Bottom -
                        1,
                    header.Right,
                    header.Bottom -
                        1
                );
            }
        }

        private void DrawLaneMetrics(
            Graphics graphics,
            Rectangle block,
            CurrentMatchLaneDisplayValue? value
        )
        {
            const int labelHeight =
                13;

            const int valueHeight =
                18;

            const int matchesHeight =
                15;

            var contentHeight =
                labelHeight +
                valueHeight +
                matchesHeight;

            var contentTop =
                block.Top +
                (
                    block.Height -
                    contentHeight
                ) /
                2;

            const int leftPadding =
                5;

            const int rightPadding =
                3;

            const int columnGap =
                1;

            var availableWidth =
                Math.Max(
                    0,
                    block.Width -
                        leftPadding -
                        rightPadding -
                        columnGap
                );

            var leftColumnWidth =
                availableWidth /
                2;

            var rightColumnWidth =
                availableWidth -
                leftColumnWidth;

            var leftColumn =
                new Rectangle(
                    block.Left +
                        leftPadding,
                    contentTop,
                    leftColumnWidth,
                    labelHeight +
                        valueHeight +
                        matchesHeight
                );

            var rightColumn =
                new Rectangle(
                    leftColumn.Right +
                        columnGap,
                    contentTop,
                    rightColumnWidth,
                    labelHeight +
                        valueHeight +
                        matchesHeight
                );

            DrawMetricColumn(
                graphics,
                leftColumn,
                labelHeight,
                valueHeight,
                matchesHeight,
                "WR",
                FormatLaneWinRate(
                    value
                ),
                FormatLaneMatchCount(
                    value,
                    useNetWorthMatches:
                        false
                ),
                GetLaneWinRateColor(
                    value
                )
            );

            DrawMetricColumn(
                graphics,
                rightColumn,
                labelHeight,
                valueHeight,
                matchesHeight,
                "S15",
                FormatLaneS15(
                    value
                ),
                FormatLaneMatchCount(
                    value,
                    useNetWorthMatches:
                        true
                ),
                GetLaneS15Color(
                    value
                )
            );
        }

        private void DrawMetricColumn(
            Graphics graphics,
            Rectangle column,
            int labelHeight,
            int valueHeight,
            int matchesHeight,
            string label,
            string value,
            string matchCount,
            Color valueColor
        )
        {
            var labelBounds =
                new Rectangle(
                    column.Left,
                    column.Top,
                    column.Width,
                    labelHeight
                );

            var valueBounds =
                new Rectangle(
                    column.Left,
                    column.Top +
                        labelHeight,
                    column.Width,
                    valueHeight
                );

            var matchesBounds =
                new Rectangle(
                    column.Left,
                    valueBounds.Bottom,
                    column.Width,
                    matchesHeight
                );

            var commonFlags =
                TextFormatFlags.VerticalCenter |
                TextFormatFlags.SingleLine |
                TextFormatFlags.NoPadding |
                TextFormatFlags.EndEllipsis;

            TextRenderer.DrawText(
                graphics,
                label,
                _metricLabelFont,
                labelBounds,
                BridgeUiTheme.ActionPurple,
                commonFlags |
                TextFormatFlags.HorizontalCenter
            );

            TextRenderer.DrawText(
                graphics,
                value,
                _metricValueFont,
                valueBounds,
                valueColor,
                commonFlags |
                TextFormatFlags.HorizontalCenter
            );

            TextRenderer.DrawText(
                graphics,
                matchCount,
                _metricLabelFont,
                matchesBounds,
                BridgeUiTheme.TextMuted,
                commonFlags |
                TextFormatFlags.HorizontalCenter
            );
        }

        private static Color GetLaneWinRateColor(
            CurrentMatchLaneDisplayValue? value
        )
        {
            if (
                !value.HasValue ||
                !value.Value
                    .WinRatePercent
                    .HasValue
            )
            {
                return BridgeUiTheme.TextMuted;
            }

            var winRatePercent =
                Math.Round(
                    value.Value
                        .WinRatePercent
                        .Value,
                    1,
                    MidpointRounding.AwayFromZero
                );

            if (winRatePercent > 51)
            {
                return BridgeUiTheme
                    .ServiceCompleted;
            }

            if (winRatePercent < 50)
            {
                return BridgeUiTheme
                    .ServiceError;
            }

            return BridgeUiTheme.Text;
        }

        private static Color GetLaneS15Color(
            CurrentMatchLaneDisplayValue? value
        )
        {
            if (
                !value.HasValue ||
                !value.Value
                    .NetWorthDiff15
                    .HasValue
            )
            {
                return BridgeUiTheme.TextMuted;
            }

            var netWorthDiff15 =
                Math.Round(
                    value.Value
                        .NetWorthDiff15
                        .Value,
                    MidpointRounding.AwayFromZero
                );

            if (netWorthDiff15 > 0)
            {
                return BridgeUiTheme
                    .ServiceCompleted;
            }

            if (netWorthDiff15 < 0)
            {
                return BridgeUiTheme
                    .ServiceError;
            }

            return BridgeUiTheme.Text;
        }

        private static string FormatLaneMatchCount(
            CurrentMatchLaneDisplayValue? value,
            bool useNetWorthMatches
        )
        {
            if (!value.HasValue)
            {
                return "n=—";
            }

            var matches =
                useNetWorthMatches
                    ? value.Value
                        .NetWorthMatches
                    : value.Value
                        .Matches;

            return "n=" +
                matches.ToString(
                    CultureInfo.InvariantCulture
                );
        }

        private static string FormatLaneWinRate(
            CurrentMatchLaneDisplayValue? value
        )
        {
            if (
                !value.HasValue ||
                !value.Value
                    .WinRatePercent
                    .HasValue
            )
            {
                return "—";
            }

            var rounded =
                Math.Round(
                    value.Value
                        .WinRatePercent
                        .Value,
                    1,
                    MidpointRounding.AwayFromZero
                );

            return rounded.ToString(
                "0.0",
                CultureInfo.InvariantCulture
            ) +
                "%";
        }

        private static string FormatLaneS15(
            CurrentMatchLaneDisplayValue? value
        )
        {
            if (
                !value.HasValue ||
                !value.Value
                    .NetWorthDiff15
                    .HasValue
            )
            {
                return "—";
            }

            var rounded =
                Math.Round(
                    value.Value
                        .NetWorthDiff15
                        .Value,
                    MidpointRounding.AwayFromZero
                );

            return rounded.ToString(
                "+0;-0;0",
                CultureInfo.InvariantCulture
            );
        }

        private void OnGridLayout(
            object? sender,
            LayoutEventArgs e
        )
        {
            Invalidate();
        }

        private void OnGridScroll(
            object? sender,
            ScrollEventArgs e
        )
        {
            Invalidate();
        }

        protected override void Dispose(
            bool disposing
        )
        {
            if (disposing)
            {
                _grid.Layout -=
                    OnGridLayout;

                _grid.Scroll -=
                    OnGridScroll;

                _metricFont.Dispose();
                _metricValueFont.Dispose();
                _metricLabelFont.Dispose();
            }

            base.Dispose(
                disposing
            );
        }
    }

    private sealed class CurrentMatchDamageDiagnosticsResponse
    {
        public CurrentMatchDamageDiagnosticsResponse()
        {
        }

        public CurrentMatchDamageDiagnosticsSnapshot? Snapshot
        {
            get;
            set;
        }

        public CurrentMatchLaneStatsDiagnostics?
            CurrentLaneStats
        {
            get;
            set;
        }
    }

    private sealed class CurrentMatchLaneStatsDiagnostics
    {
        public CurrentMatchLaneStatsDiagnostics()
        {
        }

        public List<uint>? HeroIds
        {
            get;
            set;
        }

        public List<
            CurrentMatchLaneStatsDiagnosticsEntry?
        >? Lanes
        {
            get;
            set;
        }
    }

    private sealed class
        CurrentMatchLaneStatsDiagnosticsEntry
    {
        public CurrentMatchLaneStatsDiagnosticsEntry()
        {
        }

        public int LaneIndex
        {
            get;
            set;
        }

        public bool HasMatchData
        {
            get;
            set;
        }

        public double? WinRatePercent
        {
            get;
            set;
        }

        public ulong Matches
        {
            get;
            set;
        }

        public bool HasNetWorthData
        {
            get;
            set;
        }

        public double? NetWorthDiff15
        {
            get;
            set;
        }

        public ulong NetWorthMatches
        {
            get;
            set;
        }
    }

    private sealed class CurrentMatchDamageDiagnosticsSnapshot
    {
        public CurrentMatchDamageDiagnosticsSnapshot()
        {
        }

        public ulong MatchId
        {
            get;
            set;
        }

        public CurrentMatchLiveDamageDiagnostics? LiveDamage
        {
            get;
            set;
        }
    }

    private sealed class CurrentMatchLiveDamageDiagnostics
    {
        public CurrentMatchLiveDamageDiagnostics()
        {
        }

        public ulong MatchId
        {
            get;
            set;
        }

        public List<
            CurrentMatchLiveDamageDiagnosticsPlayer
        >? Players
        {
            get;
            set;
        }
    }

    private sealed class CurrentMatchLiveDamageDiagnosticsPlayer
    {
        public CurrentMatchLiveDamageDiagnosticsPlayer()
        {
        }

        public uint AccountId
        {
            get;
            set;
        }

        public uint HeroId
        {
            get;
            set;
        }

        public long HeroDamage
        {
            get;
            set;
        }

        public long Tick
        {
            get;
            set;
        }
    }

    private readonly record struct CurrentMatchDamageValue(
        uint HeroId,
        long HeroDamage,
        long Tick
    );

    internal readonly record struct
        CurrentMatchLaneDisplayValue(
            double? WinRatePercent,
            ulong Matches,
            double? NetWorthDiff15,
            ulong NetWorthMatches
        );

    private static string GetStateText(
        BridgeRuntimeState state
    )
    {
        return state switch
        {
            BridgeRuntimeState.WaitingForDeadlock =>
                "WAITING",

            BridgeRuntimeState.StartingSteam =>
                "STARTING STEAM",

            BridgeRuntimeState.StartingHttpServer =>
                "STARTING SERVER",

            BridgeRuntimeState.Running =>
                "RUNNING",

            BridgeRuntimeState.Stopping =>
                "STOPPING",

            BridgeRuntimeState.Stopped =>
                "STOPPED",

            BridgeRuntimeState.Error =>
                "ERROR",

            _ =>
                "UNKNOWN"
        };
    }

    private static Color GetStateColor(
        BridgeRuntimeState state
    )
    {
        return state switch
        {
            BridgeRuntimeState.Running =>
                Color.LightGreen,

            BridgeRuntimeState.Error =>
                Color.LightCoral,

            BridgeRuntimeState.Stopping =>
                Color.Khaki,

            BridgeRuntimeState.Stopped =>
                Color.Silver,

            _ =>
                Color.LightSkyBlue
        };
    }

    private enum MainSection
    {
        Match,
        History,
        Reactions,
        Log
    }
}
