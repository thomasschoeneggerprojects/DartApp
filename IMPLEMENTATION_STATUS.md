# DartForge implementation status

This package is a **source-code MVP**, not the complete feature set from the master prompt. The app builds as a signed .NET 8 Android APK and has been installed and launch-tested on an Android emulator.

## Implemented (source)
- .NET 8 MAUI Android + Blazor Hybrid project and responsive dark UI
- Independent scoring engine for X01, Cricket variants, Around the Clock, Shanghai, Count Up, High Score, Halve It, Bob's 27 (some simplified; see below)
- Individual dart entry, automatic turn change, undo/redo, game setup, basic bot
- Local SQLite completed-match and training history
- Simple target-hit training, lightweight offline challenges and statistics
- No advertising SDK, no networking, optional visual footer placeholder
- xUnit starter tests

## Not complete / known limitations
- **Not all requested games**: Killer, Baseball, Golf, 21, Scram, Gotcha, etc. are not yet implemented.
- Cricket and alternative modes need extensive variant-specific and multiplayer tests; scoring rules are currently simplified.
- No robust local match autosave or process-death resume.
- No full-leg/sets match format configuration UI or local multiplayer beyond human vs bot.
- DartBot is rudimentary and does not model a physics-accurate dartboard or calibrate realistic averages.
- Training currently records hits/misses, without detailed darts, XP or individual training challenges.
- Offline daily goals are based on local training history, not a full challenge or achievement engine.
- No actual tournaments, caller voices, sound files, full localization, heatmaps, backup/import/export.
- No real ad integration; banner is a non-ad placeholder in the UI.
- Broader device coverage, lifecycle stress testing, and release signing remain to be completed.

## Next tasks
1. Add tests for all game types and Android lifecycle behavior.
2. Finish full X01 and Cricket behavior, persistence, and recovery first.
3. Implement remaining modes one-by-one with precise documented rules and exhaustive tests.
4. Add full training plans, challenges, achievements, tournament brackets and audio.
5. Add consent-compliant optional ads only after verifying compatible Android SDKs.
