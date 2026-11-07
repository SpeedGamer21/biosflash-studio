namespace BiosFlashStudio.Analysis;

/// <summary>
/// EFI 1.1 and Tiano compression (PI spec "EFI_STANDARD_COMPRESSION", used by AMI Aptio 4, Phoenix and older Insyde firmware).
/// LZ77 + static Huffman blocks; the two variants differ only in the width of the position-code count (4 vs 5 bits) and
/// therefore in the window size. The decoder follows the EDK2 reference (BaseUefiDecompressLib) bit for bit, so anything it
/// accepts is accepted by the firmware; the encoder is an independent implementation that emits streams in that format.
/// Stream layout: u32 compressed size, u32 original size, then the bit stream (MSB first).
/// </summary>
public static class EfiCompression
{
    public enum Variant { Efi = 1, Tiano = 2 }

    const int BITBUFSIZ = 32, MAXMATCH = 256, THRESHOLD = 3, CBIT = 9, TBIT = 5;
    const int NC = 0xFF + MAXMATCH + 2 - THRESHOLD;   // 510 literal/length symbols
    const int MAXNP = (1 << 5) - 1;                   // 31
    const int NT = 16 + 3;                            // 19 code-length symbols
    const int NPT = MAXNP;                            // 31 (> NT)

    static int PBit(Variant v) => v == Variant.Efi ? 4 : 5;
    /// <summary>Number of position symbols the encoder uses (window = 2^(NP-1)).</summary>
    static int Np(Variant v) => v == Variant.Efi ? 14 : 20;

    public static bool TryGetSizes(byte[] d, long off, long len, out uint compSize, out uint origSize)
    {
        compSize = origSize = 0;
        if (len < 8 || off < 0 || off + 8 > d.Length) return false;
        compSize = BitConverter.ToUInt32(d, (int)off);
        origSize = BitConverter.ToUInt32(d, (int)off + 4);
        return (long)compSize + 8 <= len;
    }

    // ================================================================== decoder (EDK2 UefiDecompress)
    sealed class Decoder
    {
        public byte[] Src; public int InBuf; public uint CompSize;
        public byte[] Dst; public int OutBuf; public int OrigSize;
        public uint BitBuf, SubBitBuf; public int BitCount;
        public int BlockSize;
        public bool Bad;
        public int PBit;
        public readonly ushort[] Left = new ushort[2 * NC - 1], Right = new ushort[2 * NC - 1];
        public readonly byte[] CLen = new byte[NC], PTLen = new byte[NPT];
        public readonly ushort[] CTable = new ushort[4096], PTTable = new ushort[256];

        public void FillBuf(int n)
        {
            BitBuf = n >= 32 ? 0 : BitBuf << n;
            while (n > BitCount)
            {
                n -= BitCount;
                BitBuf |= n >= 32 ? 0 : SubBitBuf << n;
                if (CompSize > 0) { CompSize--; SubBitBuf = InBuf < Src.Length ? Src[InBuf++] : 0u; }
                else SubBitBuf = 0;
                BitCount = 8;
            }
            BitCount -= n;
            BitBuf |= SubBitBuf >> BitCount;
        }

        uint GetBits(int n)
        {
            uint r = n == 0 ? 0 : BitBuf >> (BITBUFSIZ - n);
            FillBuf(n);
            return r;
        }

        bool MakeTable(int numOfChar, byte[] bitLen, int tableBits, ushort[] table)
        {
            var count = new ushort[17];
            var weight = new ushort[17];
            var start = new ushort[18];
            for (int i = 0; i < numOfChar; i++)
            {
                if (bitLen[i] > 16) return false;
                count[bitLen[i]]++;
            }
            for (int i = 1; i <= 16; i++) start[i + 1] = (ushort)(start[i] + (count[i] << (16 - i)));
            if (start[17] != 0) return false;   // the code must be complete (Kraft sum exactly 2^16, wrapping to 0)

            int juBits = 16 - tableBits;
            int k;
            for (k = 1; k <= tableBits; k++) { start[k] >>= juBits; weight[k] = (ushort)(1 << (tableBits - k)); }
            for (; k <= 16; k++) weight[k] = (ushort)(1 << (16 - k));

            int idx = start[tableBits + 1] >> juBits;
            if (idx != 0)
            {
                int idx3 = 1 << tableBits;
                if (idx < idx3) Array.Fill(table, (ushort)0, idx, idx3 - idx);
            }

            int avail = numOfChar;
            int mask = 1 << (15 - tableBits);
            int maxTableLength = 1 << tableBits;
            for (int ch = 0; ch < numOfChar; ch++)
            {
                int len = bitLen[ch];
                if (len == 0 || len >= 17) continue;
                int nextCode = (ushort)(start[len] + weight[len]);
                if (len <= tableBits)
                {
                    if (start[len] >= nextCode || nextCode > maxTableLength) return false;
                    for (int i = start[len]; i < nextCode; i++) table[i] = (ushort)ch;
                }
                else
                {
                    int index3 = start[len];
                    ref ushort p = ref table[index3 >> juBits];
                    for (int i = len - tableBits; i != 0; i--)
                    {
                        if (p == 0 && avail < 2 * NC - 1)
                        {
                            Right[avail] = Left[avail] = 0;
                            p = (ushort)avail++;
                        }
                        if (p < 2 * NC - 1)
                        {
                            if ((index3 & mask) != 0) p = ref Right[p];
                            else p = ref Left[p];
                        }
                        index3 = (ushort)(index3 << 1);
                    }
                    p = (ushort)ch;
                }
                start[len] = (ushort)nextCode;
            }
            return true;
        }

