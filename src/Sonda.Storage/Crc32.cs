namespace Sonda.Storage;

public static class Crc32
{
    private const uint Polynomial = 0xEDB88320u;
    private static readonly uint[] Table = BuildTable();

    private static uint[] BuildTable()
    {
        var table = new uint[8 * 256];
        for (uint i = 0; i < 256; i++)
        {
            var crc = i;
            for (var j = 0; j < 8; j++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ Polynomial : crc >> 1;
            }

            table[i] = crc;
        }

        for (var i = 0; i < 256; i++)
        {
            for (var slice = 1; slice < 8; slice++)
            {
                var previous = table[((slice - 1) * 256) + i];
                table[(slice * 256) + i] = (previous >> 8) ^ table[previous & 0xFF];
            }
        }

        return table;
    }

    public static uint Append(uint crc, ReadOnlySpan<byte> data)
    {
        crc = ~crc;
        var table = Table;
        var i = 0;
        while (data.Length - i >= 8)
        {
            var one = (uint)(data[i] | (data[i + 1] << 8) | (data[i + 2] << 16) | (data[i + 3] << 24)) ^ crc;
            var two = (uint)(data[i + 4] | (data[i + 5] << 8) | (data[i + 6] << 16) | (data[i + 7] << 24));
            crc = table[(7 * 256) + (one & 0xFF)]
                ^ table[(6 * 256) + ((one >> 8) & 0xFF)]
                ^ table[(5 * 256) + ((one >> 16) & 0xFF)]
                ^ table[(4 * 256) + (one >> 24)]
                ^ table[(3 * 256) + (two & 0xFF)]
                ^ table[(2 * 256) + ((two >> 8) & 0xFF)]
                ^ table[256 + ((two >> 16) & 0xFF)]
                ^ table[two >> 24];
            i += 8;
        }

        for (; i < data.Length; i++)
        {
            crc = (crc >> 8) ^ table[(crc ^ data[i]) & 0xFF];
        }

        return ~crc;
    }

    public static uint Compute(ReadOnlySpan<byte> data) => Append(0, data);
}
