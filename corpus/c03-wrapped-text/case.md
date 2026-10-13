# c03-wrapped-text

A d12 table whose results wrap onto two or three lines with a hanging indent. Every word is drawn as its own
positioned string with no space character between words (how pdfTeX and some exporters write text), so the reader
must infer word spaces from the gaps, and must decide the line breaks itself.
Exercises: wrapped continuation lines, space inference, line order.
Expected: each row's lines joined with single spaces. (No wrapped line starts with a digit; that parser hazard is
discussed in the report, not tested here.)
