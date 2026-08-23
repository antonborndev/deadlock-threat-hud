using System.Buffers.Binary;
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
    private const string CurrentMatchContextDiagnosticsAddress =
        "http://127.0.0.1:28741/current-match-context-diagnostics";

    private const int CurrentMatchDamagePollIntervalMilliseconds =
        1_000;

    private const int HeroIconPixels =
        48;

    private const int MaximumHeroIconBytes =
        2 * 1024 * 1024;

    private const int MaximumHeroIconSourceDimension =
        2_048;

    private const int MaximumHeroIconCacheEntries =
        96;

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

    private readonly Label _winrateServiceValue;
    private readonly Label _rankServiceValue;
    private readonly Label _adviserServiceValue;
    private readonly Label _heroDamageServiceValue;

    private readonly Button _installModButton;
    private readonly Button _activateModButton;

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

    private MatchPlayerDetailsUiState?
        _requestedMatchPlayerDetailsState;

    private string _requestedMatchPlayerDetailsDetail =
        String.Empty;

    private readonly Label _matchStatusValue;
    private readonly DataGridView _alliesMatchGrid;
    private readonly DataGridView _enemiesMatchGrid;

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

    private int _matchIconGeneration;

    private readonly RichTextBox _logBox;

    private bool _allowClose;

    public event EventHandler? ShutdownRequested;

    public MainForm()
    {
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
                out _winrateServiceValue,
                out _rankServiceValue,
                out _adviserServiceValue,
                out _heroDamageServiceValue,
                out _installModButton,
                out _activateModButton
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
        var contentFrame =
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

        contentFrame.Controls.Add(
            contentHost
        );

        contentRoot.Controls.Add(
            contentFrame,
            0,
            1
        );

        var matchPanel =
            CreateMatchPanel(
                out _matchStatusValue,
                out _alliesMatchGrid,
                out _enemiesMatchGrid
            );

        _logBox =
            CreateLogBox();

        _logBox.Visible =
            false;

        var reactionList =
            new PlayerReactionListControl
            {
                Dock =
                    DockStyle.Fill,

                Visible =
                    false
            };

        contentHost.Controls.Add(
            matchPanel
        );

        contentHost.Controls.Add(
            reactionList
        );

        contentHost.Controls.Add(
            _logBox
        );

        void SelectSection(
            MainSection section
        )
        {
            var matchSelected =
                section ==
                    MainSection.Match;

            var reactionsSelected =
                section ==
                    MainSection.Reactions;

            var logSelected =
                section ==
                    MainSection.Log;

            matchPanel.Visible =
                matchSelected;

            reactionList.Visible =
                reactionsSelected;

            _logBox.Visible =
                logSelected;

            if (matchSelected)
            {
                matchPanel.BringToFront();
            }
            else if (reactionsSelected)
            {
                reactionList.BringToFront();
            }
            else
            {
                _logBox.BringToFront();
            }

            BridgeUiTheme.SetNavigationSelected(
                matchButton,
                matchSelected
            );

            BridgeUiTheme.SetNavigationSelected(
                reactionsButton,
                reactionsSelected
            );

            BridgeUiTheme.SetNavigationSelected(
                logButton,
                logSelected
            );
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

                await reactionList.RefreshAsync();
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
            !status.IsDeadlockLocated ||
            status.HasVpkConflict
        )
        {
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
            !status.IsDeadlockLocated
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
                ? "Uninstall mod"
                : "Install mod";

        _activateModButton.Text =
            status?.IsActive == true
                ? "Deactivate mod"
                : "Activate mod";

        var baseEnabled =
            !_modOperationRunning &&
            !_deadlockRunning &&
            status?.IsDeadlockLocated == true;

        _installModButton.Enabled =
            baseEnabled &&
            String.IsNullOrWhiteSpace(
                status?.VpkError
            ) &&
            status?.HasVpkConflict != true;

        _activateModButton.Enabled =
            baseEnabled &&
            String.IsNullOrWhiteSpace(
                status?.ActivationError
            ) &&
            (
                status?.IsInstalled == true ||
                status?.IsActive == true
            );

        string installHint;
        string activateHint;

        if (_modOperationRunning)
        {
            installHint =
                "A mod operation is in progress.";

            activateHint =
                installHint;
        }
        else if (_deadlockRunning)
        {
            installHint =
                "Close Deadlock before changing mod files.";

            activateHint =
                installHint;
        }
        else if (
            !String.IsNullOrWhiteSpace(
                _modStatusError
            )
        )
        {
            installHint =
                _modStatusError;

            activateHint =
                installHint;
        }
        else if (
            status?.IsDeadlockLocated != true
        )
        {
            installHint =
                "Deadlock was not found in the configured Steam libraries.";

            activateHint =
                installHint;
        }
        else
        {
            if (
                !String.IsNullOrWhiteSpace(
                    status.VpkError
                )
            )
            {
                installHint =
                    status.VpkError;
            }
            else if (status.HasVpkConflict)
            {
                installHint =
                    "pak57_dir.vpk already exists and is not owned by " +
                    "Threat HUD Bridge.";
            }
            else
            {
                installHint =
                    status.IsInstalled
                        ? status.IsCurrentPayload
                            ? "Remove the Threat HUD VPK from Deadlock."
                            : "Remove the installed Threat HUD VPK " +
                              "before installing this build."
                        : "Install the VPK embedded in ThreatHudBridge.exe.";
            }

            if (
                !String.IsNullOrWhiteSpace(
                    status.ActivationError
                )
            )
            {
                activateHint =
                    status.ActivationError;
            }
            else
            {
                activateHint =
                    status.IsActive
                        ? "Disable the citadel/addons search path."
                        : status.IsInstalled
                            ? "Enable the citadel/addons search path."
                            : "Install the Threat HUD VPK before activation.";
            }
        }

        _modToolTip.SetToolTip(
            _installModButton,
            installHint
        );

        _modToolTip.SetToolTip(
            _activateModButton,
            activateHint
        );
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
                ApplyServiceState(
                    _winrateServiceValue,
                    snapshot?.Winrate
                );

                ApplyServiceState(
                    _rankServiceValue,
                    snapshot?.Rank
                );

                ApplyServiceState(
                    _adviserServiceValue,
                    snapshot?.Adviser
                );

                ApplyServiceState(
                    _heroDamageServiceValue,
                    snapshot?.HeroDamage
                );
            }
        );
    }

    private static void ApplyServiceState(
        Label label,
        BridgeServiceState? state
    )
    {
        var text =
            !state.HasValue
                ? "—"
                : state.Value switch
                {
                    BridgeServiceState.InProgress =>
                        "In progress",

                    BridgeServiceState.Completed =>
                        "Done",

                    BridgeServiceState.Error =>
                        "Error",

                    _ =>
                        throw new ArgumentOutOfRangeException(
                            nameof(state),
                            state,
                            "Unknown Bridge service state."
                        )
                };

        var color =
            !state.HasValue
                ? BridgeUiTheme.TextMuted
                : state.Value switch
                {
                    BridgeServiceState.InProgress =>
                        BridgeUiTheme
                            .ServiceInProgress,

                    BridgeServiceState.Completed =>
                        BridgeUiTheme
                            .ServiceCompleted,

                    BridgeServiceState.Error =>
                        BridgeUiTheme
                            .ServiceError,

                    _ =>
                        throw new ArgumentOutOfRangeException(
                            nameof(state),
                            state,
                            "Unknown Bridge service state."
                        )
                };

        if (
            !String.Equals(
                label.Text,
                text,
                StringComparison.Ordinal
            )
        )
        {
            label.Text =
                text;
        }

        if (
            label.ForeColor !=
                color
        )
        {
            label.ForeColor =
                color;
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

                _alliesMatchGrid.Rows.Clear();
                _enemiesMatchGrid.Rows.Clear();

                if (
                    state !=
                        MatchPlayerDetailsUiState.Ready
                )
                {
                    _currentMatchDamageByAccountId.Clear();
                    ClearHeroIconCache();
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

                PopulateMatchGrid(
                    _alliesMatchGrid,
                    players
                        .Take(6)
                        .ToArray(),
                    _matchIconGeneration
                );

                PopulateMatchGrid(
                    _enemiesMatchGrid,
                    players
                        .Skip(6)
                        .Take(6)
                        .ToArray(),
                    _matchIconGeneration
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
        int iconGeneration
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

            var rowIndex =
                grid.Rows.Add(
                    null,
                    currentDamage.ToString(
                        CultureInfo.InvariantCulture
                    ),
                    hasStats
                        ? Math.Round(
                            player.SoulsPerMinute
                        )
                            .ToString(
                                "0",
                                CultureInfo.InvariantCulture
                            )
                        : "—",
                    hasStats
                        ? FormatPercent(
                            player.HeadshotRatePercent
                        )
                        : "—",
                    hasStats
                        ? FormatPercent(
                            player.AccuracyPercent
                        )
                        : "—"
                );

            var row =
                grid.Rows[
                    rowIndex
                ];

            row.Tag =
                player;

            row.Cells[0].ToolTipText =
                player.HeroName;

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
                    iconGeneration
                );
            }
        }
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

        var comparableCells =
            new List<(
                DataGridViewCell Cell,
                long Damage
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

            damageCell.Value =
                damage.ToString(
                    CultureInfo.InvariantCulture
                );

            SetCurrentMatchDamageCellColor(
                damageCell,
                BridgeUiTheme.Text
            );

            if (hasLiveDamage)
            {
                comparableCells.Add(
                    (
                        damageCell,
                        damage
                    )
                );
            }
        }

        if (
            comparableCells.Count < 2 ||
            comparableCells.Count !=
                grid.Rows.Count
        )
        {
            return;
        }

        var minimumDamage =
            comparableCells.Min(
                value =>
                    value.Damage
            );

        var maximumDamage =
            comparableCells.Max(
                value =>
                    value.Damage
            );

        if (
            minimumDamage ==
                maximumDamage
        )
        {
            return;
        }

        var averageDamage =
            comparableCells.Sum(
                value =>
                    (decimal)value.Damage
            ) /
            comparableCells.Count;

        var exceptionalDamageThreshold =
            averageDamage *
            CurrentMatchExceptionalDamageMultiplier;

        foreach (var value in comparableCells)
        {
            var color =
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
        cell.Style.ForeColor =
            color;

        cell.Style.SelectionForeColor =
            color;
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
                .Cells[0]
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

            row.Cells[0].Value =
                icon;
        }
        catch
        {
            // Missing hero art must not break Match statistics.
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

    private void ClearHeroIconCache()
    {
        foreach (var image in _heroIconCache.Values)
        {
            image.Dispose();
        }

        _heroIconCache.Clear();
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

            try
            {
                _modManagementCancellation.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }

            _matchIconGeneration +=
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
            _modToolTip.Dispose();
            _modManagementCancellation.Dispose();

            ClearHeroIconCache();
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
        out Label winrateServiceValue,
        out Label rankServiceValue,
        out Label adviserServiceValue,
        out Label heroDamageServiceValue,
        out Button installModButton,
        out Button activateModButton
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

        var serviceControls =
            CreateServiceStatusPanel(
                out winrateServiceValue,
                out rankServiceValue,
                out adviserServiceValue,
                out heroDamageServiceValue
            );

        panel.Controls.Add(
            serviceControls,
            2,
            0
        );

        panel.SetRowSpan(
            serviceControls,
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
                    2,

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

    private TableLayoutPanel
        CreateServiceStatusPanel(
            out Label winrateValue,
            out Label rankValue,
            out Label adviserValue,
            out Label heroDamageValue
        )
    {
        var panel =
            new TableLayoutPanel
            {
                Dock =
                    DockStyle.Fill,

                AutoSize =
                    true,

                ColumnCount =
                    2,

                RowCount =
                    5,

                BackColor =
                    BridgeUiTheme.SurfaceRaised,

                Margin =
                    new Padding(
                        18,
                        0,
                        12,
                        0
                    ),

                Padding =
                    new Padding(
                        0
                    )
            };

        panel.ColumnStyles.Add(
            new ColumnStyle(
                SizeType.Absolute,
                105
            )
        );

        panel.ColumnStyles.Add(
            new ColumnStyle(
                SizeType.Percent,
                100F
            )
        );

        panel.RowStyles.Add(
            new RowStyle(
                SizeType.AutoSize
            )
        );

        var title =
            new Label
            {
                AutoSize =
                    true,

                Text =
                    "SERVICE STATUS",

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
                        3
                    )
            };

        panel.Controls.Add(
            title,
            0,
            0
        );

        panel.SetColumnSpan(
            title,
            2
        );

        winrateValue =
            AddStatusRow(
                panel,
                1,
                "Winrate:"
            );

        rankValue =
            AddStatusRow(
                panel,
                2,
                "Rank:"
            );

        adviserValue =
            AddStatusRow(
                panel,
                3,
                "Adviser:"
            );

        heroDamageValue =
            AddStatusRow(
                panel,
                4,
                "Hero Damage:"
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

    private Control CreateMatchPanel(
        out Label statusValue,
        out DataGridView alliesGrid,
        out DataGridView enemiesGrid
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

        var title =
            new Label
            {
                AutoSize =
                    true,

                Text =
                    "CURRENT HERO STATS",

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

        root.Controls.Add(
            title,
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
                    2,

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
                out alliesGrid
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
                out enemiesGrid
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

        teams.Controls.Add(
            enemiesPanel,
            1,
            0
        );

        root.Controls.Add(
            teams,
            0,
            2
        );

        return root;
    }

    private Control CreateMatchTeamStatsPanel(
        string title,
        out DataGridView grid
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
                    2,

                BackColor =
                    BridgeUiTheme.SurfaceRaised,

                Padding =
                    new Padding(
                        8
                    )
            };

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

        root.Controls.Add(
            titleLabel,
            0,
            0
        );

        root.Controls.Add(
            grid,
            0,
            1
        );

        return root;
    }

    private DataGridView CreateMatchStatsGrid()
    {
        var grid =
            new DataGridView
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

        grid.ColumnHeadersHeight =
            30;

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

                SortMode =
                    DataGridViewColumnSortMode.NotSortable
            };

        grid.Columns.Add(
            heroColumn
        );

        AddMatchTextColumn(
            grid,
            "Damage",
            "DMG",
            1.1F,
            "Hero damage in the current match"
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
        Reactions,
        Log
    }
}
