using System.Drawing;
using System.Windows.Forms;

internal sealed class
    BridgeModuleSettingsChangedEventArgs :
        EventArgs
{
    public BridgeModuleSettingsChangedEventArgs(
        BridgeModuleSettings previousSettings,
        BridgeModuleSettings settings
    )
    {
        PreviousSettings =
            previousSettings;

        Settings =
            settings;
    }

    public BridgeModuleSettings PreviousSettings
    {
        get;
    }

    public BridgeModuleSettings Settings
    {
        get;
    }
}

internal sealed class BridgeServiceStatusControl :
    TableLayoutPanel
{
    private static readonly BridgeServiceKind[]
        Services =
            Enum.GetValues<BridgeServiceKind>();

    private readonly Dictionary<
        BridgeServiceKind,
        CheckBox
    > _moduleCheckBoxes =
        new();

    private readonly Dictionary<
        BridgeServiceKind,
        Label
    > _serviceStateLabels =
        new();

    private BridgeModuleSettings _settings;

    private BridgeServiceStatusSnapshot?
        _serviceStates;

    private bool _applyingSettings;

    public BridgeServiceStatusControl(
        BridgeModuleSettings settings
    )
    {
        Dock =
            DockStyle.Fill;

        AutoSize =
            true;

        ColumnCount =
            2;

        RowCount =
            5;

        BackColor =
            BridgeUiTheme.SurfaceRaised;

        Margin =
            new Padding(
                6,
                0,
                6,
                0
            );

        Padding =
            new Padding(
                0
            );

        ColumnStyles.Add(
            new ColumnStyle(
                SizeType.Absolute,
                150
            )
        );

        ColumnStyles.Add(
            new ColumnStyle(
                SizeType.Percent,
                100F
            )
        );

        RowStyles.Add(
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

        Controls.Add(
            title,
            0,
            0
        );

        SetColumnSpan(
            title,
            2
        );

        AddServiceRow(
            BridgeServiceKind.Winrate,
            1,
            "Winrate:"
        );

        AddServiceRow(
            BridgeServiceKind.Rank,
            2,
            "Rank:"
        );

        AddServiceRow(
            BridgeServiceKind.Adviser,
            3,
            "Adviser:"
        );

        AddServiceRow(
            BridgeServiceKind.HeroDamage,
            4,
            "Hero Damage:"
        );

        SetSettings(
            settings
        );
    }

    public event EventHandler<
        BridgeModuleSettingsChangedEventArgs
    >? SettingsChanged;

    public BridgeModuleSettings Settings =>
        _settings;

    public void SetSettings(
        BridgeModuleSettings settings
    )
    {
        _applyingSettings =
            true;

        try
        {
            foreach (
                var service in
                    Services
            )
            {
                _moduleCheckBoxes[
                    service
                ].Checked =
                    settings.IsEnabled(
                        service
                    );
            }

            _settings =
                settings;
        }
        finally
        {
            _applyingSettings =
                false;
        }

        ApplyAllServiceStates();
    }

    public void SetServiceStates(
        BridgeServiceStatusSnapshot? snapshot
    )
    {
        _serviceStates =
            snapshot;

        ApplyAllServiceStates();
    }

    private void AddServiceRow(
        BridgeServiceKind service,
        int row,
        string title
    )
    {
        RowStyles.Add(
            new RowStyle(
                SizeType.AutoSize
            )
        );

        var checkBox =
            new BridgeModuleCheckBox
            {
                AutoSize =
                    false,

                Width =
                    142,

                Height =
                    24,

                Text =
                    title,

                ForeColor =
                    BridgeUiTheme.TextMuted,

                BackColor =
                    BridgeUiTheme.SurfaceRaised,

                Margin =
                    new Padding(
                        0,
                        2,
                        8,
                        2
                    ),

                Tag =
                    service
            };

        var stateLabel =
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

        _moduleCheckBoxes[service] =
            checkBox;

        _serviceStateLabels[service] =
            stateLabel;

        checkBox.CheckedChanged +=
            OnModuleCheckedChanged;

        Controls.Add(
            checkBox,
            0,
            row
        );

        Controls.Add(
            stateLabel,
            1,
            row
        );
    }

    private void OnModuleCheckedChanged(
        object? sender,
        EventArgs e
    )
    {
        if (
            _applyingSettings ||
            sender is not CheckBox checkBox
        )
        {
            return;
        }

        if (
            checkBox.Tag is not
                BridgeServiceKind service
        )
        {
            return;
        }

        var previousSettings =
            _settings;

        var settings =
            previousSettings.WithEnabled(
                service,
                checkBox.Checked
            );

        if (
            settings ==
                previousSettings
        )
        {
            return;
        }

        _settings =
            settings;

        ApplyServiceState(
            service
        );

        SettingsChanged?.Invoke(
            this,
            new BridgeModuleSettingsChangedEventArgs(
                previousSettings,
                settings
            )
        );
    }

    private void ApplyAllServiceStates()
    {
        foreach (
            var service in
                Services
        )
        {
            ApplyServiceState(
                service
            );
        }
    }

    private void ApplyServiceState(
        BridgeServiceKind service
    )
    {
        var label =
            _serviceStateLabels[
                service
            ];

        string text;
        Color color;

        if (_serviceStates is null)
        {
            text =
                "—";

            color =
                BridgeUiTheme.TextMuted;
        }
        else if (
            !_settings.IsEnabled(
                service
            )
        )
        {
            text =
                "Disabled";

            color =
                BridgeUiTheme.TextMuted;
        }
        else
        {
            var state =
                _serviceStates.GetState(
                    service
                );

            text =
                state switch
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

            color =
                state switch
                {
                    BridgeServiceState.InProgress =>
                        BridgeUiTheme.ServiceInProgress,

                    BridgeServiceState.Completed =>
                        BridgeUiTheme.ServiceCompleted,

                    BridgeServiceState.Error =>
                        BridgeUiTheme.ServiceError,

                    _ =>
                        throw new ArgumentOutOfRangeException(
                            nameof(state),
                            state,
                            "Unknown Bridge service state."
                        )
                };
        }

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

    protected override void Dispose(
        bool disposing
    )
    {
        if (disposing)
        {
            foreach (
                var checkBox in
                    _moduleCheckBoxes.Values
            )
            {
                checkBox.CheckedChanged -=
                    OnModuleCheckedChanged;
            }
        }

        base.Dispose(
            disposing
        );
    }
}
