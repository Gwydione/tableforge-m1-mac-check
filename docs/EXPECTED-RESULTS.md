# Expected results — TableForge Mac Check (E2 PDF cases)

Generated from `corpus/*/expected.json` and E2's recorded results (`tests/MacCheck.Tests/e2-fixtures/E2-results.json`).
"Expected" is the table each PDF *should* parse to. The baselines show what TableForge 1.0.0's parser actually made of the
same PDF copied from other readers in E2 — the reference for reading a Mac result. A Mac FAIL on c02 matches Chrome,
Chromium and Firefox; it is a known parser limitation (more than two gapless ranges per line), not a Mac problem.

Verdicts: PASS = exact table; TEXT-DIFF = structure right, some text differs (session outcome INCONCLUSIVE);
FAIL-flagged = structure wrong, parser warned; FAIL-silent = structure wrong, no warning. Only the **raw** parse decides the
outcome; TableForge's cleanup tools are reported separately.

| Case | Required | Table | Dice | Rows | Win Chrome | Win Edge | Linux Chromium | Linux Firefox | Linux Evince |
|---|---|---|---|---|---|---|---|---|---|
| c01-d20-simple | yes | Roadside Encounters | d20 | 20 | PASS | PASS | PASS | PASS | FAIL-flagged |
| c02-d100-four-columns | yes | Village Names | d100 | 20 | FAIL-flagged | PASS | FAIL-flagged | FAIL-flagged | FAIL-flagged |
| c05-d66-two-columns | yes | Shipwreck Finds | d66 | 36 | PASS | PASS | PASS | PASS | FAIL-flagged |
| c07-open-ended-ranges | optional | Morale Outcome | d20 | 7 | PASS | FAIL-flagged | PASS | PASS | FAIL-flagged |
| c08-heading-two-result-columns | optional | Difficulty Modifier | d8 | 12 (2 sets) | PASS | FAIL-flagged | PASS | PASS | FAIL-flagged |
| c03-wrapped-text | optional | Strange Omens | d12 | 12 | PASS | PASS | PASS | PASS | FAIL-flagged |
| c04-hyphenation | optional | Curious Trinkets | d10 | 10 | TEXT-DIFF | TEXT-DIFF | TEXT-DIFF | TEXT-DIFF | FAIL-flagged |
| c06-special-characters | optional | Alchemist’s Shelf | d8 | 8 | PASS | PASS | PASS | PASS | FAIL-flagged |

## Expected tables

### c01-d20-simple — Roadside Encounters (d20)

| Range | Result |
|---|---|
| 1 | A pedlar with a cart of cracked pots. |
| 2 | Three pilgrims singing off-key. |
| 3 | A riderless horse, still saddled. |
| 4 | Bandits demand a toll of 10 gp. |
| 5 | A shrine with a fresh offering. |
| 6 | Wolves watching from the treeline. |
| 7 | A broken wagon blocking the road. |
| 8 | A herald announcing a tourney. |
| 9 | Two knights arguing over a bridge. |
| 10 | A lost child who knows a secret path. |
| 11 | Crows feeding on something large. |
| 12 | A patrol of the duke's wardens. |
| 13 | An old woman selling charms. |
| 14 | A milestone carved with a warning. |
| 15 | Smoke rising from a burnt farm. |
| 16 | A troupe of travelling players. |
| 17 | A tax collector and his guards. |
| 18 | A hermit offering to read fortunes. |
| 19 | A sudden, freezing fog bank. |
| 20 | A dragon's shadow passes overhead. |

### c02-d100-four-columns — Village Names (d100)

| Range | Result |
|---|---|
| 01-05 | Ashby Ford |
| 06-10 | Brackenmoor |
| 11-15 | Cold Harbour |
| 16-20 | Dunwick |
| 21-25 | Elder Cross |
| 26-30 | Fallowmere |
| 31-35 | Greystone |
| 36-40 | Hollins End |
| 41-45 | Ivyreach |
| 46-50 | Juniper Hill |
| 51-55 | Kestrel Bay |
| 56-60 | Long Sutton |
| 61-65 | Marrowdale |
| 66-70 | Nettlebed |
| 71-75 | Oakhanger |
| 76-80 | Pike's Rest |
| 81-85 | Quarry Mill |
| 86-90 | Ravensholt |
| 91-95 | Saltmarsh |
| 96-00 | Thornbury |

### c05-d66-two-columns — Shipwreck Finds (d66)

