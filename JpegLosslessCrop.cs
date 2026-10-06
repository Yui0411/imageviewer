using System.IO;

namespace ImageViewer;

/// <summary>
/// Crops a baseline JPEG without re-compressing it: the compressed 8×8 blocks inside the crop are copied unchanged
/// into a new file, and only the entropy (Huffman) coding is redone. The crop's left and top edges must lie on the
/// JPEG block grid (see <see cref="JpegHeader"/>); the right and bottom edges can be anywhere.
/// Progressive, arithmetic-coded, 12-bit and multi-scan JPEGs are refused.
/// </summary>
internal static class JpegLosslessCrop
{
    private sealed class Component
    {
        public int Id, H, V, QuantTable, DcTable, AcTable;
        public int BlocksWide, BlocksHigh;
        public short[] Coefficients = Array.Empty<short>(); // 64 per block, zigzag order, row-major blocks
    }

    private sealed class HuffmanTable
    {
        public readonly int[] Lookup = new int[1 << LookupBits]; // (length << 8) | value for short codes, 0 if longer
        public readonly int[] MaxCode = new int[18];
        public readonly int[] ValOffset = new int[18];
        public byte[] Values = Array.Empty<byte>();
    }

    private const int LookupBits = 9;

    public static byte[] Crop(byte[] data, int x, int y, int width, int height)
    {
        if (data.Length < 4 || data[0] != 0xFF || data[1] != 0xD8)
            throw new EditException("This file isn't a valid JPEG.");

        var keptSegments = new MemoryStream();
        var dcTables = new HuffmanTable?[4];
        var acTables = new HuffmanTable?[4];
        Component[]? components = null;
        int frameMarker = 0, imageWidth = 0, imageHeight = 0, restartInterval = 0;
        int pos = 2;

        // ---- headers, up to the start of scan
        while (true)
        {
            int marker = NextMarker(data, ref pos);
            if (marker == 0xDA) break;
            if (marker == 0xD9 || pos + 2 > data.Length) throw new EditException("This JPEG has no image data.");
            int length = (data[pos] << 8) | data[pos + 1];
            int segmentStart = pos + 2, segmentEnd = pos + length;
            if (length < 2 || segmentEnd > data.Length) throw new EditException("This JPEG is damaged.");

            if (marker is 0xC0 or 0xC1)
            {
                if (data[segmentStart] != 8) throw new EditException("Only 8-bit JPEGs can be cropped losslessly.");
                frameMarker = marker;
                imageHeight = (data[segmentStart + 1] << 8) | data[segmentStart + 2];
                imageWidth = (data[segmentStart + 3] << 8) | data[segmentStart + 4];
                int count = data[segmentStart + 5];
                components = new Component[count];
                for (int i = 0; i < count; i++)
                {
                    int p = segmentStart + 6 + i * 3;
                    components[i] = new Component { Id = data[p], H = data[p + 1] >> 4, V = data[p + 1] & 15, QuantTable = data[p + 2] };
                }
            }
            else if (marker is >= 0xC2 and <= 0xCF && marker != 0xC4 && marker != 0xC8 && marker != 0xCC)
            {
                throw new EditException(marker is 0xC2 or 0xC6 or 0xCA or 0xCE
                    ? "Progressive JPEGs can't be cropped losslessly. The file was not changed."
                    : "This kind of JPEG can't be cropped losslessly. The file was not changed.");
            }
            else if (marker == 0xC4)
            {
                ReadHuffmanTables(data, segmentStart, segmentEnd, dcTables, acTables);
            }
            else if (marker == 0xDD)
            {
                restartInterval = (data[segmentStart] << 8) | data[segmentStart + 1];
            }
            else
            {
                // APPn (EXIF, ICC profile…), DQT, COM and anything else: carried over unchanged.
                keptSegments.Write(data, pos - 2, length + 2);
            }
            pos = segmentEnd;
        }

        if (components == null) throw new EditException("This JPEG is damaged.");

        // ---- scan header
        int scanLength = (data[pos] << 8) | data[pos + 1];
        int scanCount = data[pos + 2];
        if (scanCount != components.Length)
            throw new EditException("This JPEG uses multiple scans and can't be cropped losslessly. The file was not changed.");
        var scanOrder = new Component[scanCount];
        for (int i = 0; i < scanCount; i++)
        {
            int p = pos + 3 + i * 2;
            var component = components.FirstOrDefault(c => c.Id == data[p]) ?? throw new EditException("This JPEG is damaged.");
            component.DcTable = data[p + 1] >> 4;
            component.AcTable = data[p + 1] & 15;
            scanOrder[i] = component;
        }
        int spectral = pos + 3 + scanCount * 2;
        if (data[spectral] != 0 || data[spectral + 1] != 63 || data[spectral + 2] != 0)
            throw new EditException("This kind of JPEG can't be cropped losslessly. The file was not changed.");
        pos += scanLength;

        // ---- decode every block's coefficients
        bool interleaved = components.Length > 1;
        int maxH = interleaved ? components.Max(c => c.H) : 1;
        int maxV = interleaved ? components.Max(c => c.V) : 1;
        int mcusWide = DivCeil(imageWidth, 8 * maxH), mcusHigh = DivCeil(imageHeight, 8 * maxV);
        foreach (var c in components)
        {
            c.BlocksWide = interleaved ? mcusWide * c.H : DivCeil(imageWidth, 8);
            c.BlocksHigh = interleaved ? mcusHigh * c.V : DivCeil(imageHeight, 8);
            c.Coefficients = new short[c.BlocksWide * c.BlocksHigh * 64];
            if (dcTables[c.DcTable] == null || acTables[c.AcTable] == null) throw new EditException("This JPEG is damaged.");
        }

        var reader = new BitReader(data, pos);
        var predictions = new int[components.Length];
        int totalMcus = interleaved ? mcusWide * mcusHigh : components[0].BlocksWide * components[0].BlocksHigh;
        for (int mcu = 0; mcu < totalMcus; mcu++)
        {
            if (restartInterval > 0 && mcu > 0 && mcu % restartInterval == 0)
            {
                reader.Restart();
                Array.Clear(predictions);
            }
            if (interleaved)
            {
                int mx = mcu % mcusWide, my = mcu / mcusWide;
                for (int s = 0; s < scanOrder.Length; s++)
                {
                    var c = scanOrder[s];
                    int index = Array.IndexOf(components, c);
                    for (int v = 0; v < c.V; v++)
                        for (int h = 0; h < c.H; h++)
                            DecodeBlock(reader, c, ((my * c.V + v) * c.BlocksWide + mx * c.H + h) * 64,
                                dcTables[c.DcTable]!, acTables[c.AcTable]!, ref predictions[index]);
                }
            }
            else
            {
                var c = components[0];
                DecodeBlock(reader, c, mcu * 64, dcTables[c.DcTable]!, acTables[c.AcTable]!, ref predictions[0]);
            }
        }
        if (reader.NextMarkerAfterScan() is int after && after != 0xD9)
            throw new EditException("This JPEG uses multiple scans and can't be cropped losslessly. The file was not changed.");

        // ---- crop: keep only the blocks inside the new image
        int blockW = 8 * maxH, blockH = 8 * maxV;
        if (x % blockW != 0 || y % blockH != 0 || width <= 0 || height <= 0 || x + width > imageWidth || y + height > imageHeight)
            throw new EditException("The crop doesn't line up with the JPEG block grid.");
        int newMcusWide = DivCeil(width, blockW), newMcusHigh = DivCeil(height, blockH);
        foreach (var c in components)
        {
            int newWide = interleaved ? newMcusWide * c.H : DivCeil(width, 8);
            int newHigh = interleaved ? newMcusHigh * c.V : DivCeil(height, 8);
            int fromX = x / blockW * (interleaved ? c.H : 1), fromY = y / blockH * (interleaved ? c.V : 1);
            var cropped = new short[newWide * newHigh * 64];
            for (int row = 0; row < newHigh; row++)
                Array.Copy(c.Coefficients, ((fromY + row) * c.BlocksWide + fromX) * 64, cropped, row * newWide * 64, newWide * 64);
            c.Coefficients = cropped;
            c.BlocksWide = newWide;
            c.BlocksHigh = newHigh;
        }

        // ---- re-encode with Huffman tables optimized for the cropped data
        var dcFrequencies = new long[4][];
        var acFrequencies = new long[4][];
        foreach (var c in components)
        {
            dcFrequencies[c.DcTable] ??= new long[257];
            acFrequencies[c.AcTable] ??= new long[257];
        }
        EncodeScan(components, scanOrder, interleaved, newMcusWide, newMcusHigh, null, dcFrequencies, acFrequencies, null, null);

        var dcCodes = new (int code, int length)[4][];
        var acCodes = new (int code, int length)[4][];
        var output = new MemoryStream();
        output.Write(new byte[] { 0xFF, 0xD8 });
        keptSegments.WriteTo(output);
        WriteFrameHeader(output, frameMarker, width, height, components);
        var dht = new MemoryStream();
        for (int t = 0; t < 4; t++)
        {
            if (dcFrequencies[t] != null) dcCodes[t] = BuildTable(dcFrequencies[t], 0, t, dht);
            if (acFrequencies[t] != null) acCodes[t] = BuildTable(acFrequencies[t], 1, t, dht);
        }
        WriteSegment(output, 0xC4, dht.ToArray());
        WriteScanHeader(output, scanOrder);
        var writer = new BitWriter(output);
        EncodeScan(components, scanOrder, interleaved, newMcusWide, newMcusHigh, writer, null, null, dcCodes, acCodes);
        writer.Flush();
        output.Write(new byte[] { 0xFF, 0xD9 });
        return output.ToArray();
    }

