using System.Buffers.Binary;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.Windows.Forms;

internal sealed class MatchHistoryDetailsForm :
    Form
{
    private const ulong SteamId64IndividualBase =
        76561197960265728UL;

    private const int TeamSize =
        6;

    private const int LaneCount =
        3;

    private const int HeroIconPixels =
        48;

    private const int RankIconPixels =
        38;

    private const int MaximumHeroIconBytes =
        2 * 1024 * 1024;

    private const int MaximumHeroIconSourceDimension =
        2_048;

    private const int MaximumRankIconSourceDimension =
        512;

    private const int MatchGridHeightSafetyMargin =
        2;

    private const string ReactionColumnName =
        "Reaction";

    private const int ReactionColumnWidth =
        50;

    private const int ReactionIconPixels =
        14;

    private const int ReactionIconGap =
        4;

    private const int ReactionIconHorizontalHitPadding =
        2;

    private const int ReactionIconVerticalHitPadding =
        5;

    private const int ReactionHeaderIconPixels =
        16;

    private const decimal ExceptionalDamageMultiplier =
        1.20M;

    private static readonly Color TopDamageColor =
        Color.LightGreen;

    private static readonly Color LowestDamageColor =
        Color.LightCoral;

    private static readonly Color ExceptionalDamageColor =
        Color.FromArgb(
            204,
            128,
            255
        );

    /*
     * History windows belong to the supervisor process, while the localhost
     * worker can stop and restart independently. Fetch rank art directly from
     * Deadlock API and share its bounded byte cache between all open windows.
     */
    private static readonly HttpClient SharedRankHttpClient =
        CreateSharedRankHttpClient();

    private static readonly DeadlockRankImageService
        SharedRankImageService =
            new(
                SharedRankHttpClient,
                cacheLifetime:
                    TimeSpan.FromHours(6)
            );

    private readonly MatchHistoryEntry _entry;
    private readonly MatchHistorySnapshot _snapshot;
    private readonly Label _statusValue;
    private readonly MainForm.CurrentMatchLaneStatsPanel
        _alliesLaneStatsPanel;
    private readonly DataGridView _alliesGrid;
    private readonly DataGridView _enemiesGrid;

    private readonly HttpClient _heroImageHttpClient =
        new()
        {
            Timeout =
                TimeSpan.FromSeconds(10)
        };

    private readonly CancellationTokenSource
        _lifetimeCancellation =
            new();

    private readonly Dictionary<string, Image>
        _heroIconCache =
            new(
                StringComparer.OrdinalIgnoreCase
            );

    private readonly Dictionary<int, Image>
        _rankIconCache =
            new();

    private readonly Image _rankUnavailableIcon =
        CreateRankUnavailableIcon();

    private readonly Bitmap _reactionHeaderIcon =
        CreateReactionHeaderIcon();

    private readonly PlayerReactionStore _reactionStore =
        new();

    private readonly Dictionary<uint, int>
        _reactionsByAccountId =
            new();

    private readonly HashSet<uint>
        _reactionWritesInProgress =
            new();

    private readonly Bitmap _reactionLikeNeutralIcon;
    private readonly Bitmap _reactionDislikeNeutralIcon;
    private readonly Bitmap _reactionLikeSelectedIcon;
    private readonly Bitmap _reactionDislikeSelectedIcon;
    private readonly Bitmap _reactionLikeDisabledIcon;
    private readonly Bitmap _reactionDislikeDisabledIcon;

    private bool _reactionLoadStarted;
    private bool _reactionLoadFailed;
    private bool _reactionStoreReady;

    private bool _resourcesDisposed;

    public MatchHistoryDetailsForm(
        MatchHistoryEntry entry,
        MatchHistorySnapshot snapshot
    )
    {
        _entry =
            entry ??
            throw new ArgumentNullException(
                nameof(entry)
            );

        _snapshot =
            snapshot ??
            throw new ArgumentNullException(
                nameof(snapshot)
            );

        if (
            _entry.MatchId == 0 ||
            _snapshot.MatchId !=
                _entry.MatchId
        )
        {
            throw new ArgumentException(
                "The saved snapshot does not match its history entry.",
                nameof(snapshot)
            );
        }

        _reactionLikeNeutralIcon =
            CreateReactionIcon(
                ReactionLikePngBase64,
                Color.White,
                0.68F
            );

        _reactionDislikeNeutralIcon =
            CreateReactionIcon(
                ReactionDislikePngBase64,
                Color.White,
                0.68F
            );

        _reactionLikeSelectedIcon =
            CreateReactionIcon(
                ReactionLikePngBase64,
                Color.LightGreen,
                1F
            );

        _reactionDislikeSelectedIcon =
            CreateReactionIcon(
                ReactionDislikePngBase64,
                Color.LightCoral,
                1F
            );

        _reactionLikeDisabledIcon =
            CreateReactionIcon(
                ReactionLikePngBase64,
                BridgeUiTheme.TextMuted,
                0.28F
            );

        _reactionDislikeDisabledIcon =
            CreateReactionIcon(
                ReactionDislikePngBase64,
                BridgeUiTheme.TextMuted,
                0.28F
            );

        Text =
            "Match " +
            _entry.MatchId.ToString(
                CultureInfo.InvariantCulture
            ) +
            " — Threat HUD Bridge";

        StartPosition =
            FormStartPosition.CenterParent;

        MinimumSize =
            new Size(
                1040,
                470
            );

        Size =
            new Size(
                1120,
                540
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
                    2,

                Padding =
                    new Padding(18),

                BackColor =
                    BackColor
            };

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

        var frame =
            new Panel
            {
                Dock =
                    DockStyle.Fill,

                BackColor =
                    BridgeUiTheme.Border,

                Padding =
                    new Padding(1),

                Margin =
                    new Padding(0)
            };

        var matchPanel =
            MainForm.CreateMatchPanel(
                out _statusValue,
                out _alliesLaneStatsPanel,
                out _alliesGrid,
                out _enemiesGrid,
                titleText:
                    "MATCH ID " +
                    _entry.MatchId.ToString(
                        CultureInfo.InvariantCulture
                    ),
                titleBadgeText:
                    _snapshot.LocalPlayerWon switch
                    {
                        true =>
                            "VICTORY",

                        false =>
                            "DEFEAT",

                        null =>
                            null
                    },
                titleBadgeColor:
                    _snapshot.LocalPlayerWon switch
                    {
                        true =>
                            BridgeUiTheme.ServiceCompleted,

                        false =>
                            BridgeUiTheme.ServiceError,

                        null =>
                            null
                    }
            );

        AddReactionColumn(
            _alliesGrid
        );

        AddReactionColumn(
            _enemiesGrid
        );

        frame.Controls.Add(
            matchPanel
        );

        root.Controls.Add(
            frame,
            0,
            0
        );

        var footer =
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

        footer.Controls.Add(
            closeButton
        );

        root.Controls.Add(
            footer,
            0,
            1
        );

        _alliesGrid.CellContentClick +=
            OnMatchGridCellContentClick;

        _enemiesGrid.CellContentClick +=
            OnMatchGridCellContentClick;

        SubscribeReactionGridEvents(
            _alliesGrid
        );

        SubscribeReactionGridEvents(
            _enemiesGrid
        );

        PopulateSnapshot();
    }

    private void PopulateSnapshot()
    {
        var playerDetails =
            _snapshot.PlayerDetails;

        var savedPlayers =
            playerDetails?.Players;

        DeadlockMatchPlayerDetailsEntry[]?
            players =
                null;

        if (
            savedPlayers is not null &&
            savedPlayers.Count ==
                TeamSize * 2 &&
            savedPlayers.All(
                static player =>
                    player is not null
            )
        )
        {
            players =
                savedPlayers
                    .OrderBy(
                        player =>
                            player.Index
                    )
                    .ToArray();
        }

        if (
            !String.Equals(
                playerDetails?.Status,
                "ready",
                StringComparison.OrdinalIgnoreCase
            ) ||
            players is null ||
            !HasValidRoster(players)
        )
        {
            SetUnavailableSnapshotStatus(
                playerDetails
            );

            return;
        }

        var rankModuleEnabled =
            _snapshot.Modules?.Rank ==
                true;

        var damageIndex =
            BuildDamageIndex(
                _snapshot
            );

        PopulateGrid(
            _alliesGrid,
            players.Take(TeamSize).ToArray(),
            damageIndex,
            rankModuleEnabled,
            isAllies:
                true
        );

        PopulateGrid(
            _enemiesGrid,
            players.Skip(TeamSize).Take(TeamSize).ToArray(),
            damageIndex,
            rankModuleEnabled,
            isAllies:
                false
        );

        ApplyDamageColors(
            _alliesGrid,
            damageIndex
        );

        ApplyDamageColors(
            _enemiesGrid,
            damageIndex
        );

        if (
            _snapshot.Modules?.Adviser ==
                true &&
            TryBuildLaneValues(
                _snapshot.LaneStats,
                players,
                out var laneValues
            )
        )
        {
            _alliesLaneStatsPanel.SetLaneStats(
                laneValues
            );
        }
        else
        {
            _alliesLaneStatsPanel.ClearLaneStats();
        }

        _statusValue.Text =
            "Saved match " +
            _entry.MatchId.ToString(
                CultureInfo.InvariantCulture
            ) +
            " · added " +
            _entry.AddedAtUtc
                .ToLocalTime()
                .ToString(
                    "yyyy-MM-dd HH:mm:ss",
                    CultureInfo.InvariantCulture
                ) +
            " · snapshot " +
            _entry.UpdatedAtUtc
                .ToLocalTime()
                .ToString(
                    "yyyy-MM-dd HH:mm:ss",
                    CultureInfo.InvariantCulture
                );

        _statusValue.ForeColor =
            Color.LightGreen;
    }

    private void SetUnavailableSnapshotStatus(
        DeadlockMatchPlayerDetailsSnapshot? playerDetails
    )
    {
        var status =
            playerDetails?.Status;

        if (
            String.Equals(
                status,
                "waiting",
                StringComparison.OrdinalIgnoreCase
            )
        )
        {
            _statusValue.Text =
                "Player statistics were not available when this snapshot was saved.";

            _statusValue.ForeColor =
                BridgeUiTheme.TextMuted;

            return;
        }

        if (
            String.Equals(
                status,
                "loading",
                StringComparison.OrdinalIgnoreCase
            )
        )
        {
            _statusValue.Text =
                "Player statistics were still loading when this snapshot was saved.";

            _statusValue.ForeColor =
                Color.LightSkyBlue;

            return;
        }

        if (
            String.Equals(
                status,
                "failed",
                StringComparison.OrdinalIgnoreCase
            )
        )
        {
            var error =
                playerDetails?.Error;

            _statusValue.Text =
                String.IsNullOrWhiteSpace(
                    error
                )
                    ? "Failed to load player statistics for this saved snapshot."
                    : "Failed to load player statistics: " +
                        error;

            _statusValue.ForeColor =
                Color.LightCoral;

            return;
        }

        _statusValue.Text =
            "Saved snapshot does not contain a complete player roster.";

        _statusValue.ForeColor =
            Color.LightCoral;
    }

    private static bool HasValidRoster(
        IReadOnlyList<
            DeadlockMatchPlayerDetailsEntry
        > players
    )
    {
        if (players.Count != TeamSize * 2)
        {
            return false;
        }

        for (
            var index = 0;
            index < players.Count;
            index++
        )
        {
            if (players[index].Index != index)
            {
                return false;
            }
        }

        return true;
    }

    private void PopulateGrid(
        DataGridView grid,
        IReadOnlyList<
            DeadlockMatchPlayerDetailsEntry
        > players,
        IReadOnlyDictionary<
            uint,
            HistoricalDamageValue
        > damageIndex,
        bool rankModuleEnabled,
        bool isAllies
    )
    {
        foreach (var player in players)
        {
            var hasStats =
                String.Equals(
                    player.Status,
                    "ok",
                    StringComparison.Ordinal
                ) &&
                player.MatchesPlayed > 0;

            var hasDamage =
                TryGetDamage(
                    player,
                    damageIndex,
                    out var damage
                );

            var rank =
                player.Rank;

            var subrank =
                player.Subrank;

            var hasRank =
                rankModuleEnabled &&
                TryGetDisplayRank(
                    player,
                    out rank,
                    out subrank
                );

            var rowIndex =
                grid.Rows.Add();

            var row =
                grid.Rows[rowIndex];

            row.Tag =
                player;

            MainForm.ApplyLocalPlayerRowStyle(
                row,
                player.AccountId,
                _snapshot.LocalPlayerAccountId
            );

            row.Cells[ReactionColumnName].Value =
                null;

            UpdateReactionToolTip(
                row,
                player
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
                (
                    hasDamage
                        ? damage.HeroDamage
                        : 0L
                )
                .ToString(
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
                isAllies &&
                rowIndex % 2 == 1 &&
                rowIndex < players.Count - 1
            )
            {
                row.DividerHeight =
                    2;
            }

            row.Cells["Hero"].ToolTipText =
                CanOpenSteamProfile(player)
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
                    : "Rank module was disabled for this snapshot.";

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
                    player.HeroIconUrl!
                );
            }

            if (hasRank)
            {
                QueueRankIconLoad(
                    grid,
                    rowIndex,
                    rank,
                    subrank
                );
            }
        }
    }

    private static void AddReactionColumn(
        DataGridView grid
    )
    {
        if (
            grid.Columns.Contains(
                ReactionColumnName
            )
        )
        {
            return;
        }

        grid.Columns.Add(
            new DataGridViewTextBoxColumn
            {
                Name =
                    ReactionColumnName,

                HeaderText =
                    String.Empty,

                Width =
                    ReactionColumnWidth,

                MinimumWidth =
                    ReactionColumnWidth,

                AutoSizeMode =
                    DataGridViewAutoSizeColumnMode.None,

                ReadOnly =
                    true,

                SortMode =
                    DataGridViewColumnSortMode.NotSortable,

                ToolTipText =
                    "Player reactions"
            }
        );
    }

    private void SubscribeReactionGridEvents(
        DataGridView grid
    )
    {
        grid.CellPainting +=
            OnReactionGridCellPainting;

        grid.CellMouseClick +=
            OnReactionGridCellMouseClick;

        grid.CellMouseMove +=
            OnReactionGridCellMouseMove;

        grid.MouseLeave +=
            OnReactionGridMouseLeave;
    }

    private void UnsubscribeReactionGridEvents(
        DataGridView grid
    )
    {
        grid.CellPainting -=
            OnReactionGridCellPainting;

        grid.CellMouseClick -=
            OnReactionGridCellMouseClick;

        grid.CellMouseMove -=
            OnReactionGridCellMouseMove;

        grid.MouseLeave -=
            OnReactionGridMouseLeave;

        grid.Cursor =
            Cursors.Default;
    }

    private async Task LoadReactionsAsync()
    {
        if (_reactionLoadStarted)
        {
            return;
        }

        _reactionLoadStarted =
            true;

        try
        {
            var cancellationToken =
                _lifetimeCancellation.Token;

            var accountIds =
                GetReactableAccountIds();

            if (accountIds.Length > 0)
            {
                await _reactionStore.InitializeAsync(
                    cancellationToken
                );

                var reactions =
                    await _reactionStore.GetManyAsync(
                        accountIds,
                        cancellationToken
                    );

                if (
                    !CanUseReactionUi(
                        cancellationToken
                    )
                )
                {
                    return;
                }

                _reactionsByAccountId.Clear();

                foreach (var reaction in reactions)
                {
                    _reactionsByAccountId[
                        reaction.Key
                    ] =
                        reaction.Value;
                }
            }

            if (
                !CanUseReactionUi(
                    cancellationToken
                )
            )
            {
                return;
            }

            _reactionStoreReady =
                true;

            RefreshReactionCells();
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (Exception error)
        {
            _reactionLoadFailed =
                true;

            if (!CanUseReactionUi())
            {
                return;
            }

            RefreshReactionCells();

            MessageBox.Show(
                this,
                error.Message,
                "Failed to load player reactions",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error
            );
        }
    }

    private uint[] GetReactableAccountIds()
    {
        return _alliesGrid.Rows
            .Cast<DataGridViewRow>()
            .Concat(
                _enemiesGrid.Rows
                    .Cast<DataGridViewRow>()
            )
            .Select(
                static row =>
                    row.Tag
            )
            .OfType<
                DeadlockMatchPlayerDetailsEntry
            >()
            .Where(
                CanSetPlayerReaction
            )
            .Select(
                static player =>
                    player.AccountId
            )
            .Distinct()
            .ToArray();
    }

    private void OnReactionGridCellPainting(
        object? sender,
        DataGridViewCellPaintingEventArgs e
    )
    {
        if (
            _resourcesDisposed ||
            sender is not DataGridView grid ||
            !IsReactionColumn(
                grid,
                e.ColumnIndex
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

        if (e.RowIndex == -1)
        {
            e.PaintBackground(
                e.CellBounds,
                false
            );

            using var headerSeparatorPen =
                new Pen(
                    BridgeUiTheme.Border
                );

            graphics.DrawLine(
                headerSeparatorPen,
                e.CellBounds.Left,
                e.CellBounds.Bottom -
                    1,
                e.CellBounds.Right,
                e.CellBounds.Bottom -
                    1
            );

            graphics.DrawImageUnscaled(
                _reactionHeaderIcon,
                e.CellBounds.Left +
                    (e.CellBounds.Width -
                        _reactionHeaderIcon.Width) /
                        2,

                e.CellBounds.Top +
                    (e.CellBounds.Height -
                        _reactionHeaderIcon.Height) /
                        2
            );

            e.Handled =
                true;

            return;
        }

        if (
            e.RowIndex < 0 ||
            e.RowIndex >= grid.Rows.Count
        )
        {
            return;
        }

        e.PaintBackground(
            e.CellBounds,
            true
        );

        e.Paint(
            e.CellBounds,
            DataGridViewPaintParts.Border
        );

        if (
            grid.Rows[e.RowIndex].Tag is not
                DeadlockMatchPlayerDetailsEntry player
        )
        {
            e.Handled =
                true;

            return;
        }

        GetReactionIconBounds(
            e.CellBounds,
            out var likeBounds,
            out var dislikeBounds
        );

        var enabled =
            CanSetPlayerReaction(player) &&
            _reactionStoreReady &&
            !_reactionWritesInProgress.Contains(
                player.AccountId
            );

        var reaction =
            GetReaction(
                player.AccountId
            );

        var likeIcon =
            !enabled
                ? _reactionLikeDisabledIcon
                : reaction ==
                    PlayerReactionValue.Like
                    ? _reactionLikeSelectedIcon
                    : _reactionLikeNeutralIcon;

        var dislikeIcon =
            !enabled
                ? _reactionDislikeDisabledIcon
                : reaction ==
                    PlayerReactionValue.Dislike
                    ? _reactionDislikeSelectedIcon
                    : _reactionDislikeNeutralIcon;

        var graphicsState =
            graphics.Save();

        try
        {
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

            graphics.DrawImage(
                likeIcon,
                likeBounds
            );

            graphics.DrawImage(
                dislikeIcon,
                dislikeBounds
            );
        }
        finally
        {
            graphics.Restore(
                graphicsState
            );
        }

        e.Handled =
            true;
    }

    private async void OnReactionGridCellMouseClick(
        object? sender,
        DataGridViewCellMouseEventArgs e
    )
    {
        if (
            e.Button != MouseButtons.Left ||
            sender is not DataGridView grid ||
            e.RowIndex < 0 ||
            e.RowIndex >= grid.Rows.Count ||
            !IsReactionColumn(
                grid,
                e.ColumnIndex
            ) ||
            grid.Rows[e.RowIndex].Tag is not
                DeadlockMatchPlayerDetailsEntry player ||
            !CanSetPlayerReaction(player) ||
            !_reactionStoreReady ||
            _reactionWritesInProgress.Contains(
                player.AccountId
            )
        )
        {
            return;
        }

        var cell =
            grid.Rows[e.RowIndex]
                .Cells[e.ColumnIndex];

        GetReactionHitTargetBounds(
            new Rectangle(
                0,
                0,
                cell.Size.Width,
                cell.Size.Height
            ),
            out var likeBounds,
            out var dislikeBounds
        );

        var clickPoint =
            new Point(
                e.X,
                e.Y
            );

        int requestedReaction;

        if (
            likeBounds.Contains(
                clickPoint
            )
        )
        {
            requestedReaction =
                PlayerReactionValue.Like;
        }
        else if (
            dislikeBounds.Contains(
                clickPoint
            )
        )
        {
            requestedReaction =
                PlayerReactionValue.Dislike;
        }
        else
        {
            return;
        }

        var accountId =
            player.AccountId;

        var currentReaction =
            GetReaction(
                accountId
            );

        var newReaction =
            currentReaction ==
                requestedReaction
                ? PlayerReactionValue.None
                : requestedReaction;

        _reactionWritesInProgress.Add(
            accountId
        );

        RefreshReactionCells(
            accountId
        );

        try
        {
            await _reactionStore.SetAsync(
                accountId,
                newReaction,
                _lifetimeCancellation.Token
            );

            if (!CanUseReactionUi())
            {
                return;
            }

            _reactionsByAccountId[
                accountId
            ] =
                newReaction;
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (Exception error)
        {
            if (CanUseReactionUi())
            {
                MessageBox.Show(
                    this,
                    error.Message,
                    "Failed to save the player reaction",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error
                );
            }
        }
        finally
        {
            _reactionWritesInProgress.Remove(
                accountId
            );

            if (CanUseReactionUi())
            {
                RefreshReactionCells(
                    accountId
                );
            }
        }
    }

    private void OnReactionGridCellMouseMove(
        object? sender,
        DataGridViewCellMouseEventArgs e
    )
    {
        if (sender is not DataGridView grid)
        {
            return;
        }

        var useHandCursor =
            e.RowIndex >= 0 &&
            e.RowIndex < grid.Rows.Count &&
            IsReactionColumn(
                grid,
                e.ColumnIndex
            ) &&
            grid.Rows[e.RowIndex].Tag is
                DeadlockMatchPlayerDetailsEntry player &&
            CanSetPlayerReaction(player) &&
            _reactionStoreReady &&
            !_reactionWritesInProgress.Contains(
                player.AccountId
            ) &&
            IsReactionIconPoint(
                grid.Rows[e.RowIndex]
                    .Cells[e.ColumnIndex],
                e.X,
                e.Y
            );

        grid.Cursor =
            useHandCursor
                ? Cursors.Hand
                : Cursors.Default;
    }

    private static void OnReactionGridMouseLeave(
        object? sender,
        EventArgs e
    )
    {
        if (sender is DataGridView grid)
        {
            grid.Cursor =
                Cursors.Default;
        }
    }

    private static bool IsReactionIconPoint(
        DataGridViewCell cell,
        int x,
        int y
    )
    {
        GetReactionHitTargetBounds(
            new Rectangle(
                0,
                0,
                cell.Size.Width,
                cell.Size.Height
            ),
            out var likeBounds,
            out var dislikeBounds
        );

        var point =
            new Point(
                x,
                y
            );

        return
            likeBounds.Contains(point) ||
            dislikeBounds.Contains(point);
    }

    private static void GetReactionIconBounds(
        Rectangle cellBounds,
        out Rectangle likeBounds,
        out Rectangle dislikeBounds
    )
    {
        var contentWidth =
            ReactionIconPixels * 2 +
            ReactionIconGap;

        var left =
            cellBounds.Left +
            Math.Max(
                0,
                (cellBounds.Width -
                    contentWidth) /
                    2
            );

        var top =
            cellBounds.Top +
            Math.Max(
                0,
                (cellBounds.Height -
                    ReactionIconPixels) /
                    2
            );

        likeBounds =
            new Rectangle(
                left,
                top,
                ReactionIconPixels,
                ReactionIconPixels
            );

        dislikeBounds =
            new Rectangle(
                likeBounds.Right +
                    ReactionIconGap,
                top,
                ReactionIconPixels,
                ReactionIconPixels
            );
    }

    private static void GetReactionHitTargetBounds(
        Rectangle cellBounds,
        out Rectangle likeBounds,
        out Rectangle dislikeBounds
    )
    {
        GetReactionIconBounds(
            cellBounds,
            out likeBounds,
            out dislikeBounds
        );

        likeBounds.Inflate(
            ReactionIconHorizontalHitPadding,
            ReactionIconVerticalHitPadding
        );

        dislikeBounds.Inflate(
            ReactionIconHorizontalHitPadding,
            ReactionIconVerticalHitPadding
        );
    }

    private static bool IsReactionColumn(
        DataGridView grid,
        int columnIndex
    )
    {
        return
            columnIndex >= 0 &&
            columnIndex < grid.Columns.Count &&
            String.Equals(
                grid.Columns[columnIndex].Name,
                ReactionColumnName,
                StringComparison.Ordinal
            );
    }

    private static bool CanSetPlayerReaction(
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

    private int GetReaction(
        uint accountId
    )
    {
        return
            _reactionsByAccountId.TryGetValue(
                accountId,
                out var reaction
            ) &&
            PlayerReactionValue.IsValid(
                reaction
            )
                ? reaction
                : PlayerReactionValue.None;
    }

    private void RefreshReactionCells(
        uint? accountId =
            null
    )
    {
        RefreshReactionCells(
            _alliesGrid,
            accountId
        );

        RefreshReactionCells(
            _enemiesGrid,
            accountId
        );
    }

    private void RefreshReactionCells(
        DataGridView grid,
        uint? accountId
    )
    {
        if (
            grid.IsDisposed ||
            !grid.Columns.Contains(
                ReactionColumnName
            )
        )
        {
            return;
        }

        var reactionColumnIndex =
            grid.Columns[ReactionColumnName]
                .Index;

        foreach (
            DataGridViewRow row in
            grid.Rows
        )
        {
            if (
                row.Tag is not
                    DeadlockMatchPlayerDetailsEntry player ||
                (
                    accountId.HasValue &&
                    player.AccountId !=
                        accountId.Value
                )
            )
            {
                continue;
            }

            UpdateReactionToolTip(
                row,
                player
            );

            grid.InvalidateCell(
                reactionColumnIndex,
                row.Index
            );
        }
    }

    private void UpdateReactionToolTip(
        DataGridViewRow row,
        DeadlockMatchPlayerDetailsEntry player
    )
    {
        var cell =
            row.Cells[
                ReactionColumnName
            ];

        if (player.AccountId == 0)
        {
            cell.ToolTipText =
                "Player identity is unresolved.";

            return;
        }

        if (
            String.Equals(
                player.Status,
                "bot",
                StringComparison.OrdinalIgnoreCase
            )
        )
        {
            cell.ToolTipText =
                "Reactions are unavailable for bots.";

            return;
        }

        if (
            _reactionWritesInProgress.Contains(
                player.AccountId
            )
        )
        {
            cell.ToolTipText =
                "Saving reaction...";

            return;
        }

        if (_reactionLoadFailed)
        {
            cell.ToolTipText =
                "Player reactions are unavailable.";

            return;
        }

        if (!_reactionStoreReady)
        {
            cell.ToolTipText =
                "Loading saved reaction...";

            return;
        }

        cell.ToolTipText =
            GetReaction(
                player.AccountId
            ) switch
            {
                PlayerReactionValue.Like =>
                    "Liked. Click Like again to clear, or click Dislike to replace it.",

                PlayerReactionValue.Dislike =>
                    "Disliked. Click Dislike again to clear, or click Like to replace it.",

                _ =>
                    "Click Like or Dislike."
            };
    }

    private bool CanUseReactionUi(
        CancellationToken cancellationToken =
            default
    )
    {
        return
            !_resourcesDisposed &&
            !cancellationToken.IsCancellationRequested &&
            !IsDisposed &&
            !Disposing;
    }

    private static IReadOnlyDictionary<
        uint,
        HistoricalDamageValue
    > BuildDamageIndex(
        MatchHistorySnapshot snapshot
    )
    {
        var result =
            new Dictionary<
                uint,
                HistoricalDamageValue
            >();

        if (
            snapshot.Modules?.HeroDamage !=
                true ||
            snapshot.Context?
                .HeroDamageAllowedForMatch !=
                true
        )
        {
            return result;
        }

        var liveDamage =
            snapshot.HeroDamage;

        if (
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
                player is null ||
                player.AccountId == 0 ||
                player.HeroDamage < 0
            )
            {
                continue;
            }

            var value =
                new HistoricalDamageValue(
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

    private static bool TryGetDamage(
        DeadlockMatchPlayerDetailsEntry player,
        IReadOnlyDictionary<
            uint,
            HistoricalDamageValue
        > damageIndex,
        out HistoricalDamageValue damage
    )
    {
        if (
            player.AccountId != 0 &&
            damageIndex.TryGetValue(
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
            damage =
                accountDamage;

            return true;
        }

        if (player.HeroId == 0)
        {
            damage =
                default;

            return false;
        }

        var found =
            false;

        var heroDamage =
            default(HistoricalDamageValue);

        foreach (var candidate in damageIndex.Values)
        {
            if (candidate.HeroId != player.HeroId)
            {
                continue;
            }

            if (found)
            {
                damage =
                    default;

                return false;
            }

            heroDamage =
                candidate;

            found =
                true;
        }

        damage =
            heroDamage;

        return found;
    }

    private static void ApplyDamageColors(
        DataGridView grid,
        IReadOnlyDictionary<
            uint,
            HistoricalDamageValue
        > damageIndex
    )
    {
        var damageColumn =
            grid.Columns["Damage"];

        if (damageColumn is null)
        {
            return;
        }

        var values =
            new List<(
                DataGridViewCell Cell,
                long Damage,
                bool HasLiveDamage
            )>(grid.Rows.Count);

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
                TryGetDamage(
                    player,
                    damageIndex,
                    out var liveDamage
                )
            )
            {
                damage =
                    liveDamage.HeroDamage;

                hasLiveDamage =
                    true;
            }

            values.Add(
                (
                    row.Cells[damageColumn.Index],
                    damage,
                    hasLiveDamage
                )
            );
        }

        var useComparisonColors =
            values.Count >= 2 &&
            values.All(
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
                values.Min(
                    value =>
                        value.Damage
                );

            maximumDamage =
                values.Max(
                    value =>
                        value.Damage
                );

            if (minimumDamage == maximumDamage)
            {
                useComparisonColors =
                    false;
            }
            else
            {
                averageDamage =
                    values.Sum(
                        value =>
                            (decimal)value.Damage
                    ) /
                    values.Count;

                exceptionalDamageThreshold =
                    averageDamage *
                    ExceptionalDamageMultiplier;
            }
        }

        foreach (var value in values)
        {
            var color =
                BridgeUiTheme.Text;

            if (useComparisonColors)
            {
                color =
                    averageDamage > 0M &&
                    value.Damage >=
                        exceptionalDamageThreshold
                        ? ExceptionalDamageColor
                        : value.Damage ==
                            maximumDamage
                            ? TopDamageColor
                            : value.Damage ==
                                minimumDamage
                                ? LowestDamageColor
                                : BridgeUiTheme.Text;
            }

            value.Cell.Value =
                value.Damage.ToString(
                    CultureInfo.InvariantCulture
                );

            value.Cell.Style.ForeColor =
                color;

            value.Cell.Style.SelectionForeColor =
                color;
        }
    }

    private static bool TryBuildLaneValues(
        MatchHistoryLaneStatsSnapshot? laneSnapshot,
        IReadOnlyList<
            DeadlockMatchPlayerDetailsEntry
        > players,
        out IReadOnlyList<
            MainForm.CurrentMatchLaneDisplayValue
        > laneValues
    )
    {
        laneValues =
            Array.Empty<
                MainForm.CurrentMatchLaneDisplayValue
            >();

        if (
            laneSnapshot?.HeroIds is null ||
            laneSnapshot.Lanes is null ||
            laneSnapshot.HeroIds.Count !=
                TeamSize * 2 ||
            laneSnapshot.Lanes.Count !=
                LaneCount ||
            players.Count !=
                TeamSize * 2
        )
        {
            return false;
        }

        for (
            var index = 0;
            index < players.Count;
            index++
        )
        {
            if (
                players[index].HeroId !=
                    laneSnapshot.HeroIds[index]
            )
            {
                return false;
            }
        }

        var result =
            new MainForm.CurrentMatchLaneDisplayValue[
                LaneCount
            ];

        var assigned =
            new bool[LaneCount];

        foreach (var lane in laneSnapshot.Lanes)
        {
            if (
                lane is null ||
                lane.LaneIndex < 0 ||
                lane.LaneIndex >=
                    LaneCount ||
                assigned[lane.LaneIndex]
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

            result[lane.LaneIndex] =
                new MainForm.CurrentMatchLaneDisplayValue(
                    WinRatePercent:
                        winRatePercent,

                    Matches:
                        lane.Matches,

                    NetWorthDiff15:
                        netWorthDiff15,

                    NetWorthMatches:
                        lane.NetWorthMatches
                );

            assigned[lane.LaneIndex] =
                true;
        }

        if (assigned.Any(value => !value))
        {
            return false;
        }

        laneValues =
            result;

        return true;
    }

    private void QueueHeroIconLoad(
        DataGridView grid,
        int rowIndex,
        string heroIconUrl
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
                .Cells["Hero"].Value =
                    cachedImage;

            return;
        }

        _ = LoadHeroIconAsync(
            grid,
            rowIndex,
            heroIconUrl
        );
    }

    private async Task LoadHeroIconAsync(
        DataGridView grid,
        int rowIndex,
        string heroIconUrl
    )
    {
        try
        {
            var cancellationToken =
                _lifetimeCancellation.Token;

            using var response =
                await _heroImageHttpClient.GetAsync(
                    heroIconUrl,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken
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
                await ReadLimitedBytesAsync(
                    response.Content,
                    MaximumHeroIconBytes,
                    cancellationToken
                );

            if (bytes is null)
            {
                return;
            }

            using var renderedIcon =
                RenderIcon(
                    bytes,
                    HeroIconPixels,
                    MaximumHeroIconSourceDimension
                );

            if (
                !CanApplyIcon(
                    grid,
                    rowIndex,
                    cancellationToken
                ) ||
                grid.Rows[rowIndex].Tag is not
                    DeadlockMatchPlayerDetailsEntry rowPlayer ||
                !String.Equals(
                    rowPlayer.HeroIconUrl,
                    heroIconUrl,
                    StringComparison.OrdinalIgnoreCase
                )
            )
            {
                return;
            }

            if (
                !_heroIconCache.TryGetValue(
                    heroIconUrl,
                    out var icon
                )
            )
            {
                icon =
                    new Bitmap(
                        renderedIcon
                    );

                _heroIconCache[heroIconUrl] =
                    icon;
            }

            grid.Rows[rowIndex]
                .Cells["Hero"].Value =
                    icon;
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch
        {
            // Missing hero art must not break a saved match window.
        }
    }

    private void QueueRankIconLoad(
        DataGridView grid,
        int rowIndex,
        byte rank,
        byte subrank
    )
    {
        var cacheKey =
            rank * 10 +
            subrank;

        if (
            _rankIconCache.TryGetValue(
                cacheKey,
                out var cachedImage
            )
        )
        {
            grid.Rows[rowIndex]
                .Cells["Rank"].Value =
                    cachedImage;

            return;
        }

        _ = LoadRankIconAsync(
            grid,
            rowIndex,
            rank,
            subrank
        );
    }

    private async Task LoadRankIconAsync(
        DataGridView grid,
        int rowIndex,
        byte rank,
        byte subrank
    )
    {
        try
        {
            var cancellationToken =
                _lifetimeCancellation.Token;

            var bytes =
                await SharedRankImageService.GetPngAsync(
                    rank,
                    subrank,
                    cancellationToken
                );

            using var renderedIcon =
                RenderIcon(
                    bytes,
                    RankIconPixels,
                    MaximumRankIconSourceDimension
                );

            if (
                !CanApplyIcon(
                    grid,
                    rowIndex,
                    cancellationToken
                ) ||
                grid.Rows[rowIndex].Tag is not
                    DeadlockMatchPlayerDetailsEntry rowPlayer ||
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

            var cacheKey =
                rank * 10 +
                subrank;

            if (
                !_rankIconCache.TryGetValue(
                    cacheKey,
                    out var icon
                )
            )
            {
                icon =
                    new Bitmap(
                        renderedIcon
                    );

                _rankIconCache[cacheKey] =
                    icon;
            }

            grid.Rows[rowIndex]
                .Cells["Rank"].Value =
                    icon;
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch
        {
            // Missing rank art must not break a saved match window.
        }
    }

    private bool CanApplyIcon(
        DataGridView grid,
        int rowIndex,
        CancellationToken cancellationToken
    )
    {
        return
            !_resourcesDisposed &&
            !cancellationToken.IsCancellationRequested &&
            !IsDisposed &&
            !Disposing &&
            !grid.IsDisposed &&
            rowIndex >= 0 &&
            rowIndex < grid.Rows.Count;
    }

    private static async Task<byte[]?>
        ReadLimitedBytesAsync(
            HttpContent content,
            int maximumBytes,
            CancellationToken cancellationToken
        )
    {
        await using var input =
            await content.ReadAsStreamAsync(
                cancellationToken
            );

        using var output =
            new MemoryStream();

        var buffer =
            new byte[64 * 1024];

        while (true)
        {
            var bytesRead =
                await input.ReadAsync(
                    buffer.AsMemory(),
                    cancellationToken
                );

            if (bytesRead == 0)
            {
                break;
            }

            if (
                output.Length +
                    bytesRead >
                maximumBytes
            )
            {
                return null;
            }

            await output.WriteAsync(
                buffer.AsMemory(
                    0,
                    bytesRead
                ),
                cancellationToken
            );
        }

        return output.ToArray();
    }

    private static Bitmap RenderIcon(
        byte[] bytes,
        int displayPixels,
        int maximumSourceDimension
    )
    {
        if (
            !TryReadPngDimensions(
                bytes,
                maximumSourceDimension,
                out var encodedWidth,
                out var encodedHeight
            )
        )
        {
            throw new InvalidDataException(
                "The downloaded icon is not a valid PNG."
            );
        }

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
            sourceImage.Width != encodedWidth ||
            sourceImage.Height != encodedHeight
        )
        {
            throw new InvalidDataException(
                "The decoded icon dimensions do not match its PNG header."
            );
        }

        var renderedIcon =
            new Bitmap(
                displayPixels,
                displayPixels,
                PixelFormat.Format32bppPArgb
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

            var scale =
                Math.Min(
                    displayPixels /
                        (double)sourceImage.Width,
                    displayPixels /
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

            graphics.DrawImage(
                sourceImage,
                new Rectangle(
                    (displayPixels - drawWidth) / 2,
                    (displayPixels - drawHeight) / 2,
                    drawWidth,
                    drawHeight
                ),
                0,
                0,
                sourceImage.Width,
                sourceImage.Height,
                GraphicsUnit.Pixel
            );

            return renderedIcon;
        }
        catch
        {
            renderedIcon.Dispose();
            throw;
        }
    }

    /*
     * Bootstrap Icons "heart-fill":
     * https://icons.getbootstrap.com/icons/heart-fill/
     *
     * The MIT License (MIT)
     * Copyright (c) 2019-2024 The Bootstrap Authors
     *
     * Permission is hereby granted, free of charge, to any person obtaining
     * a copy of this software and associated documentation files (the
     * "Software"), to deal in the Software without restriction, including
     * without limitation the rights to use, copy, modify, merge, publish,
     * distribute, sublicense, and/or sell copies of the Software, and to
     * permit persons to whom the Software is furnished to do so, subject to
     * the following conditions:
     *
     * The above copyright notice and this permission notice shall be included
     * in all copies or substantial portions of the Software.
     *
     * THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND,
     * EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF
     * MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT.
     * IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY
     * CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT,
     * TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE
     * SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
     */
    private static Bitmap CreateReactionHeaderIcon()
    {
        var image =
            new Bitmap(
                ReactionHeaderIconPixels,
                ReactionHeaderIconPixels,
                PixelFormat.Format32bppPArgb
            );

        try
        {
            using var graphics =
                Graphics.FromImage(
                    image
                );

            graphics.Clear(
                Color.Transparent
            );

            graphics.CompositingMode =
                CompositingMode.SourceOver;

            graphics.CompositingQuality =
                CompositingQuality.HighQuality;

            graphics.PixelOffsetMode =
                PixelOffsetMode.HighQuality;

            graphics.SmoothingMode =
                SmoothingMode.AntiAlias;

            using var path =
                new GraphicsPath();

            path.StartFigure();

            path.AddBezier(
                new PointF(
                    8F,
                    1.314F
                ),
                new PointF(
                    12.438F,
                    -3.248F
                ),
                new PointF(
                    23.534F,
                    4.735F
                ),
                new PointF(
                    8F,
                    15F
                )
            );

            path.AddBezier(
                new PointF(
                    8F,
                    15F
                ),
                new PointF(
                    -7.534F,
                    4.736F
                ),
                new PointF(
                    3.562F,
                    -3.248F
                ),
                new PointF(
                    8F,
                    1.314F
                )
            );

            path.CloseFigure();

            using var brush =
                new SolidBrush(
                    BridgeUiTheme.TextMuted
                );

            graphics.FillPath(
                brush,
                path
            );

            return image;
        }
        catch
        {
            image.Dispose();
            throw;
        }
    }

    private static Bitmap CreateReactionIcon(
        string pngBase64,
        Color tint,
        float opacity
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

        var renderedIcon =
            new Bitmap(
                ReactionIconPixels,
                ReactionIconPixels,
                PixelFormat.Format32bppPArgb
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
                        tint.R / 255F,

                    Matrix11 =
                        tint.G / 255F,

                    Matrix22 =
                        tint.B / 255F,

                    Matrix33 =
                        Math.Clamp(
                            opacity,
                            0F,
                            1F
                        ),

                    Matrix44 =
                        1F
                },
                ColorMatrixFlag.Default,
                ColorAdjustType.Bitmap
            );

            graphics.DrawImage(
                sourceImage,
                new Rectangle(
                    0,
                    0,
                    ReactionIconPixels,
                    ReactionIconPixels
                ),
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

    private static bool TryReadPngDimensions(
        byte[] bytes,
        int maximumSourceDimension,
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
            width <= maximumSourceDimension &&
            height <= maximumSourceDimension &&
            (long)width * height <=
                (long)maximumSourceDimension *
                maximumSourceDimension;
    }

    private void OnMatchGridCellContentClick(
        object? sender,
        DataGridViewCellEventArgs e
    )
    {
        if (
            sender is not DataGridView grid ||
            e.RowIndex < 0 ||
            e.RowIndex >= grid.Rows.Count ||
            e.ColumnIndex < 0 ||
            e.ColumnIndex >= grid.Columns.Count ||
            !String.Equals(
                grid.Columns[e.ColumnIndex].Name,
                "Hero",
                StringComparison.Ordinal
            ) ||
            grid.Rows[e.RowIndex].Tag is not
                DeadlockMatchPlayerDetailsEntry player ||
            !CanOpenSteamProfile(player)
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
                            "https://steamcommunity.com/profiles/" +
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
            return "Rank module was disabled for this snapshot.";
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

        return player.RankStatus?.ToLowerInvariant() switch
        {
            "loading" =>
                "Rank was still loading when this snapshot was saved.",

            "unranked" =>
                "Player is unranked.",

            "protected" =>
                "Player rank is protected.",

            "not_found" =>
                "Player rank was not found.",

            "error" =>
                "Failed to load player rank.",

            "ok" =>
                "Saved player rank data is invalid.",

            _ =>
                "Player rank is unavailable."
        };
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
            return BridgeUiTheme.ServiceCompleted;
        }

        if (winRatePercent < 45)
        {
            return BridgeUiTheme.ServiceError;
        }

        return BridgeUiTheme.Text;
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

    private static HttpClient CreateSharedRankHttpClient()
    {
        var client =
            new HttpClient
            {
                BaseAddress =
                    new Uri(
                        "https://api.deadlock-api.com/"
                    ),

                Timeout =
                    TimeSpan.FromSeconds(30)
            };

        client.DefaultRequestHeaders
            .UserAgent
            .ParseAdd(
                "DeadlockThreatHud/1.0"
            );

        return client;
    }

    protected override void OnShown(
        EventArgs e
    )
    {
        base.OnShown(
            e
        );

        AdjustWindowHeightForContent();

        _ = LoadReactionsAsync();
    }

    private void AdjustWindowHeightForContent()
    {
        if (
            _resourcesDisposed ||
            IsDisposed ||
            Disposing ||
            WindowState !=
                FormWindowState.Normal
        )
        {
            return;
        }

        PerformLayout();
        _alliesGrid.PerformLayout();
        _enemiesGrid.PerformLayout();

        var missingHeight =
            Math.Max(
                GetMissingGridHeight(
                    _alliesGrid
                ),
                GetMissingGridHeight(
                    _enemiesGrid
                )
            );

        if (missingHeight <= 0)
        {
            return;
        }

        var workingArea =
            Screen.FromControl(
                this
            )
                .WorkingArea;

        var targetHeight =
            Math.Min(
                workingArea.Height,
                Height +
                    missingHeight
            );

        if (targetHeight <= Height)
        {
            return;
        }

        var targetTop =
            Math.Clamp(
                Top,
                workingArea.Top,
                workingArea.Bottom -
                    targetHeight
            );

        SetBounds(
            Left,
            targetTop,
            Width,
            targetHeight
        );
    }

    private static int GetMissingGridHeight(
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

    private void ClearImageCaches()
    {
        foreach (var image in _heroIconCache.Values)
        {
            image.Dispose();
        }

        _heroIconCache.Clear();

        foreach (var image in _rankIconCache.Values)
        {
            image.Dispose();
        }

        _rankIconCache.Clear();
    }

    protected override void Dispose(
        bool disposing
    )
    {
        if (
            disposing &&
            !_resourcesDisposed
        )
        {
            _resourcesDisposed =
                true;

            _alliesGrid.CellContentClick -=
                OnMatchGridCellContentClick;

            _enemiesGrid.CellContentClick -=
                OnMatchGridCellContentClick;

            UnsubscribeReactionGridEvents(
                _alliesGrid
            );

            UnsubscribeReactionGridEvents(
                _enemiesGrid
            );

            _lifetimeCancellation.Cancel();
            _heroImageHttpClient.Dispose();

            ClearImageCaches();
            _rankUnavailableIcon.Dispose();
            _reactionHeaderIcon.Dispose();
            _reactionLikeNeutralIcon.Dispose();
            _reactionDislikeNeutralIcon.Dispose();
            _reactionLikeSelectedIcon.Dispose();
            _reactionDislikeSelectedIcon.Dispose();
            _reactionLikeDisabledIcon.Dispose();
            _reactionDislikeDisabledIcon.Dispose();
            _lifetimeCancellation.Dispose();
        }

        base.Dispose(
            disposing
        );
    }

    /*
     * Keep the exact supplied PNG files inside the single-file Bridge build.
     * This avoids depending on loose image files next to the executable.
     */
    private const string ReactionLikePngBase64 =
        "iVBORw0KGgoAAAANSUhEUgAAAEAAAABACAYAAACqaXHeAAAEkklEQVR42u2ay4scVRTGf6e6ZybBMQ8m2SiKRJKNSw0iQtBN" +
        "YMQ/IAuJunHlAxdxoUvXIj4WgiKo4EoQQcGsgm5EBIOGEXQmIkIwKB0TIS7S3fW56HP12unqqurumq5+HCiqn1X3++45537n" +
        "3IKlLW1pS6uxSTJJtqjAG9H7hqRkUcAn0ev9kvYP+m5ewTf9fJukdyX9Iel3SZ9Iun+uSQjAJN0h6VfdbG1Jj4aQmLuY9/NJ" +
        "SZciwKkfbUldPx6bKxIkJZ70jjhYOdB+6/j5R0lr4X/j3LsusZSYmYDTgAHtjLE1gA5wDDhlZmmNMIy95DUlbQ+ZfUXfpe4F" +
        "+8bVCUkNwIc4PgEcAfJmNXx3N3DUPcdmOgQcxOM+njSPMwf8F3Ax+mz2CHDXTSUdBjYdSKMAAQC/ANclmRM4kx7Q8MFvAod9" +
        "9q0gARfNrD0uhmkTENz9iRJuLD+2QgTNJAGSGmaWSnoIeLig+wfABvwwbvzXIvtLOtcncoZZUIXXJR2d2bogAn+ywLo/SAl+" +
        "NinwyRTAW++kNeD1ES/zYc2U7Eizf7qE6yuqES5LWo8LqFmTvIk3OXYcVFH3b/v57UlWgs0iJWoBFVdm3e9Iet6lbCdvDAPC" +
        "9R0fV24NMI5AKsyw9+qs4OybpIPe5elGbp1nwUu+K5P4ipTLzSHsdSXtBfZE+rtfjcnMrhX0mjD7LwOHgG6JJBbu9xGwz9tm" +
        "nSEiSD0I9mekObpZomKQ2xvwIvAUcEvGjeQgPgVeAFp5bifpmCu4JBI0Zexan/BRTsF0FnjazFqSmmbWKZqhX1I5O+vu1pB0" +
        "wN08nA9K2vD3H5TM/JOwnyQ9OExW9rvuOrADbBRcazvACnAv8D3ws19j0L0ODAinUqmp5O+7HuYd4AzwFnDDO0k35wAzk6QV" +
        "YNV1eZHBht/d6udDwN4cLT9y62CEVS71Mb4KnDezL+Kc0MxgOR1hYN2IdWWQNw3hkviYbFCxlUxwlladuO3o/9Z3TK3p5FhV" +
        "ZS0QOrtXa1qmqmoCCqnLKXtBczcIqOPMm+uUrb5O1EIQEPqMb5rZb74CaFFCILTZrgBvhC50VSEQsnyrZrMP8IqZtaJEXSkB" +
        "52sUCv+OKatIm/cQCB6wmbWFNu9JMAB+wD2guxsE1MlCnXIPcJfXOskieUAYy9/AjUHjW4QcEDTApX4NMGkCwoW/nWLlN0gD" +
        "tLI0QFUEtGpCQAD7WpYGqCoE1mq2Apwb1hmuQghdrdlKsDqsUTtJAsIae6qGxRC7QcAVSc8Az1Gu51/1EnjG135VRUBw/duB" +
        "Zyn2mMtuiaAUeAS4zx/GKNwT1Ahs76HXDdaUCYhbX8H918uEQFg/uyVi3+jtB3zMf09zplM6QgO27aJsG/g6Swf8H7lvdEp6" +
        "L9qS7g45wpb1jqQV3wX6XNO1y9HrC5KOO7ZkWPwGAsL7DeB9eo+v5dkW8KSZfRNd5wTFHniqwi4Ad9LbhfrSN2QznyW0HI84" +
        "Pix+3P2/MrN2IG+sPfkqEoKUxFthhQgoC6b/JlN+jj/OA2kehjwPKAIkrdusL21pS1va0graP8vZcKZ8y4ZuAAAAAElFTkSu" +
        "QmCC";

    private const string ReactionDislikePngBase64 =
        "iVBORw0KGgoAAAANSUhEUgAAAEAAAABACAYAAACqaXHeAAAEi0lEQVR42u2aP6gcVRTGf2d2n3+SkLcvW6kokvAsTJWAvEIQ" +
        "bATBxlZF0MLGpxaihYJoY5NKYyeERBFsBTGmitgE0UZCwBiLIATEZ5D4P+7OfBbv3OSyzszO7L7dnd23B4a7j503c+93v3Pu" +
        "d85ZWNrSlra0XWxW9qWkVoVnZGamhQJAkgFUXZikxMyymsBNyjJfl1XZnGEMeADYV3JLCpwzs15d0KZlg5tTCkBYBNAFPgAe" +
        "rfCOC8AzZvZ19JyHgFmx4DxwD9ABvjSzviSrtDGSWpJM0iltW09SWnL1/L4fJK1IWpP0uWZrP0WfzzuLkZSUMiCgJKkDXHbq" +
        "J8PcBOgDbeAo8CywCfRmyICw0B6wAlwCjgB/5bloUgBKWmMBLUDAQeBx/9+2P3sWl/xa8c1ZBzZ84UkRWrWCY8H9/wB/++dZ" +
        "BkKL5h/W98cwuowVaH28Arwb7cKsLfW5fAZ846dBOgkAgh0ws+PAO+4WWUM0zjE/Bm1SDIhjAcDHDRN7ychfjugKnYYB8G+k" +
        "byYKQLDrTRGBPj5cJoKSCfhcd2ACs6b+S5K6QJbHhEkAcLQhAAQ90wVeqKsDxrF+wwKggE1Jd5lZOsiCZELINy3d3wPckje/" +
        "hMW21Bd8Abjs+U62mxgQ4tA5jwGt3RgDAE6772saQuhIg5hwY05FWmASAHQbyICXi7TAortA0AIHirTAogfBQS1wx6AW2A0A" +
        "hODXBQ4PrnvRXWAwRvUnGQRDgOk0mAn/m1N7SESvlXc7oOvR/6sh7iFudoyGAmAjMCNWWa0itGdkGTfL+2kpAE7hnu9mWtFN" +
        "UrZL0L/7S34hv50W3ENjgFOXmaFE3wdeAb4qKo4GAFo+vl6zG3NGUuKdpY53iMK4Jqnrf3/o9/en2Cn6XtKDlY8sZ4EBrwHP" +
        "AXsLdkyO8KfAq8BVKG+OSrrPM7NkRDe5NsAClTDFgDPAppldldQ2s36tM1vS7cBtObS9EeTM7FoOgHnW8kble8Dz1Os+hXvf" +
        "AI5HtLYyAMzs18DsQtqXLL5V9b6yqmsMjF9rkra8uZpVpHHq47dFTc6CdyZV5lZl0oVXzee1fXwz6j6rJggb/u72Ts5tOofx" +
        "9sQSSaveUs+ihQ2zANb7dRjaRBDCSfN0zRMhi/r/+4bEm0YDEFhwq6TvBug9zAJYT8YutRMFg+kJ8u1j0szsOvDiiI95IlJ5" +
        "zLsrnK3hCplff0paD5F+rhiQY2/VSJTMd33PTtUeZwaAV2YSM/sCOFuUrBSIHAH3zzUAA+8/WWMhQUIfHiN1bwwAoT53Gtii" +
        "2s9rAlCHJK2MGwhnCkCo0prZloNQxQ0CAPcCe/2nfTavDIgTqFNR8aJKkXM/cKih5beRxFFb0qUKwigkURcl7R9X9zelO9zy" +
        "XP2jKP0tK3EZ8LaZ/eYupHlnQOI7eTDS/GmJFL7oUjqZy3ygJA4g6RFJV6IMMKi/+IfbT811RljGBB/vlvRjQUr82EIuPqdo" +
        "cqekE149+lnSJ5I2dkL/zw0T/POqpNW87xYdBItp7rXH3bH4HCCMpS1taUubgP0HLeaoOdHs7nAAAAAASUVORK5CYII=";

    private readonly record struct HistoricalDamageValue(
        uint HeroId,
        long HeroDamage,
        long Tick
    );
}
