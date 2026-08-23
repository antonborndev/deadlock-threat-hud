internal interface IBridgeRuntimeView
{
    void SetRuntimeState(
        BridgeRuntimeState state,
        string? detail = null
    );

    void SetDeadlockRunning(
        bool running
    );

    void SetSteamInitialized(
        bool initialized
    );

    void SetHttpServerRunning(
        bool running,
        string address =
            "http://127.0.0.1:28741"
    );

    void SetAccountId(
        uint? accountId
    );

    void AppendLog(
        string message
    );
}