        int DecodeP()
        {
            int val = PTTable[BitBuf >> (BITBUFSIZ - 8)];
            if (val >= MAXNP)
            {
                uint m = 1u << (BITBUFSIZ - 1 - 8);
                do { val = (BitBuf & m) != 0 ? Right[val] : Left[val]; m >>= 1; } while (val >= MAXNP && m != 0);
                if (val >= MAXNP) { Bad = true; return 0; }
            }
            FillBuf(PTLen[val]);
            int pos = val;
            if (val > 1) pos = (int)((1u << (val - 1)) + GetBits(val - 1));
            return pos;
        }

        bool ReadPTLen(int nn, int nbit, int special)
        {
            int number = (int)GetBits(nbit);
            if (number > NPT || nn > NPT) return false;
            if (number == 0)
            {
                ushort c = (ushort)GetBits(nbit);
                Array.Fill(PTTable, c);
                Array.Clear(PTLen, 0, nn);
                return c < nn;
            }
            int index = 0;
            while (index < number && index < NPT)
            {
                int c = (int)(BitBuf >> (BITBUFSIZ - 3));
                if (c == 7)
                {
                    uint m = 1u << (BITBUFSIZ - 1 - 3);
                    while ((m & BitBuf) != 0) { m >>= 1; c++; }
                    if (c > 16) return false;
                }
                FillBuf(c < 7 ? 3 : c - 3);
                PTLen[index++] = (byte)c;
                if (index == special)
                {
                    int z = (int)GetBits(2);
                    while (--z >= 0 && index < NPT) PTLen[index++] = 0;
                }
            }
            while (index < nn && index < NPT) PTLen[index++] = 0;
            return MakeTable(nn, PTLen, 8, PTTable);
        }

        bool ReadCLen()
        {
            int number = (int)GetBits(CBIT);
            if (number == 0)
            {
                ushort c = (ushort)GetBits(CBIT);
                Array.Clear(CLen);
                Array.Fill(CTable, c);
                return c < NC;
            }
            int index = 0;
            while (index < number && index < NC)
            {
                int c = PTTable[BitBuf >> (BITBUFSIZ - 8)];
                if (c >= NT)
                {
                    uint m = 1u << (BITBUFSIZ - 1 - 8);
                    do { c = (m & BitBuf) != 0 ? Right[c] : Left[c]; m >>= 1; } while (c >= NT && m != 0);
                    if (c >= NT) return false;
                }
                FillBuf(PTLen[c]);
                if (c <= 2)
                {
                    if (c == 0) c = 1;
                    else if (c == 1) c = (int)GetBits(4) + 3;
                    else c = (int)GetBits(CBIT) + 20;
                    while (--c >= 0 && index < NC) CLen[index++] = 0;
                }
                else CLen[index++] = (byte)(c - 2);
            }
            Array.Clear(CLen, index, NC - index);
            return MakeTable(NC, CLen, 12, CTable);
        }

        int DecodeC()
        {
            if (BlockSize == 0)
            {
                BlockSize = (int)GetBits(16);
                if (!ReadPTLen(NT, TBIT, 3)) { Bad = true; return 0; }
                if (!ReadCLen()) { Bad = true; return 0; }
                if (!ReadPTLen(MAXNP, PBit, -1)) { Bad = true; return 0; }
            }
            BlockSize--;
            int idx = CTable[BitBuf >> (BITBUFSIZ - 12)];
            if (idx >= NC)
            {
                uint m = 1u << (BITBUFSIZ - 1 - 12);
                do { idx = (BitBuf & m) != 0 ? Right[idx] : Left[idx]; m >>= 1; } while (idx >= NC && m != 0);
                if (idx >= NC) { Bad = true; return 0; }
            }
            FillBuf(CLen[idx]);
            return idx;
        }

