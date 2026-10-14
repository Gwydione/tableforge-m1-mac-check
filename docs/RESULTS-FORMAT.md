# Results format — TableForge Mac Check

The tester returns **one zip**, by default `TableForge-Mac-Check-Results-<yyyy-mm-dd>.zip`. It is written only after they
tick "I agree to save these results" and choose a location in the standard Save dialog.

| Entry | Content |
|---|---|
| `results.json` | The full machine-readable record (below) |
| `results.md` | The same, readable: overall, per-section and per-check outcomes, baseline comparison, tester answers, events |
| `e2/<case>.txt` | The exact text pasted for each supplied test PDF. Only text that passed the clipboard guard (it contains that table's title) is ever stored. |
| `e4/dice-600ms.png`, `dice-1100ms.png`, `dice-settled.png` | Frames of the dice canvas taken inside the page (no screen capture) |
| `log.txt` | The session log, redacted (home folder, user name, computer name, `/Users/<name>` paths, the dddice guest token), at most 3,000 lines |
| `README.txt` | One sentence explaining the contents |

The Safari backup page instead produces `TableForge-Mac-Check-Backup-<date>.json`, which is **reduced validation**:
`{format: "TableForgeMacCheckBackup", formatVersion: 1, reduced: true, savedAt, userAgent (only if ticked), cases: {<case>: {text, chars, pastedAt, types}}}`.
Analyse both with `tools/MacCheck.Analyze`.

## `results.json` (format `TableForgeMacCheck`, version 1)

| Field | Meaning |
|---|---|
| `build` | `app`, `version`, `buildId` (date + source hash), `tableForgeSource` (TableForge v1.0.0, commit 6415232), `runtime` (`osx-arm64` / `osx-x64`) |
| `startedUtc`, `finishedUtc` | Session times (UTC) |
| `consent` | `testsAgreed`, `systemInfoAgreed`, `modelAgreed`, `saveAgreed` |
| `system` | Only if consented. `os`, `osVersion` (`kern.osproductversion`), `osBuild`, `processArchitecture`, `osArchitecture`, `runningUnderRosetta`, `.net`, and `modelIdentifier` only with its own consent. Never names, serials, UUIDs, IP addresses or paths. |
| `overall` | `PASS` / `FAIL` / `UNABLE_TO_TEST` / `INCONCLUSIVE`: the worst of E2 (mandatory PDFs), E4 and E3. E1 is optional and not counted. |
| `e2[]` | Per PDF. Fields: `case`, `mandatory`, `outcome`, `rawVerdict` (E2's PASS / TEXT-DIFF / FAIL-flagged / FAIL-silent on the text **exactly as pasted**), `cleanupVerdicts` (review-cleanup, paste-cleanup; reported separately, never used for the outcome), `expectedRows`, `parsedRows`, `rowsMatched`, `mismatches[]`, `parserIssues[]`, `characters` (line endings, tabs, non-ASCII inventory), `pasteMethod`, `contentRejected`, `seconds`, `captureFile`, `sameVerdictAsE2Readers[]`, `rows` (coverage and order of the raw parse: `expected`, `found`, `missing`, `extra`, `outOfOrder` — rows off the longest run in the expected order — `textDifferences`, `warned`, `coverage`, `order`, and `silent` when rows were reordered, lost or added **without** a parser warning) |
| `e2Summary`, `e4`, `e3`, `e1` | Sections. Fields: `outcome`, `note`, `checks[]`. Each check has `id`, `title`, `outcome`, `automaticPass`, `testerAnswer` (`yes`/`no`/`not_sure`/`not_asked`), `detail`, `seconds`. |
| `questions` | Free-standing tester answers, for example whether macOS asked to allow pasting |
| `events` | Consent, choices, resumption after an unexpected exit |

### Outcome rules

| Outcome | Rule |
|---|---|
| **PASS** | Every automatic check passed and every tester confirmation was "Yes" |
| **FAIL** | An automatic check failed, or the tester answered "No" |
| **UNABLE_TO_TEST** | Skipped or blocked (no evidence either way). Also every check that only a person can confirm (for example "the dice tumbled") when nobody answered — an automated self-test — or when there was nothing to see (no dice were shown) |
| **INCONCLUSIVE** | Ran, but incomplete or contradictory: tester "Not sure", text that wasn't the test table, TEXT-DIFF, a dice shutdown with no helper processes to observe (macOS) |

A section takes the worst of its items. An E2 `FAIL` that matches the E2 browser-engine baselines (for example c02) is a
known parser limitation, not a macOS problem. See `EXPECTED-RESULTS.md`.

Sample output (a Windows self-test, **not** a Mac run): `samples/final/`.
