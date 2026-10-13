# c06-special-characters

A d8 table full of characters PDFs commonly mangle. The page draws the ligature GLYPHS fi, fl, ff, ffi, ffl, and the
PDF's text layer maps each to its single Unicode ligature code point (U+FB01 etc.), as many real PDFs do. Also:
curly quotes and apostrophes (including in the heading), em dash, ellipsis, multiplication sign, one-half, Æ, ë, é, ö,
a real minus sign (U+2212) and non-breaking spaces (row 8, between number and unit).
Expected: what the page shows, with ligatures as ordinary letters (fi, fl, ff, ffi, ffl), non-breaking spaces as plain
spaces, and every other character exactly as printed (curly quotes stay curly, the minus stays U+2212).
