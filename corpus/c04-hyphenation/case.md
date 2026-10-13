# c04-hyphenation

A d10 table set in a narrow, justified column (word spacing stretched with the Tw operator, real space glyphs present).
Line-end hyphenation is drawn as an ordinary hyphen (U+002D) except row 3, whose break hyphen is a soft hyphen
(U+00AD) in the text layer, as some exporters write it.
Rows: 1, 4, 7, 9 break a single word at a hyphen; 2 breaks the genuine compound "half-orc" at its own hyphen;
3 also has "rolled-up" mid-line and 6 has "well-made" mid-line; 5, 8, 10 need no dehyphenation.
Expected: the text a reader of the printed page would type (hyphenation removed, genuine compounds kept).