    // ---------------------------------------------------------------- decoding

    private static int NextMarker(byte[] data, ref int pos)
    {
        if (pos >= data.Length || data[pos] != 0xFF) throw new EditException("This JPEG is damaged.");
        while (pos < data.Length && data[pos] == 0xFF) pos++;
        if (pos >= data.Length) throw new EditException("This JPEG is damaged.");
        return data[pos++];
    }

    private static void ReadHuffmanTables(byte[] data, int pos, int end, HuffmanTable?[] dc, HuffmanTable?[] ac)
    {
        while (pos < end)
        {
            int tableClass = data[pos] >> 4, id = data[pos] & 15;
            if (id > 3 || tableClass > 1) throw new EditException("This JPEG is damaged.");
            var counts = new int[17];
            int total = 0;
            for (int i = 1; i <= 16; i++) total += counts[i] = data[pos + i];
            var table = new HuffmanTable { Values = data.AsSpan(pos + 17, total).ToArray() };

            // Canonical code assignment (JPEG spec, Annex C), plus a lookup table for short codes.
            int code = 0, k = 0;
            for (int length = 1; length <= 16; length++)
            {
                table.ValOffset[length] = k - code;
                for (int i = 0; i < counts[length]; i++, k++, code++)
                {
                    if (length <= LookupBits)
                    {
                        int first = code << (LookupBits - length);
                        for (int fill = 0; fill < 1 << (LookupBits - length); fill++)
                            table.Lookup[first + fill] = (length << 8) | table.Values[k];
                    }
                }
                table.MaxCode[length] = counts[length] > 0 ? code - 1 : -1;
                code <<= 1;
            }
            table.MaxCode[17] = int.MaxValue;
            (tableClass == 0 ? dc : ac)[id] = table;
            pos += 17 + total;
        }
    }

