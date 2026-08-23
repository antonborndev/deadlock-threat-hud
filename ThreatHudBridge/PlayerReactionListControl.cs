using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;

internal sealed class PlayerReactionListControl :
    UserControl
{
    private const int PageSize =
        10;

    /*
     * SteamID64 public individual account:
     *
     * 76561197960265728 + accountId
     */
    private const ulong SteamId64IndividualBase =
        76561197960265728UL;

    private readonly PlayerReactionStore
        _reactionStore =
            new();

    private readonly DataGridView
        _grid;

    private readonly Label
        _pageLabel;

    private readonly Button
        _previousButton;

    private readonly Button
        _nextButton;

    private int _pageIndex;
    private bool _storeInitialized;
    private bool _loading;

    public PlayerReactionListControl()
    {
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
            GridCellContentClick;

        root.Controls.Add(
            _grid,
            0,
            0
        );

        /*
         * TableLayoutPanel instead of FlowLayoutPanel
         * guarantees the same vertical
         * position for buttons and the page label.
         */
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

    public async Task RefreshAsync()
    {
        if (
            _loading ||
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

        try
        {
            if (!_storeInitialized)
            {
                await _reactionStore
                    .InitializeAsync(
                        CancellationToken.None
                    );

                _storeInitialized =
                    true;
            }

            var page =
                await _reactionStore
                    .GetPageAsync(
                        _pageIndex,
                        PageSize,
                        CancellationToken.None
                    );

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
                    await _reactionStore
                        .GetPageAsync(
                            _pageIndex,
                            PageSize,
                            CancellationToken.None
                        );
            }

            _grid.Rows.Clear();

            for (
                var index = 0;
                index < page.Items.Count;
                index++
            )
            {
                var item =
                    page.Items[index];

                var steamId64 =
                    SteamId64IndividualBase +
                    item.AccountId;

                var reactionText =
                    item.Reaction ==
                        PlayerReactionValue.Like
                        ? "Like (+1)"
                        : "Dislike (-1)";

                var rowIndex =
                    _grid.Rows.Add(
                        steamId64.ToString(),
                        reactionText,
                        item.UpdatedAtUtc
                            .ToLocalTime()
                            .ToString(
                                "yyyy-MM-dd HH:mm:ss"
                            )
                    );

                var row =
                    _grid.Rows[
                        rowIndex
                    ];

                row.Tag =
                    item;

                row.Cells[
                    "Reaction"
                ].Style.ForeColor =
                    item.Reaction ==
                        PlayerReactionValue.Like
                        ? Color.LightGreen
                        : Color.LightCoral;
            }

            _pageLabel.Text =
                page.TotalCount == 0
                    ? "No saved reactions"
                    : $"Page " +
                        $"{_pageIndex + 1} " +
                        $"of {totalPages} · " +
                        $"Total: {page.TotalCount}";

            _previousButton.Enabled =
                _pageIndex >
                    0;

            _nextButton.Enabled =
                _pageIndex +
                    1 <
                    totalPages;
        }
        catch (Exception error)
        {
            _pageLabel.Text =
                "Load error";

            ShowError(
                "Failed to load reactions.",
                error
            );
        }
        finally
        {
            _loading =
                false;
        }
    }

    private DataGridView CreateGrid()
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
                    "SteamId",

                HeaderText =
                    "SteamID",

                AutoSizeMode =
                    DataGridViewAutoSizeColumnMode
                        .Fill,

                FillWeight =
                    38F,

                LinkColor =
                    BridgeUiTheme.Link,

                ActiveLinkColor =
                    Color.White,

                VisitedLinkColor =
                    BridgeUiTheme.Link,

                TrackVisitedState =
                    false
            }
        );

        grid.Columns.Add(
            new DataGridViewTextBoxColumn
            {
                Name =
                    "Reaction",

                HeaderText =
                    "Reaction",

                AutoSizeMode =
                    DataGridViewAutoSizeColumnMode
                        .Fill,

                FillWeight =
                    20F
            }
        );

        grid.Columns.Add(
            new DataGridViewTextBoxColumn
            {
                Name =
                    "Date",

                HeaderText =
                    "Date",

                AutoSizeMode =
                    DataGridViewAutoSizeColumnMode
                        .Fill,

                FillWeight =
                    27F
            }
        );

        var deleteColumn =
            new DataGridViewButtonColumn
            {
                Name =
                    "Delete",

                HeaderText =
                    String.Empty,

                Text =
                    "Delete",

                UseColumnTextForButtonValue =
                    true,

                AutoSizeMode =
                    DataGridViewAutoSizeColumnMode
                        .Fill,

                FillWeight =
                    15F,

                FlatStyle =
                    FlatStyle.Flat
            };

        deleteColumn.DefaultCellStyle.BackColor =
            BridgeUiTheme.SurfaceRaised;

        deleteColumn.DefaultCellStyle.ForeColor =
            BridgeUiTheme.Text;

        deleteColumn.DefaultCellStyle
            .SelectionBackColor =
                BridgeUiTheme.SurfaceHover;

        deleteColumn.DefaultCellStyle
            .SelectionForeColor =
                Color.White;

        grid.Columns.Add(
            deleteColumn
        );

        return grid;
    }

    private async void GridCellContentClick(
        object? sender,
        DataGridViewCellEventArgs e
    )
    {
        if (
            e.RowIndex < 0 ||
            e.ColumnIndex < 0 ||
            _loading
        )
        {
            return;
        }

        var column =
            _grid.Columns[
                e.ColumnIndex
            ];

        var row =
            _grid.Rows[
                e.RowIndex
            ];

        if (
            row.Tag is not
                PlayerReactionListItem item
        )
        {
            return;
        }

        var steamId64 =
            Convert.ToString(
                row.Cells[
                    "SteamId"
                ].Value
            ) ??
            String.Empty;

        if (
            column.Name ==
                "SteamId"
        )
        {
            try
            {
                using var process =
                    Process.Start(
                        new ProcessStartInfo
                        {
                            FileName =
                                "https://steamcommunity.com/" +
                                "profiles/" +
                                steamId64,

                            UseShellExecute =
                                true
                        }
                    );
            }
            catch (Exception error)
            {
                ShowError(
                    "Failed to open the Steam profile.",
                    error
                );
            }

            return;
        }

        if (
            column.Name !=
                "Delete"
        )
        {
            return;
        }

        var reactionText =
            item.Reaction ==
                PlayerReactionValue.Like
                ? "Like"
                : "Dislike";

        var confirmation =
            MessageBox.Show(
                this,

                "Delete reaction " +
                $"{reactionText} for player" +
                Environment.NewLine +
                $"{steamId64}?",

                "Delete reaction",

                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2
            );

        if (
            confirmation !=
                DialogResult.Yes
        )
        {
            return;
        }

        _loading =
            true;

        _grid.Enabled =
            false;

        try
        {
            await _reactionStore.SetAsync(
                item.AccountId,
                PlayerReactionValue.None,
                CancellationToken.None
            );
        }
        catch (Exception error)
        {
            ShowError(
                "Failed to delete the reaction.",
                error
            );

            return;
        }
        finally
        {
            _grid.Enabled =
                true;

            _loading =
                false;
        }

        await RefreshAsync();
    }

    private void ShowError(
        string message,
        Exception error
    )
    {
        MessageBox.Show(
            this,
            message +
            Environment.NewLine +
            Environment.NewLine +
            error.Message,
            "Threat HUD Bridge",
            MessageBoxButtons.OK,
            MessageBoxIcon.Error
        );
    }
}
