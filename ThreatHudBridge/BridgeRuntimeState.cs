internal enum BridgeRuntimeState
{
    WaitingForDeadlock,
    StartingSteam,
    StartingHttpServer,
    Running,
    Stopping,
    Stopped,
    Error
}