    private static void DecodeBlock(BitReader reader, Component c, int offset, HuffmanTable dc, HuffmanTable ac, ref int prediction)
    {
        var block = c.Coefficients;
        int size = reader.Decode(dc);
        prediction += size == 0 ? 0 : Extend(reader.Bits(size), size);
        block[offset] = (short)prediction;
        for (int k = 1; k < 64;)
        {
            int symbol = reader.Decode(ac);
            int run = symbol >> 4, bits = symbol & 15;
            if (bits == 0)
            {
                if (run != 15) break; // end of block
                k += 16;
                continue;
            }
            k += run;
            if (k > 63) throw new EditException("This JPEG is damaged.");
            block[offset + k++] = (short)Extend(reader.Bits(bits), bits);
        }
    }

    private static int Extend(int value, int bits) => value < 1 << (bits - 1) ? value - (1 << bits) + 1 : value;

    private sealed class BitReader(byte[] data, int pos)
    {
        private int _pos = pos;
        private int _buffer, _count;

        private void Fill()
        {
            while (_count <= 24)
            {
                int b = 0;
                if (_pos < data.Length)
                {
                    b = data[_pos];
                    if (b == 0xFF)
                    {
                        int next = _pos + 1 < data.Length ? data[_pos + 1] : 0xD9;
                        if (next == 0x00) _pos += 2; // stuffed byte
                        else b = 0;                   // hit a marker: feed zeros, don't consume it
                    }
                    else _pos++;
                }
                _buffer |= b << (24 - _count);
                _count += 8;
            }
        }

        public int Bits(int n)
        {
            Fill();
            int value = (int)((uint)_buffer >> (32 - n));
            _buffer <<= n;
            _count -= n;
            return value;
        }

