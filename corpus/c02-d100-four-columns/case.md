# c02-d100-four-columns

A d100 table printed in FOUR page columns of five rows each (01-05 ... 25 in column 1, 26-50 column 2, and so on),
ranges written with an en dash and 96-00 for the last row. The PDF content stream is row-major (every row of the page
is drawn left to right before the next row), as a table object exports. Column groups are 138 pt apart; within a
group the result starts 40 pt after the range.
Exercises: multi-column continuation tables (one result set whose later columns continue the first), en dash spans,
"00" meaning 100, and whatever spacing each reader puts between the four groups on one printed line.
