# Touchline synthetic season

> **Synthetic data.** Every match is simulated from a seed by the touchline-ai match engine. Every club, player, referee and ground is fictional, and no real match, club or player data is used.

This folder is a 38-match season between two fictional clubs, North Bank FC and Thames Valley United. The clubs play each other once in every round, home and away alternating. Round N is seed N, so each match can be rebuilt from its seed alone (ADR-063).

## Files

| File | What it is |
|---|---|
| `events.jsonl.gz` | Every event, one JSON object per line, gzipped. The JSON is the same as the events sent to Event Hubs (`DomainJson`). |
| `matches.jsonl` | One line per match: the metadata, both line-ups, the final score and each side's totals. `synthetic` is always `true`. |
| `schema.json` | A JSON Schema (draft 2020-12) for both line types, under `$defs/event` and `$defs/match`. |
| `CALIBRATION.md` | The season's averages against the ranges the simulator is tuned to (`CalibrationTests`). |
| `README.md` | This file. |

## Regenerate

From any folder, with the .NET 10 SDK installed and no Azure resources needed. A relative folder is taken from the repository root:

```
dotnet run --project src/Touchline.Simulator -- export --out dataset
```

The export is deterministic. Running it again writes the same bytes to the same files, on the same machine and .NET runtime. The gzip header's modification time is zero and its OS byte is fixed, and the data has no wall-clock times: kick-offs are a week apart from a fixed first date. Byte-identical output across different machines also depends on the runtime's zlib build, which the tests don't cover.

## Checks

The unit tests in `tests/Touchline.Tests.Unit/Simulator/DatasetExportTests.cs` export two matches twice and compare the bytes, check that every exported line matches `schema.json`, and check that the season's averages land in the calibration ranges, allowing for sampling error.

A 38-match season is a smaller sample than the 200 matches the ranges were agreed on, so `CALIBRATION.md` reads each average two ways: inside the accepted bounds, or within three standard errors of them. The tests use the same reading, and the strict 200-match check stays in `CalibrationTests`.

## Known limits

- The export has no season store, so no player is suspended from one match to the next. Every match starts with its full squads.
- The line-ups are the starting elevens the engine picks by default. No bench or substitutions are recorded.
- The schema check in the tests is a small subset of JSON Schema (types, required properties, enums, `$ref`, `items` and `additionalProperties`), not a full validator.
