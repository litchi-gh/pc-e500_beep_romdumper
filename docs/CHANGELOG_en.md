# Changelog

[日本語](CHANGELOG.md)

## 0.3.0 (2026-11-06)

### Added

- **CSAVE M recordings can now be read.** The single line `CSAVE M "A",&HC0000,&HFFFFF` plays the ROM, so no program has to be typed in. The 256KB main ROM takes about 22 minutes.
  - There is no check per block, so blocks read from one recording are yellow. They turn green when two recordings agree.
  - Checked on 8 recordings from a real PC-E500, PC-E550, PC-1480U, PC-E650, and PC-U6000, with 0 bytes of wrong data. Places with noise count as not read and turn red.
- CSAVE M was added to the method selector in the application. It shows the command to type, the steps, what the colors mean, and the commands for missing ranges. Reading a CSAVE M recording switches to it automatically.
- `beepdump listing --csave` prints the commands. `beepdump decode` also reads CSAVE M recordings, and lists missing ranges as commands.

### Changed

- The controls at the top of the application window were rearranged: the model and buttons on the first row, the method and language on the second.

### Fixed

- Part of the window was misdrawn at 150% display scaling. The heights of the text boxes and the grid labels now come from the font height and the scale.
- Hard machine-code recordings were slow to analyze (24 minutes down to about 3 on a real PC-U6000 recording). The level-smoothing pattern took the room noise before and after the sound for the sound itself, learned from it, and read every block again. From the second pattern on, only the range of sound read first is used.
- On the machine-code version, noise at the end of a recording could be given an address, showing a block that does not exist (such as `00000` or `40000`) as not read. While such a block remained, the analysis went on even with everything read.

## 0.2.0 (2026-11-05)

### Changed

- The programs to type in have changed (the machine-code version now has 21 lines). The numbers at the end of the DATA lines changed too, so do not mix them with an earlier listing.
  - The machine code and the sound format are the same. Recordings made with an earlier program can still be read.
- A block has to meet stricter conditions to turn green, so that wrong data does not. Blocks that were green may now be yellow or red.
  - On the BASIC version, a block whose address cannot be confirmed counts as not read. If some blocks disagree, the larger group must be more than twice the other.
  - On the BASIC version, a block read only by a non-standard pattern, and every block of a recording below a 32 kHz sample rate, stays yellow even if it passes the check.
  - On the machine-code version, a block stays yellow if it is the only one read.
- Disagreements are handled differently.
  - A block that differs between patterns counts as not read, rather than being decided by majority.
  - A block that differs between recordings is held back and shown red, whether it was green or yellow. New content is used once it has appeared at least twice and more often than any other.
- A recording is counted only once. Files are compared by content, so a renamed copy is recognized. A retake with the same file name is read as a new recording.
- The range shown for a retake starts 2 blocks before what is missing and covers at least 8 blocks, because the BASIC version may not read a short recording.
- `beepdump decode` no longer overwrites an existing ROM file unless you add `--force`. If a recording could not be read, it is not overwritten even with `--force`.

### Added

- Running the program without reserving memory for the machine code now stops with `POKE LINE FIRST`. Run the `POKE` line and carry on; the program you typed is kept.
- On the machine-code version, reversed addresses or a missing DATA line now stop with `NG`.
- Once everything is read, the ranges to record to confirm the yellow blocks are shown.
- A note is shown when blocks outside the selected model's ROM are read.
- Added `--force` to `beepdump decode`.

### Fixed

- A start or end address in the middle of a block could read the wrong range.
- On the BASIC version, a block was not read when the end address was its first byte.
- `.BAS` files ended with a `0x1A`.
- Short retakes, and BASIC recordings with noise in them, could be taken for the wrong version.
- On the machine-code version, a failure in the first learning step could prevent reading what followed.
- On the BASIC version, a misread check tone could turn wrong data green.
- An error during analysis lost the blocks already read. If nothing has been read, the remaining patterns are now tried.
- A failed save no longer damages the earlier ROM file.
- `beepdump decode` reports an output it cannot write to before the analysis, not after it.
- In the application, a recording that could not be read can be tried again, and an error while reading no longer leaves the buttons disabled.
- Fixed the reporting of ffmpeg failures and the reading of large WAV files.

## 0.1.0 (2026-11-05)

The first release.

- Two programs send the ROM through the buzzer: the machine-code version (256KB in about 15 minutes) and the BASIC version, which uses no machine code.
- Two applications rebuild the ROM from a recording: `BeepDump` with a window (Windows) and the command-line `beepdump`.
- Reading PC-E500 and PC-U6000 ROMs was verified on recordings of real machines.