        public void Decode()
        {
            while (OutBuf < OrigSize)
            {
                int c = DecodeC();
                if (Bad) return;
                if (c < 256) { Dst[OutBuf++] = (byte)c; continue; }
                int len = c - (0x100 - THRESHOLD);
                int src = OutBuf - DecodeP() - 1;
                if (Bad) return;
                if (src < 0) { Bad = true; return; }
                while (len-- > 0 && OutBuf < OrigSize) Dst[OutBuf++] = Dst[src++];
            }
        }
    }

    /// <summary>Decompresses a stream (with its 8-byte header) at <paramref name="off"/>. Throws on corrupt data.</summary>
    public static byte[] Decompress(byte[] d, long off, long len, Variant v, long maxSize = 64L << 20)
    {
        if (!TryGetSizes(d, off, len, out var comp, out var orig)) throw new InvalidDataException("bad header");
        if (orig > maxSize) throw new InvalidDataException("size too large");
        var dec = new Decoder
        {
            Src = d, InBuf = (int)off + 8, CompSize = comp, Dst = new byte[orig], OrigSize = (int)orig, PBit = PBit(v),
        };
        if (orig == 0) return dec.Dst;
        dec.Src = new ReadOnlySpan<byte>(d, (int)off + 8, (int)comp).ToArray();
        dec.InBuf = 0;
        dec.FillBuf(BITBUFSIZ);
        dec.Decode();
        if (dec.Bad || dec.OutBuf != orig) throw new InvalidDataException("corrupt stream");
        // the decoder may pre-fetch up to 4 bytes beyond the last symbol, but must not need data past the stream
        return dec.Dst;
    }

    public static bool TryDecompress(byte[] d, long off, long len, Variant v, out byte[] result)
    {
        try { result = Decompress(d, off, len, v); return true; }
        catch { result = null; return false; }
    }

    // ================================================================== encoder
    sealed class BitWriter
    {
        readonly List<byte> _out = new();
        uint _acc; int _n;
        public void Put(int nbits, uint value)
        {
            for (int i = nbits - 1; i >= 0; i--)
            {
                _acc = (_acc << 1) | ((value >> i) & 1);
                if (++_n == 8) { _out.Add((byte)_acc); _acc = 0; _n = 0; }
            }
        }
        public byte[] Finish()
        {
            if (_n > 0) { _out.Add((byte)(_acc << (8 - _n))); _acc = 0; _n = 0; }
            return _out.ToArray();
        }
    }

    readonly record struct Token(ushort C, int Pos);   // C < 256 literal; else length symbol, Pos = distance-1

    /// <summary>Huffman code lengths limited to <paramref name="maxLen"/> bits (frequencies are flattened until they fit).</summary>
    static byte[] CodeLengths(int[] freq, int maxLen)
    {
        var f = (int[])freq.Clone();
        while (true)
        {
            var len = HuffmanLengths(f);
            if (len.All(l => l <= maxLen)) return len.Select(l => (byte)l).ToArray();
            for (int i = 0; i < f.Length; i++) if (f[i] > 0) f[i] = Math.Max(1, f[i] >> 1);
        }
    }

    static int[] HuffmanLengths(int[] freq)
    {
        int n = freq.Length;
        var len = new int[n];
        var pq = new PriorityQueue<int, (long w, int id)>();
        var parent = new List<int>();
        var weight = new List<long>();
        for (int i = 0; i < n; i++)
        {
            parent.Add(-1); weight.Add(freq[i]);
            if (freq[i] > 0) pq.Enqueue(i, (freq[i], i));
        }
        if (pq.Count < 2) return len;
        while (pq.Count > 1)
        {
            pq.TryDequeue(out int a, out var pa);
            pq.TryDequeue(out int b, out var pb);
            int id = parent.Count;
            parent.Add(-1); weight.Add(pa.w + pb.w);
            parent[a] = id; parent[b] = id;
            pq.Enqueue(id, (pa.w + pb.w, id));
        }
        for (int i = 0; i < n; i++)
        {
            if (freq[i] == 0) continue;
            int d = 0;
            for (int x = i; parent[x] >= 0; x = parent[x]) d++;
            len[i] = d;
        }
        return len;
    }

