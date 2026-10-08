# Pure model tests

These checks compile only the record model, without Minecraft or ULTRAKILL. From the repository root with JDK 21 on PATH:

```powershell
javac -encoding UTF-8 -d fabric/build/model-tests fabric/src/main/java/dev/ultracraft/BossRecords.java tests/BossRecordsTest.java
java -cp fabric/build/model-tests dev.ultracraft.BossRecordsTest
```

The checks cover stable modifier buckets, world-tick duration and timer formatting, faster-only personal bests, independent difficulties,
immutable snapshots, old-save defaults, long P values, save-map round trips and malformed record entries.

Record timings use the starting world's game time at one tick (0.05 second) resolution. Pauses and frozen world ticks
do not advance that clock; the boss controller's existing ten-tick scheduling is unchanged.
