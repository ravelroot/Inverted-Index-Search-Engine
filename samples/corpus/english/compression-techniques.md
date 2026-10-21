# Compression techniques for search indexes

Search indexes store billions of integers, so compression is essential for both disk space and speed.

**Delta encoding** stores the gap between consecutive document identifiers instead of the identifiers themselves. Gaps are small numbers when the list is dense.

**Variable byte encoding** (VInt) writes seven bits per byte and uses the high bit as a continuation flag, so small numbers take a single byte.

**Frame of reference** packs a block of 128 integers using the number of bits required by the largest value in the block. Decoding is a tight loop with no branches, which modern processors execute very quickly.

Stored fields, such as the original text used for snippets, compress well with general purpose algorithms like Brotli or LZ4 when documents are grouped into chunks of several kilobytes.