    /// <summary>Canonical codes exactly as the decoder's MakeTable assigns them (shorter first, then by symbol).</summary>
    static ushort[] Codes(byte[] len)
    {
        var count = new int[18];
        foreach (var l in len) count[l]++;
        var start = new int[18];
        for (int i = 1; i <= 16; i++) start[i + 1] = start[i] + (count[i] << (16 - i));
        var codes = new ushort[len.Length];
        for (int s = 0; s < len.Length; s++)
        {
            int l = len[s];
            if (l == 0) continue;
            codes[s] = (ushort)(start[l] >> (16 - l));
            start[l] += 1 << (16 - l);
        }
        return codes;
    }

    static int PSym(int pos) => pos == 0 ? 0 : 32 - System.Numerics.BitOperations.LeadingZeroCount((uint)pos);

    static void WritePtLen(BitWriter w, byte[] len, int count, int nbit, int special)
    {
        int n = count;
        while (n > 0 && len[n - 1] == 0) n--;
        w.Put(nbit, (uint)n);
        for (int i = 0; i < n;)
        {
            int l = len[i++];
            if (l <= 6) w.Put(3, (uint)l);
            else { w.Put(3, 7); for (int k = 7; k < l; k++) w.Put(1, 1); w.Put(1, 0); }
            if (i == special)
            {
                int z = 0;
                while (z < 3 && i + z < n && len[i + z] == 0) z++;
                w.Put(2, (uint)z);
                i += z;
            }
        }
    }

    static void WriteBlock(BitWriter w, List<Token> toks, int from, int to, Variant v)
    {
        int np = Np(v), pbit = PBit(v);
        var cFreq = new int[NC];
        var pFreq = new int[np];
        for (int i = from; i < to; i++)
        {
            cFreq[toks[i].C]++;
            if (toks[i].C >= 256) pFreq[PSym(toks[i].Pos)]++;
        }

        w.Put(16, (uint)(to - from));

        // --- C lengths (and the T code that transmits them)
        var cLen = CodeLengths(cFreq, 16);
        int cUsed = cFreq.Count(x => x > 0);
        int singleC = cUsed == 1 ? Array.FindIndex(cFreq, x => x > 0) : -1;
        if (singleC >= 0)
        {
            w.Put(TBIT, 0); w.Put(TBIT, 0);           // dummy T table (single symbol 0)
            w.Put(CBIT, 0); w.Put(CBIT, (uint)singleC);
            cLen = new byte[NC];
        }
        else
        {
            int cn = NC;
            while (cn > 0 && cLen[cn - 1] == 0) cn--;
            // run-length symbols over cLen[0..cn)
            var tSyms = new List<(int sym, int extraBits, int extra)>();
            for (int i = 0; i < cn;)
            {
                if (cLen[i] == 0)
                {
                    int run = 0;
                    while (i + run < cn && cLen[i + run] == 0) run++;
                    i += run;
                    while (run > 0)
                    {
                        if (run <= 2) { for (int k = 0; k < run; k++) tSyms.Add((0, 0, 0)); run = 0; }
                        else if (run == 19) { tSyms.Add((0, 0, 0)); tSyms.Add((1, 4, 15)); run = 0; }
                        else if (run <= 18) { tSyms.Add((1, 4, run - 3)); run = 0; }
                        else { int r = Math.Min(run, 20 + 511); tSyms.Add((2, CBIT, r - 20)); run -= r; }
                    }
                }
                else { tSyms.Add((cLen[i] + 2, 0, 0)); i++; }
            }
            var tFreq = new int[NT];
            foreach (var t in tSyms) tFreq[t.sym]++;
            int tUsed = tFreq.Count(x => x > 0);
            byte[] tLen;
            ushort[] tCode;
            if (tUsed == 1)
            {
                int s = Array.FindIndex(tFreq, x => x > 0);
                w.Put(TBIT, 0); w.Put(TBIT, (uint)s);
                tLen = new byte[NT];
                tCode = new ushort[NT];
            }
            else
            {
                tLen = CodeLengths(tFreq, 16);
                tCode = Codes(tLen);
                WritePtLen(w, tLen, NT, TBIT, 3);
            }
            w.Put(CBIT, (uint)cn);
            foreach (var t in tSyms)
            {
                if (tLen[t.sym] > 0) w.Put(tLen[t.sym], tCode[t.sym]);
                if (t.extraBits > 0) w.Put(t.extraBits, (uint)t.extra);
            }
        }
        var cCode = Codes(cLen);

        // --- P lengths
        int pUsed = pFreq.Count(x => x > 0);
        byte[] pLen;
        if (pUsed <= 1)
        {
            int s = Math.Max(0, Array.FindIndex(pFreq, x => x > 0));
            w.Put(pbit, 0); w.Put(pbit, (uint)s);
            pLen = new byte[np];
        }
        else
        {
            pLen = CodeLengths(pFreq, 16);
            WritePtLen(w, pLen, np, pbit, -1);
        }
        var pCode = Codes(pLen);

        // --- symbols
        for (int i = from; i < to; i++)
        {
            var t = toks[i];
            if (cLen[t.C] > 0) w.Put(cLen[t.C], cCode[t.C]);
            if (t.C < 256) continue;
            int ps = PSym(t.Pos);
            if (pLen[ps] > 0) w.Put(pLen[ps], pCode[ps]);
            if (ps > 1) w.Put(ps - 1, (uint)(t.Pos - (1 << (ps - 1))));
        }
    }

