# Privacy and data flow

Threat HUD has no project-owned account system and no project-owned telemetry
endpoint. It does make the network requests required for its features.

## Network access

Bridge can communicate with:

- `api.deadlock-api.com` for public player statistics, ranks, hero data, lane
  matchup statistics, and the current match's delayed broadcast URL;
- Steam through Steamworks for local Steam context and player identity data;
- Valve relay hosts returned for the delayed broadcast stream.

Requests to Deadlock API can include Steam account IDs, hero IDs, and a match
ID. The broadcast parser receives the broadcast URL and processes player damage
events from that delayed stream.

The Panorama mod communicates with Bridge through an unauthenticated local HTTP
service bound to `127.0.0.1:28741`. It is not bound to a LAN or public network
interface. Other software running as the same user can still access a loopback
service, so the API should not be treated as a secure storage boundary.

## Local files

Bridge creates runtime data under the current user's local application-data
directory, normally `%LOCALAPPDATA%\DeadlockThreatHud`:

- `player_reactions.db` stores user-created like/dislike notes;
- `ThreatHudBridge.worker.log` stores diagnostic runtime messages;
- `steam_appid.txt` supplies the Deadlock application ID to Steamworks;
- `Runtime\BroadcastParser\` contains the hash-verified parser extracted from
  the single-file Bridge executable.

The worker log can contain account IDs, match IDs, local paths, API/relay
status, and a temporary broadcast URL. Do not publish it without reviewing and
redacting it first.

Uninstalling the VPK does not automatically delete this local database or log.
They can be removed manually while Bridge is closed if the user no longer wants
to retain them.