| Range | Result |
|---|---|
| 11 | Barrel of salt pork |
| 12 | Ship's bell, cracked |
| 13 | Bolt of red silk |
| 14 | Sea chest, locked |
| 15 | Drowned parrot |
| 16 | Coil of rope |
| 21 | Lantern, still lit |
| 22 | Brass sextant |
| 23 | Crate of oranges |
| 24 | Captain's log |
| 25 | Iron cannonball |
| 26 | Box of candles |
| 31 | Carved figurehead |
| 32 | Bundle of letters |
| 33 | Spare sail |
| 34 | Keg of rum |
| 35 | Silver spoon |
| 36 | Rusted cutlass |
| 41 | Jar of pickled eels |
| 42 | Wooden leg |
| 43 | Bag of nails |
| 44 | Sealed bottle |
| 45 | Torn flag |
| 46 | Tin of biscuits |
| 51 | Fishing net |
| 52 | Small anchor |
| 53 | Oilskin coat |
| 54 | Chart of the reef |
| 55 | Bosun's whistle |
| 56 | Hammock |
| 61 | Lucky coin |
| 62 | Wax-sealed scroll |
| 63 | Ship's cat, alive |
| 64 | Spyglass |
| 65 | Bucket of tar |
| 66 | Mermaid's comb |

### c07-open-ended-ranges — Morale Outcome (d20)

| Range | Result |
|---|---|
| -4 or less | Rout: the band flees and scatters. |
| -3-0 | Retreat in disorder, dropping loot. |
| 1-6 | Fall back to the nearest cover. |
| 7-12 | Hold position, shaken. |
| 13-19 | Stand firm. |
| 20-24 | Press the attack. |
| 25+ | Fight to the last. |

### c08-heading-two-result-columns — Difficulty Modifier (d8)

*Set: Difficulty*

| Range | Result |
|---|---|
| 1 | Child’s play |
| 2-3 | Easy |
| 4-5 | Normal |
| 6 | Hard |
| 7 | Very hard |
| 8 | Heroic |

*Set: Modifier*

| Range | Result |
|---|---|
| 1 | +30 |
| 2-3 | +20 |
| 4-5 | +0 |
| 6 | −10 |
| 7 | −20 |
| 8 | −30 |

### c03-wrapped-text — Strange Omens (d12)

| Range | Result |
|---|---|
| 1 | Every candle in the village gutters out at once, though there is no wind. |
| 2 | A white stag crosses the road and stops to stare at the party before vanishing. |
| 3 | The church bell rings by itself at midnight. |
| 4 | Birds fall silent for an hour; when they sing again, it is a song nobody recognises, and the oldest villager weeps to hear it. |
| 5 | Milk sours in every pail overnight. |
| 6 | A stranger arrives asking for a person who died here long ago, and describes them perfectly. |
| 7 | Frost forms in the shape of a hand on the inn's door. |
| 8 | The river runs backwards until dawn. |
| 9 | Every dog in town turns to face the north and will not be called away. |
| 10 | A child draws a map of a place she has never seen, and it is accurate. |
| 11 | Moths gather on the party's packs and cannot be shaken off. |
| 12 | The moon rises red, and the well water tastes of iron. |

### c04-hyphenation — Curious Trinkets (d10)

| Range | Result |
|---|---|
| 1 | A heavy and rather magnificent brass key that fits no lock. |
| 2 | A sharpened, polished half-orc's tooth on a silver chain. |
| 3 | A rolled-up and quite extraordinary map, drawn in squid ink. |
| 4 | A music box that plays a lullaby backwards. |
| 5 | A glass eye, slightly warm. |
| 6 | A well-made dagger, blunt edge. |
| 7 | A letter of introduction to someone who does not exist. |
| 8 | A copper coin stamped on both sides with the same face. |
| 9 | A tiny wicker cage holding a sleeping cricket. |
| 10 | A deck of cards that is missing all the queens. |

### c06-special-characters — Alchemist’s Shelf (d8)

| Range | Result |
|---|---|
| 1 | A flask of fire-salt (½ dose). |
| 2 | Twelve “will-o’-wisp” lanterns. |
| 3 | An offering bowl × 3 — still warm. |
| 4 | An effigy of Zoë the Bold… |
| 5 | The Café Noir ledger: 4 sp, 2 cp. |
| 6 | Ætheling’s ring (−1 to saves). |
| 7 | A baffling map of the Fjörd. |
| 8 | A 10 ft. pole, notched at 5 ft. |

