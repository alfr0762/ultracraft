# Terminal main menu, field manual and personal boss records

The title screen now gives Singleplayer a prominent terminal entry against an original infernal voxel illustration.
Minecraft still creates and handles its own Singleplayer, Multiplayer, Realms, Options and Quit controls. Their
callbacks, disabled states, tooltips and narration are preserved. Language and accessibility controls remain available.
Demo mode uses the vanilla layout. Unknown third-party controls are left alone.

`terminalMenu=false` restores the standard title screen. The same switch is available on Ultracraft Settings → Gameplay.
The background is one static 1672×941 texture, cropped to preserve its aspect ratio. This feature does not change
resolution, effects, gore, render distance, frame limits or any other performance preset.

The Field Manual opens from the main menu and has three pages: movement, weapon combinations and personal records.
It displays current Minecraft key bindings and links to Ultracraft's existing ULTRAKILL controls screen. Text wraps and
scrolls; narrow screens use two rows of buttons. Escape returns to the previous screen. New strings are provided in
English and Turkish.

After a successful boss fight, chat shows elapsed time, highest reported style, P received during the fight before
the boss reward, and a personal-best notice when appropriate. `/uc records [page]` lists only the caller's records
in the current world. Existing progress saves load without a records field.

Records are grouped by boss, difficulty and sorted modifier set. Only a strictly faster clear replaces a record.
Participants who arrive late or leave the fight do not receive a time record. These are personal training records:
cheats, upgrades, revives and co-op are not separate categories, and this does not calculate ULTRAKILL level ranks.
The clock uses elapsed world ticks (0.05 seconds), excludes paused/frozen world time, and includes bridge spawn latency.
The bridge batches style earnings, so a final payout received after victory is absent from the fight's P subtotal.

The contribution also restores Minecraft's normal perspective key by removing the line that forcibly disabled it.
The existing bridge supplies the first/third-person views.

## Verification

Pure Java checks live in `tests/BossRecordsTest.java` and `tests/MenuLayoutTest.java`; they do not require either game.
See `tests/README.md` and `tests/run-menu-tests.ps1`. The layout checks cover minimum GUI dimensions, typical and
ultrawide sizes, row separation and clearance for native footer controls. Runtime findings and build details are
included in the packaged contribution report.

## Artwork provenance

`fabric/src/main/resources/assets/ultracraft/textures/gui/terminal_background.png` is an original image generated
with OpenAI's built-in image generation tool on 2026-10-08. No ULTRAKILL or Minecraft game artwork was extracted for it.
It is supplied with this contribution under the repository's MIT terms; maintainers can replace it without changing
menu behavior.

Art direction: a cinematic wide composition of a dark voxel industrial cathedral; charcoal architecture, muted
crimson and ivory highlights, a thin red rectangular portal toward the right, and quiet negative space across the
left for the menu. No text, logos, controls or characters. The original generated PNG is used unchanged at 1672×941.
