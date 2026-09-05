Threat HUD Bridge — player-stat header icons
============================================

Replace these files in ThreatHudBridge with the supplied versions:
- MainForm.cs
- MatchHistoryDetailsForm.cs

No loose image files are required: all seven new 16x16 header icons are
embedded in MainForm.cs. The shared grid factory applies them to both the
current-match table and match-history detail windows. Header artwork is fitted
without changing its aspect ratio. The header is now a flat Bridge-themed bar
with a subtle bottom separator instead of standard DataGridView 3D borders.
The reaction heart in MatchHistoryDetailsForm.cs uses the same header style.

Copy THIRD-PARTY-NOTICES.txt into the public source/release notices so the
Bootstrap Icons MIT notice and the required Game-icons.net attribution ship
with the project.

Header mapping:
- HERO  -> Bootstrap Icons person-fill
- RANK  -> Bootstrap Icons award-fill
- WR    -> Bootstrap Icons trophy-fill
- DMG   -> Game-icons.net Screen impact by Lorc
- SPM   -> Bootstrap Icons cash-stack
- HS%   -> original head-in-crosshair icon, optimized for 16x16
- ACC%  -> Bootstrap Icons bullseye
- Reaction (history) -> existing Bootstrap Icons heart-fill
- LANE remains text
