# How an inverted index works

An inverted index maps every distinct term to the list of documents that contain it. Instead of scanning every document for a word, the search engine looks the word up in a sorted dictionary and reads its postings list.

## Postings lists

Each posting stores a document identifier and the term frequency in that document. Positional indexes also store where each occurrence happens, which makes phrase queries such as "inverted index" possible: the engine checks that the positions of both terms are adjacent.

## Merging postings

A query with two required terms intersects two sorted lists. Because both lists are sorted by document identifier, the engine can advance them together and use skip pointers to jump over blocks that cannot contain a common document. Optional terms are combined with a union, and the WAND algorithm skips documents whose maximum possible score cannot beat the current top results.

## Segments

New documents are buffered in memory and written as immutable segments. Deletions are recorded in a separate bitmap, and a background merge rewrites small segments into larger ones, physically removing deleted documents.