        public int Decode(HuffmanTable table)
        {
            Fill();
            int entry = table.Lookup[(int)((uint)_buffer >> (32 - LookupBits))];
            if (entry != 0)
            {
                int length = entry >> 8;
                _buffer <<= length;
                _count -= length;
                return entry & 0xFF;
            }
            int code = 0;
            for (int length = 1; length <= 16; length++)
            {
                code = (code << 1) | Bits(1);
                if (code <= table.MaxCode[length]) return table.Values[code + table.ValOffset[length]];
            }
            throw new EditException("This JPEG is damaged.");
        }

        /// <summary>Skips to and past the next restart marker.</summary>
        public void Restart()
        {
            _buffer = _count = 0;
            while (_pos + 1 < data.Length && !(data[_pos] == 0xFF && data[_pos + 1] is >= 0xD0 and <= 0xD7)) _pos++;
            _pos += 2;
        }

        /// <summary>The marker that follows the entropy-coded data, or null if the file just ends.</summary>
        public int? NextMarkerAfterScan()
        {
            for (int p = _pos; p + 1 < data.Length; p++)
                if (data[p] == 0xFF && data[p + 1] != 0x00 && data[p + 1] != 0xFF && data[p + 1] is not (>= 0xD0 and <= 0xD7))
                    return data[p + 1];
            return null;
        }
    }

    // ---------------------------------------------------------------- encoding

    /// <summary>
    /// Walks every block in scan order. With frequency arrays it only counts symbols (first pass, to build optimal
    /// tables); with a writer it emits the entropy-coded data.
    /// </summary>
    private static void EncodeScan(Component[] components, Component[] scanOrder, bool interleaved, int mcusWide, int mcusHigh,
        BitWriter? writer, long[][]? dcFreq, long[][]? acFreq, (int code, int length)[][]? dcCodes, (int code, int length)[][]? acCodes)
    {
        var predictions = new int[components.Length];
        int total = interleaved ? mcusWide * mcusHigh : components[0].BlocksWide * components[0].BlocksHigh;
        for (int mcu = 0; mcu < total; mcu++)
        {
            if (interleaved)
            {
                int mx = mcu % mcusWide, my = mcu / mcusWide;
                foreach (var c in scanOrder)
                {
                    int index = Array.IndexOf(components, c);
                    for (int v = 0; v < c.V; v++)
                        for (int h = 0; h < c.H; h++)
                            EncodeBlock(c, ((my * c.V + v) * c.BlocksWide + mx * c.H + h) * 64, ref predictions[index],
                                writer, dcFreq?[c.DcTable], acFreq?[c.AcTable], dcCodes?[c.DcTable], acCodes?[c.AcTable]);
                }
            }
            else
            {
                var c = components[0];
                EncodeBlock(c, mcu * 64, ref predictions[0], writer, dcFreq?[c.DcTable], acFreq?[c.AcTable], dcCodes?[c.DcTable], acCodes?[c.AcTable]);
            }
        }
    }

    private static void EncodeBlock(Component c, int offset, ref int prediction, BitWriter? writer,
        long[]? dcFreq, long[]? acFreq, (int code, int length)[]? dcCodes, (int code, int length)[]? acCodes)
    {
        var block = c.Coefficients;
        int diff = block[offset] - prediction;
        prediction = block[offset];
        int size = BitLength(diff);
        Emit(size, diff, dcFreq, dcCodes, writer);

        int run = 0;
        for (int k = 1; k < 64; k++)
        {
            int value = block[offset + k];
            if (value == 0) { run++; continue; }
            while (run > 15)
            {
                Emit(0xF0, 0, acFreq, acCodes, writer, valueBits: 0); // sixteen zeros
                run -= 16;
            }
            int bits = BitLength(value);
            Emit((run << 4) | bits, value, acFreq, acCodes, writer, bits);
            run = 0;
        }
        if (run > 0) Emit(0x00, 0, acFreq, acCodes, writer, valueBits: 0); // end of block
    }

    private static void Emit(int symbol, int value, long[]? frequencies, (int code, int length)[]? codes, BitWriter? writer, int valueBits = -1)
    {
        if (valueBits < 0) valueBits = symbol; // DC: the symbol is the number of value bits
        if (frequencies != null)
        {
            frequencies[symbol]++;
            return;
        }
        var (code, length) = codes![symbol];
        writer!.Write(code, length);
        if (valueBits > 0) writer.Write(value < 0 ? value - 1 : value, valueBits);
    }

    private static int BitLength(int value)
    {
        value = Math.Abs(value);
        int bits = 0;
        while (value > 0) { bits++; value >>= 1; }
        return bits;
    }

