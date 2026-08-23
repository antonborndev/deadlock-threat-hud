/*
 * Compile-time aliases preserve the existing wiring
 * ThreatHudBridgeRuntime without a second store/service implementation.
 *
 * After a separate cleanup commit, Runtime can be
 * renamed and this file can be removed without changing behavior.
 */
global using PlayerHeroReactionStore =
    PlayerReactionStore;

global using PlayerHeroReactionWriteService =
    PlayerReactionWriteService;

global using PlayerHeroReactionWriteRequest =
    PlayerReactionWriteRequest;

global using PlayerHeroReactionWriteQueryParser =
    PlayerReactionWriteQueryParser;