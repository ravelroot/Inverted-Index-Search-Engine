using System.Text;
using Sonda.Storage;

namespace Sonda.Tests;

public class StorageTests
{
    [Fact]
    public void Crc32_confere_com_o_valor_de_referencia()
    {
        Assert.Equal(0xCBF43926u, Crc32.Compute(Encoding.ASCII.GetBytes("123456789")));
        var data = Enumerable.Range(0, 10_000).Select(i => (byte)(i * 31)).ToArray();
        Assert.Equal(Crc32.Compute(data), Crc32.Append(Crc32.Compute(data.AsSpan(0, 3333)), data.AsSpan(3333)));
    }

    [Fact]
    public void Inteiros_de_tamanho_variavel_e_textos_fazem_ida_e_volta()
    {
        using var temp = new TempDirectory();
        var directory = new IndexDirectory(temp.Path);
        int[] ints = [0, 1, 127, 128, 16_383, 16_384, int.MaxValue];
        long[] longs = [0, 1L << 35, long.MaxValue];
        long[] signed = [-1, 0, 1, long.MinValue, long.MaxValue, -123_456_789];
        using (var output = directory.CreateOutput("teste.bin"))
        {
            CodecUtil.WriteHeader(output, "Teste", 1);
            foreach (var value in ints)
            {
                output.WriteVInt(value);
            }

            foreach (var value in longs)
            {
                output.WriteVLong(value);
            }

            foreach (var value in signed)
            {
                output.WriteZLong(value);
            }

            output.WriteString("índice invertido ✓");
            CodecUtil.WriteFooter(output);
        }

        using var file = directory.OpenFile("teste.bin");
        CodecUtil.VerifyFile(file);
        var input = file.CreateInput(16);
        Assert.Equal(1, CodecUtil.CheckHeader(input, "Teste", 1, 1));
        Assert.Equal(ints, ints.Select(_ => input.ReadVInt()).ToArray());
        Assert.Equal(longs, longs.Select(_ => input.ReadVLong()).ToArray());
        Assert.Equal(signed, signed.Select(_ => input.ReadZLong()).ToArray());
        Assert.Equal("índice invertido ✓", input.ReadString());
    }

    [Fact]
    public void Checksum_detecta_um_unico_byte_alterado()
    {
        using var temp = new TempDirectory();
        var directory = new IndexDirectory(temp.Path);
        using (var output = directory.CreateOutput("dados.bin"))
        {
            CodecUtil.WriteHeader(output, "Teste", 1);
            output.WriteBytes(new byte[5000]);
            CodecUtil.WriteFooter(output);
        }

        var path = temp.Combine("dados.bin");
        var bytes = File.ReadAllBytes(path);
        bytes[2500] ^= 0x10;
        File.WriteAllBytes(path, bytes);
        using var file = directory.OpenFile("dados.bin");
        Assert.Throws<CorruptIndexException>(() => CodecUtil.VerifyFile(file));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(17)]
    [InlineData(31)]
    public void ForUtil_empacota_blocos_de_128_valores(int bits)
    {
        var random = new Random(bits);
        var values = Enumerable.Range(0, ForUtil.BlockSize)
            .Select(_ => bits == 0 ? 0 : (int)(random.NextInt64(0, 1L << bits)))
            .ToArray();
        using var temp = new TempDirectory();
        var directory = new IndexDirectory(temp.Path);
        using (var output = directory.CreateOutput("bloco.bin"))
        {
            ForUtil.Encode(values, output);
            Assert.Equal(1 + (CodecUtil.BitsRequired((uint)values.Aggregate(0, (a, b) => a | b)) * 16), output.FilePointer);
        }

        using var file = directory.OpenFile("bloco.bin");
        var decoded = new int[ForUtil.BlockSize];
        ForUtil.Decode(file.CreateInput(), decoded);
        Assert.Equal(values, decoded);
    }

    [Fact]
    public void Normas_sao_exatas_ate_23_e_monotonicas_depois()
    {
        for (var i = 0; i < 24; i++)
        {
            Assert.Equal(i, NormEncoding.Decode(NormEncoding.Encode(i)));
        }

        var previous = -1;
        for (var length = 0; length < 1_000_000; length += 97)
        {
            var decoded = NormEncoding.Decode(NormEncoding.Encode(length));
            Assert.True(decoded >= previous);
            Assert.True(decoded <= length);
            previous = decoded;
        }
    }

    [Fact]
    public void Mapa_de_bits_conta_e_clona()
    {
        var bits = new FixedBitSet(200);
        Assert.True(bits.Set(3));
        Assert.False(bits.Set(3));
        bits.Set(199);
        bits.Set(64);
        var clone = bits.Clone();
        bits.Clear(64);
        Assert.Equal(2, bits.Cardinality());
        Assert.Equal(3, clone.Cardinality());
        Assert.True(clone.Get(199));
    }

    [Fact]
    public void Diretorio_bloqueia_um_segundo_escritor()
    {
        using var temp = new TempDirectory();
        var directory = new IndexDirectory(temp.Path);
        using var first = directory.ObtainWriteLock();
        Assert.Throws<IndexLockedException>(() => new IndexDirectory(temp.Path).ObtainWriteLock());
    }
}