    /// <summary>Builds an optimal length-limited Huffman table (JPEG spec, Annex K.2) and appends it to a DHT payload.</summary>
    private static (int code, int length)[] BuildTable(long[] counts, int tableClass, int id, MemoryStream dht)
    {
        var freq = (long[])counts.Clone();
        freq[256] = 1; // reserved so no real code is all ones
        var codeSize = new int[257];
        var others = Enumerable.Repeat(-1, 257).ToArray();
        while (true)
        {
            int v1 = -1, v2 = -1;
            long f1 = long.MaxValue, f2 = long.MaxValue;
            for (int i = 0; i < 257; i++)
            {
                if (freq[i] == 0) continue;
                if (freq[i] <= f1) { v2 = v1; f2 = f1; v1 = i; f1 = freq[i]; }
                else if (freq[i] <= f2) { v2 = i; f2 = freq[i]; }
            }
            if (v2 < 0) break;
            freq[v1] += freq[v2];
            freq[v2] = 0;
            codeSize[v1]++;
            while (others[v1] >= 0) { v1 = others[v1]; codeSize[v1]++; }
            others[v1] = v2;
            codeSize[v2]++;
            while (others[v2] >= 0) { v2 = others[v2]; codeSize[v2]++; }
        }

        var bits = new int[33];
        for (int i = 0; i < 257; i++)
            if (codeSize[i] > 0) bits[codeSize[i]]++;
        for (int i = 32; i > 16; i--)
        {
            while (bits[i] > 0)
            {
                int j = i - 2;
                while (bits[j] == 0) j--;
                bits[i] -= 2;
                bits[i - 1]++;
                bits[j + 1] += 2;
                bits[j]--;
            }
        }
        int last = 16;
        while (bits[last] == 0) last--;
        bits[last]--; // drop the reserved symbol

        var values = new List<int>();
        for (int length = 1; length <= 32; length++)
            for (int symbol = 0; symbol < 256; symbol++)
                if (codeSize[symbol] == length) values.Add(symbol);

        dht.WriteByte((byte)((tableClass << 4) | id));
        for (int i = 1; i <= 16; i++) dht.WriteByte((byte)bits[i]);
        foreach (var v in values) dht.WriteByte((byte)v);

        var codes = new (int code, int length)[256];
        int code = 0, k = 0;
        for (int length = 1; length <= 16; length++)
        {
            for (int i = 0; i < bits[length]; i++) codes[values[k++]] = (code++, length);
            code <<= 1;
        }
        return codes;
    }

    private static void WriteFrameHeader(Stream output, int marker, int width, int height, Component[] components)
    {
        var payload = new MemoryStream();
        payload.WriteByte(8);
        payload.WriteByte((byte)(height >> 8)); payload.WriteByte((byte)height);
        payload.WriteByte((byte)(width >> 8)); payload.WriteByte((byte)width);
        payload.WriteByte((byte)components.Length);
        foreach (var c in components)
        {
            payload.WriteByte((byte)c.Id);
            payload.WriteByte((byte)((c.H << 4) | c.V));
            payload.WriteByte((byte)c.QuantTable);
        }
        WriteSegment(output, marker, payload.ToArray());
    }

    private static void WriteScanHeader(Stream output, Component[] scanOrder)
    {
        var payload = new MemoryStream();
        payload.WriteByte((byte)scanOrder.Length);
        foreach (var c in scanOrder)
        {
            payload.WriteByte((byte)c.Id);
            payload.WriteByte((byte)((c.DcTable << 4) | c.AcTable));
        }
        payload.Write(new byte[] { 0, 63, 0 });
        WriteSegment(output, 0xDA, payload.ToArray());
    }

    private static void WriteSegment(Stream output, int marker, byte[] payload)
    {
        int length = payload.Length + 2;
        output.Write(new[] { (byte)0xFF, (byte)marker, (byte)(length >> 8), (byte)length });
        output.Write(payload);
    }

    private sealed class BitWriter(Stream output)
    {
        private long _buffer;
        private int _count;

        public void Write(int value, int length)
        {
            _buffer = (_buffer << length) | (uint)(value & ((1 << length) - 1));
            _count += length;
            while (_count >= 8)
            {
                byte b = (byte)(_buffer >> (_count - 8));
                output.WriteByte(b);
                if (b == 0xFF) output.WriteByte(0); // byte stuffing
                _count -= 8;
            }
            _buffer &= (1L << _count) - 1;
        }

        /// <summary>Pads the last byte with 1-bits, as the spec requires.</summary>
        public void Flush()
        {
            if (_count > 0) Write((1 << (8 - _count)) - 1, 8 - _count);
        }
    }

    private static int DivCeil(int a, int b) => (a + b - 1) / b;
}
