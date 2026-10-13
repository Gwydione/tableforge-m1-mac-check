# c05-d66-two-columns

A d66 table (36 single-value rows, 11-16 ... 61-66) printed in two page columns of 18 rows: 11-36 on the left,
41-66 on the right. Unlike c02, the content stream is column-major: the whole left column is one text object, then
the whole right column (as two linked text frames export). Sans-serif font.
Exercises: d66 numbering, single-value ranges, and whether a reader copies in stream order (one column after the
other) or in visual line order (left and right rows interleaved on one line, which the parser then has to split).
