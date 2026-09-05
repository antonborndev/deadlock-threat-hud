using System.Drawing;
using System.Globalization;
using System.Windows.Forms;

internal sealed class MatchHistoryListControl :
    UserControl
{
    private const int PageSize =
        10;

    private readonly MatchHistoryStore _historyStore;
    private readonly DataGridView _grid;
    private readonly Label _pageLabel;
    private readonly Button _previousButton;
    private readonly Button _nextButton;

    private readonly System.Windows.Forms.Timer
        _refreshTimer =
            new()
            {
                Interval =
                    10_000
            };

    private readonly CancellationTokenSource
        _lifetimeCancellation =
            new();

    private int _pageIndex;
    private bool _loading;
    private bool _openingMatchDetails;
    private bool _lifetimeCancellationDisposed;
    private string? _displayedPageFingerprint;

    public MatchHistoryListControl(
        MatchHistoryStore historyStore
    )
    {
        _historyStore =
            historyStore ??
            throw new ArgumentNullException(
                nameof(historyStore)
            );

        BackColor =
            BridgeUiTheme.Surface;

        ForeColor =
            BridgeUiTheme.Text;

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
                    new Padding(
                        10
                    ),

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

        _grid =
            CreateGrid();

        _grid.CellContentClick +=
            OnHistoryGridCellContentClick;

        root.Controls.Add(
            _grid,
            0,
            0
        );

        var footer =
            new TableLayoutPanel
            {
                Dock =
                    DockStyle.Fill,

                AutoSize =
                    true,

                ColumnCount =
                    5,

                RowCount =
                    1,

                BackColor =
                    BackColor,

                Margin =
                    new Padding(
                        0,
                        10,
                        0,
                        0
                    )
            };

        footer.ColumnStyles.Add(
            new ColumnStyle(
                SizeType.AutoSize
            )
        );

        footer.ColumnStyles.Add(
            new ColumnStyle(
                SizeType.AutoSize
            )
        );

        footer.ColumnStyles.Add(
            new ColumnStyle(
                SizeType.AutoSize
            )
        );

        footer.ColumnStyles.Add(
            new ColumnStyle(
                SizeType.AutoSize
            )
        );

        footer.ColumnStyles.Add(
            new ColumnStyle(
                SizeType.Percent,
                100F
            )
        );

        _previousButton =
            BridgeUiTheme.CreateButton(
                "Back",
                88
            );

        _previousButton.Enabled =
            false;

        _pageLabel =
            new Label
            {
                AutoSize =
                    true,

                Text =
                    "—",

                ForeColor =
                    BridgeUiTheme.TextMuted,

                Anchor =
                    AnchorStyles.Left,

                Margin =
                    new Padding(
                        16,
                        0,
                        16,
                        0
                    )
            };

        _nextButton =
            BridgeUiTheme.CreateButton(
                "Next",
                88
            );

        _nextButton.Enabled =
            false;

        var refreshButton =
            BridgeUiTheme.CreateButton(
                "Refresh",
                100
            );

        refreshButton.Margin =
            new Padding(
                10,
                0,
                0,
                0
            );

        _previousButton.Click +=
            async (_, _) =>
            {
                if (
                    _pageIndex <= 0 ||
                    _loading
                )
                {
                    return;
                }

                _pageIndex--;

                await RefreshAsync();
            };

        _nextButton.Click +=
            async (_, _) =>
            {
                if (_loading)
                {
                    return;
                }

                _pageIndex++;

                await RefreshAsync();
            };

        refreshButton.Click +=
            async (_, _) =>
                await RefreshAsync();

        _refreshTimer.Tick +=
            async (_, _) =>
            {
                if (Visible)
                {
                    await RefreshAsync(
                        showErrorDialog:
                            false
                    );
                }
            };

        VisibleChanged +=
            (_, _) =>
            {
                if (Visible)
                {
                    _refreshTimer.Start();
                }
                else
                {
                    _refreshTimer.Stop();
                }
            };

        footer.Controls.Add(
            _previousButton,
            0,
            0
        );

        footer.Controls.Add(
            _pageLabel,
            1,
            0
        );

        footer.Controls.Add(
            _nextButton,
            2,
            0
        );

        footer.Controls.Add(
            refreshButton,
            3,
            0
        );

        root.Controls.Add(
            footer,
            0,
            1
        );
    }

    public async Task RefreshAsync(
        bool showErrorDialog = true
    )
    {
        if (
            _loading ||
            _lifetimeCancellationDisposed ||
            IsDisposed ||
            Disposing
        )
        {
            return;
        }

        _loading =
            true;

        _previousButton.Enabled =
            false;

        _nextButton.Enabled =
            false;

        var cancellationToken =
            _lifetimeCancellation.Token;

        try
        {
            var page =
                await _historyStore.GetPageAsync(
                    _pageIndex,
                    PageSize,
                    cancellationToken
                );

            if (
                IsDisposed ||
                Disposing
            )
            {
                return;
            }

            var totalPages =
                Math.Max(
                    1,
                    (
                        page.TotalCount +
                        PageSize -
                        1
                    ) /
                    PageSize
                );

            if (
                _pageIndex >=
                    totalPages
            )
            {
                _pageIndex =
                    totalPages -
                    1;

                page =
                    await _historyStore.GetPageAsync(
                        _pageIndex,
                        PageSize,
                        cancellationToken
                    );

                if (
                    IsDisposed ||
                    Disposing
                )
                {
                    return;
                }
            }

            var pageFingerprint =
                CreatePageFingerprint(
                    _pageIndex,
                    page.TotalCount,
                    page.Items
                );

            if (
                !String.Equals(
                    _displayedPageFingerprint,
                    pageFingerprint,
                    StringComparison.Ordinal
                )
            )
            {
                _grid.Rows.Clear();

                for (
                    var index = 0;
                    index < page.Items.Count;
                    index++
                )
                {
                    var item =
                        page.Items[index];

                    var rowIndex =
                        _grid.Rows.Add(
                            item.MatchId.ToString(
                                CultureInfo.InvariantCulture
                            ),
                            item.AddedAtUtc
                                .ToLocalTime()
                                .ToString(
                                    "yyyy-MM-dd HH:mm:ss",
                                    CultureInfo.InvariantCulture
                                ),
                            item.LocalPlayerWon switch
                            {
                                true =>
                                    "VICTORY",

                                false =>
                                    "DEFEAT",

                                null =>
                                    String.Empty
                            }
                        );

                    var row =
                        _grid.Rows[
                            rowIndex
                        ];

                    row.Tag =
                        item.MatchId;

                    if (
                        item.LocalPlayerWon is
                            bool localPlayerWon
                    )
                    {
                        var resultColor =
                            localPlayerWon
                                ? BridgeUiTheme
                                    .ServiceCompleted
                                : BridgeUiTheme
                                    .ServiceError;

                        var resultCell =
                            row.Cells[
                                "Result"
                            ];

                        resultCell.Style.ForeColor =
                            resultColor;

                        resultCell.Style
                            .SelectionForeColor =
                                resultColor;
                    }
                }

                _displayedPageFingerprint =
                    pageFingerprint;
            }

            _pageLabel.Text =
                page.TotalCount == 0
                    ? "No saved matches"
                    : "Page " +
                        (_pageIndex + 1).ToString(
                            CultureInfo.InvariantCulture
                        ) +
                        " of " +
                        totalPages.ToString(
                            CultureInfo.InvariantCulture
                        ) +
                        " · Total: " +
                        page.TotalCount.ToString(
                            CultureInfo.InvariantCulture
                        );

            _previousButton.Enabled =
                _pageIndex >
                    0;

            _nextButton.Enabled =
                _pageIndex +
                    1 <
                totalPages;
        }
        catch (
            OperationCanceledException
        )
        {
        }
        catch (Exception error)
        {
            if (
                IsDisposed ||
                Disposing
            )
            {
                return;
            }

            _pageLabel.Text =
                "Load error";

            if (showErrorDialog)
            {
                MessageBox.Show(
                    this,
                    "Failed to load match history." +
                    Environment.NewLine +
                    Environment.NewLine +
                    error.Message,
                    "Threat HUD Bridge",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error
                );
            }
        }
        finally
        {
            _loading =
                false;
        }
    }

    private async void OnHistoryGridCellContentClick(
        object? sender,
        DataGridViewCellEventArgs e
    )
    {
        if (
            _openingMatchDetails ||
            _lifetimeCancellationDisposed ||
            IsDisposed ||
            Disposing ||
            !ReferenceEquals(
                sender,
                _grid
            ) ||
            e.RowIndex < 0 ||
            e.RowIndex >=
                _grid.Rows.Count ||
            e.ColumnIndex < 0 ||
            e.ColumnIndex >=
                _grid.Columns.Count ||
            !String.Equals(
                _grid.Columns[
                    e.ColumnIndex
                ].Name,
                "MatchId",
                StringComparison.Ordinal
            ) ||
            _grid.Rows[
                e.RowIndex
            ].Tag is not ulong matchId
        )
        {
            return;
        }

        _openingMatchDetails =
            true;

        UseWaitCursor =
            true;

        try
        {
            var cancellationToken =
                _lifetimeCancellation.Token;

            var entry =
                await Task.Run(
                    async () =>
                        await _historyStore
                            .GetSnapshotAsync(
                                matchId,
                                cancellationToken
                            )
                            .ConfigureAwait(
                                false
                            ),
                    cancellationToken
                );

            if (
                _lifetimeCancellationDisposed ||
                IsDisposed ||
                Disposing
            )
            {
                return;
            }

            if (entry is null)
            {
                throw new InvalidDataException(
                    "The selected match is no longer present in history."
                );
            }

            var snapshot =
                MatchHistorySnapshotReader.Read(
                    entry
                );

            var detailsForm =
                new MatchHistoryDetailsForm(
                    entry,
                    snapshot
                );

            try
            {
                var owner =
                    FindForm();

                if (
                    owner is not null &&
                    !owner.IsDisposed &&
                    !owner.Disposing
                )
                {
                    detailsForm.Show(
                        owner
                    );
                }
                else
                {
                    detailsForm.Show();
                }
            }
            catch
            {
                detailsForm.Dispose();
                throw;
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception error)
        {
            if (
                _lifetimeCancellationDisposed ||
                IsDisposed ||
                Disposing
            )
            {
                return;
            }

            MessageBox.Show(
                this,
                "Failed to open the saved match." +
                Environment.NewLine +
                Environment.NewLine +
                error.Message,
                "Threat HUD Bridge",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error
            );
        }
        finally
        {
            if (
                !IsDisposed &&
                !Disposing
            )
            {
                UseWaitCursor =
                    false;
            }

            _openingMatchDetails =
                false;
        }
    }

    protected override void Dispose(
        bool disposing
    )
    {
        if (
            disposing &&
            !_lifetimeCancellationDisposed
        )
        {
            _lifetimeCancellationDisposed =
                true;

            _grid.CellContentClick -=
                OnHistoryGridCellContentClick;

            _refreshTimer.Stop();
            _refreshTimer.Dispose();

            _lifetimeCancellation.Cancel();
            _lifetimeCancellation.Dispose();
        }

        base.Dispose(
            disposing
        );
    }

    private static string CreatePageFingerprint(
        int pageIndex,
        int totalCount,
        IReadOnlyList<MatchHistoryListItem> items
    )
    {
        var parts =
            new string[
                items.Count +
                1
            ];

        parts[0] =
            pageIndex.ToString(
                CultureInfo.InvariantCulture
            ) +
            ":" +
            totalCount.ToString(
                CultureInfo.InvariantCulture
            );

        for (
            var index = 0;
            index < items.Count;
            index++
        )
        {
            var item =
                items[index];

            parts[index + 1] =
                item.MatchId.ToString(
                    CultureInfo.InvariantCulture
                ) +
                ":" +
                item.AddedAtUtc
                    .UtcDateTime
                    .Ticks
                    .ToString(
                        CultureInfo.InvariantCulture
                    ) +
                ":" +
                (
                    item.LocalPlayerWon switch
                    {
                        true =>
                            "1",

                        false =>
                            "0",

                        null =>
                            "-"
                    }
                );
        }

        return String.Join(
            "|",
            parts
        );
    }

    private static DataGridView CreateGrid()
    {
        var grid =
            new DataGridView
            {
                Dock =
                    DockStyle.Fill,

                AllowUserToAddRows =
                    false,

                AllowUserToDeleteRows =
                    false,

                AllowUserToResizeRows =
                    false,

                AutoGenerateColumns =
                    false,

                BackgroundColor =
                    BridgeUiTheme.Surface,

                BorderStyle =
                    BorderStyle.None,

                CellBorderStyle =
                    DataGridViewCellBorderStyle
                        .SingleHorizontal,

                ColumnHeadersBorderStyle =
                    DataGridViewHeaderBorderStyle
                        .None,

                ColumnHeadersHeight =
                    32,

                ColumnHeadersHeightSizeMode =
                    DataGridViewColumnHeadersHeightSizeMode
                        .DisableResizing,

                EnableHeadersVisualStyles =
                    false,

                MultiSelect =
                    false,

                ReadOnly =
                    true,

                RowHeadersVisible =
                    false,

                SelectionMode =
                    DataGridViewSelectionMode
                        .FullRowSelect
            };

        grid.RowTemplate.Height =
            31;

        grid.RowTemplate.Resizable =
            DataGridViewTriState.False;

        grid.ColumnHeadersDefaultCellStyle
            .BackColor =
                BridgeUiTheme.SurfaceRaised;

        grid.ColumnHeadersDefaultCellStyle
            .ForeColor =
                BridgeUiTheme.Text;

        grid.ColumnHeadersDefaultCellStyle
            .SelectionBackColor =
                BridgeUiTheme.SurfaceRaised;

        grid.ColumnHeadersDefaultCellStyle
            .SelectionForeColor =
                BridgeUiTheme.Text;

        grid.ColumnHeadersDefaultCellStyle
            .Padding =
                new Padding(
                    5,
                    0,
                    5,
                    0
                );

        grid.DefaultCellStyle.BackColor =
            BridgeUiTheme.Surface;

        grid.DefaultCellStyle.ForeColor =
            Color.Gainsboro;

        grid.DefaultCellStyle
            .SelectionBackColor =
                BridgeUiTheme.SurfaceRaised;

        grid.DefaultCellStyle
            .SelectionForeColor =
                Color.White;

        grid.DefaultCellStyle.Padding =
            new Padding(
                5,
                0,
                5,
                0
            );

        grid.GridColor =
            BridgeUiTheme.Border;

        grid.Columns.Add(
            new DataGridViewLinkColumn
            {
                Name =
                    "MatchId",

                HeaderText =
                    "MATCH ID",

                AutoSizeMode =
                    DataGridViewAutoSizeColumnMode
                        .Fill,

                FillWeight =
                    40F,

                LinkColor =
                    Color.LightSkyBlue,

                ActiveLinkColor =
                    Color.White,

                VisitedLinkColor =
                    Color.LightSkyBlue,

                TrackVisitedState =
                    false,

                LinkBehavior =
                    LinkBehavior.HoverUnderline
            }
        );

        grid.Columns.Add(
            new DataGridViewTextBoxColumn
            {
                Name =
                    "AddedAt",

                HeaderText =
                    "ADDED",

                AutoSizeMode =
                    DataGridViewAutoSizeColumnMode
                        .Fill,

                FillWeight =
                    40F
            }
        );

        grid.Columns.Add(
            new DataGridViewTextBoxColumn
            {
                Name =
                    "Result",

                HeaderText =
                    "RESULT",

                AutoSizeMode =
                    DataGridViewAutoSizeColumnMode
                        .Fill,

                FillWeight =
                    20F
            }
        );

        return grid;
    }
}
