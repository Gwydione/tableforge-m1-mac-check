# c07-open-ended-ranges

A modified-roll table (d20 plus situational modifiers): the ranges go below the die's minimum and above its maximum.
Written as typeset books write them: a real minus sign (U+2212), en dash spans (U+2013), "or less", and a trailing "+".
Left-aligned range column 70 pt wide, zebra shading, sans-serif.
Expected ranges are compared after TableForge's own range normalization (NormalizeRangeText: minus sign and dash
variants become "-"), so "−3–0" and "-3-0" are the same range; text is compared exactly.