    /// <summary>LZ77 parse with hash chains and one-step lazy matching.</summary>
    static List<Token> Parse(byte[] data, int window, int chainLimit, bool lazy)
    {
        var toks = new List<Token>(data.Length / 2);
        const int HB = 16, HS = 1 << HB;
        var head = new int[HS];
        Array.Fill(head, -1);
        var prev = new int[data.Length];
        int Hash(int i) => ((data[i] << 10) ^ (data[i + 1] << 5) ^ data[i + 2]) & (HS - 1);
        void Insert(int i)
        {
            if (i + 2 >= data.Length) return;
            int h = Hash(i);
            prev[i] = head[h];
            head[h] = i;
        }
        (int len, int dist) Find(int i)
        {
            int best = 0, bestDist = 0;
            if (i + THRESHOLD > data.Length) return (0, 0);
            int max = Math.Min(MAXMATCH, data.Length - i);
            int cand = head[Hash(i)];
            for (int n = 0; cand >= 0 && n < chainLimit; n++, cand = prev[cand])
            {
                int dist = i - cand;
                if (dist > window) break;
                if (data[cand + best] != data[i + best]) continue;
                int l = 0;
                while (l < max && data[cand + l] == data[i + l]) l++;
                if (l > best) { best = l; bestDist = dist; if (l == max) break; }
            }
            return best >= THRESHOLD ? (best, bestDist) : (0, 0);
        }

        int p = 0;
        while (p < data.Length)
        {
            var (len, dist) = Find(p);
            if (len > 0 && lazy && len < 32 && p + 1 < data.Length)
            {
                Insert(p);
                var (len2, dist2) = Find(p + 1);
                if (len2 > len + 1 || (len2 == len + 1 && dist2 < 4096))
                {
                    toks.Add(new Token(data[p], 0));
                    p++;
                    len = len2; dist = dist2;
                    // p already inserted; fall through to emit the match at p
                }
                else
                {
                    toks.Add(new Token((ushort)(len + 0x100 - THRESHOLD), dist - 1));
                    for (int k = 1; k < len; k++) Insert(p + k);
                    p += len;
                    continue;
                }
            }
            if (len > 0)
            {
                toks.Add(new Token((ushort)(len + 0x100 - THRESHOLD), dist - 1));
                for (int k = 0; k < len; k++) Insert(p + k);
                p += len;
            }
            else
            {
                toks.Add(new Token(data[p], 0));
                Insert(p);
                p++;
            }
        }
        return toks;
    }

    /// <summary>Compresses <paramref name="data"/> and returns the stream with its 8-byte header.</summary>
    public static byte[] Compress(byte[] data, Variant v, int chainLimit = 4096, bool lazy = true, int blockTokens = 16384)
    {
        int window = 1 << (Np(v) - 1);
        var toks = Parse(data, window, chainLimit, lazy);
        var w = new BitWriter();
        for (int i = 0; i < toks.Count; i += blockTokens)
            WriteBlock(w, toks, i, Math.Min(toks.Count, i + blockTokens), v);
        var body = w.Finish();
        var full = new byte[8 + body.Length];
        BitConverter.GetBytes((uint)body.Length).CopyTo(full, 0);
        BitConverter.GetBytes((uint)data.Length).CopyTo(full, 4);
        body.CopyTo(full, 8);
        return full;
    }

    /// <summary>Streams of the same data from different encoder settings (lazily, strongest first).</summary>
    public static IEnumerable<byte[]> Variants(byte[] data, Variant v)
    {
        yield return Compress(data, v, 4096, true, 16384);
        yield return Compress(data, v, 4096, true, 4096);
        yield return Compress(data, v, 4096, false, 16384);
        yield return Compress(data, v, 1024, true, 65535);
    }
}
