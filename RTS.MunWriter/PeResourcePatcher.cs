using System.Buffers.Binary;

namespace RTS.MunWriter
{
    /// <summary>
    /// Replaces icon resources in a PE resource file (imageres.dll.mun) by editing its resource
    /// table directly.
    ///
    /// Why not UpdateResource: imageres.dll.mun carries MUI resource-configuration data (an "MUI"
    /// resource), which makes it a language-neutral (LN) file. UpdateResource refuses to change
    /// any resource type that configuration lists, and imageres.dll.mun lists RT_ICON,
    /// RT_GROUP_ICON and RT_BITMAP, so every icon write fails with ERROR_NOT_SUPPORTED (50).
    /// Microsoft documents the rule in the remarks of
    /// https://learn.microsoft.com/windows/win32/api/winbase/nf-winbase-updateresourcew, and a test
    /// on a copy of the 10.0.26100.7705 file confirmed it (2026-11-09).
    ///
    /// What this changes, and nothing else:
    ///   - the data and data-entry Size of each replaced resource. Data that fits the old slot is
    ///     written in place; larger data is appended to the end of the resource section, which
    ///     must be the file's last section with nothing after it.
    ///   - when the section grows: its VirtualSize and SizeOfRawData, the resource data-directory
    ///     size and SizeOfImage.
    ///   - the PE checksum.
    /// Every other byte stays identical, including the MUI configuration, every language and every
    /// resource not being replaced. Every offset read from the file is bounds-checked, and each
    /// edit is listed in <see cref="Changes"/>.
    /// </summary>
    internal sealed class PeResourcePatcher
    {
        public const int RT_ICON = 3, RT_GROUP_ICON = 14;

        private byte[] _b;
        private readonly int _checksumOff, _sizeOfImageOff, _resDirSizeOff, _secHdr, _root;
        private readonly uint _secVa, _secRawPtr, _fileAlign, _secAlign;
        private uint _virtSize, _rawSize;
        private bool _grew;

        /// <summary>Every byte range this patcher changed: what, file offset, length.</summary>
        public List<(string What, int Offset, int Length)> Changes { get; } = new();

        public PeResourcePatcher(byte[] file)
        {
            _b = file;
            if (U16(0) != 0x5A4D) throw Bad("no MZ header");
            int pe = (int)Math.Min(U32(0x3C), int.MaxValue);
            if (U32(pe) != 0x00004550) throw Bad("no PE signature");
            int nsec = U16(pe + 6), optSize = U16(pe + 20), opt = pe + 24;
            Need(opt, optSize);

            ushort magic = U16(opt);
            bool is64 = magic == 0x20B;
            if (magic != 0x10B && !is64) throw Bad("unknown optional header format");
            _secAlign = U32(opt + 32);
            _fileAlign = U32(opt + 36);
            if (!IsPow2(_secAlign) || !IsPow2(_fileAlign)) throw Bad("bad section or file alignment");
            _sizeOfImageOff = opt + 56;
            _checksumOff = opt + 64;
            if ((_checksumOff & 1) != 0) throw Bad("checksum field is not word-aligned");

            uint numDirs = U32(opt + (is64 ? 108 : 92));
            int dd = opt + (is64 ? 112 : 96);
            if (numDirs < 5 || numDirs > 16 || dd + numDirs * 8 > opt + optSize) throw Bad("bad data directories");
            uint resRva = U32(dd + 2 * 8), resSize = U32(dd + 2 * 8 + 4);
            if (resRva == 0 || resSize == 0) throw Bad("file has no resources");
            if (U32(dd + 4 * 8 + 4) != 0) throw Bad("file carries an Authenticode signature; editing it would break it");
            _resDirSizeOff = dd + 2 * 8 + 4;

            int table = opt + optSize;
            Need(table, nsec * 40);
            int resIdx = -1, lastRawIdx = -1, lastVaIdx = -1;
            long lastRawEnd = -1, lastVa = -1;
            for (int i = 0; i < nsec; i++)
            {
                int s = table + i * 40;
                uint vsz = U32(s + 8), va = U32(s + 12), rsz = U32(s + 16), rptr = U32(s + 20);
                if (resRva >= va && resRva < (long)va + Math.Max(vsz, rsz)) resIdx = i;
                if ((long)rptr + rsz > lastRawEnd) { lastRawEnd = (long)rptr + rsz; lastRawIdx = i; }
                if (va > lastVa) { lastVa = va; lastVaIdx = i; }
            }
            if (resIdx < 0) throw Bad("resource directory lies outside every section");
            if (resIdx != lastRawIdx || resIdx != lastVaIdx) throw Bad("resource section is not the last section");
            if (lastRawEnd != file.Length) throw Bad("file has data after its last section");

            _secHdr = table + resIdx * 40;
            _virtSize = U32(_secHdr + 8);
            _secVa = U32(_secHdr + 12);
            _rawSize = U32(_secHdr + 16);
            _secRawPtr = U32(_secHdr + 20);
            if (resRva != _secVa) throw Bad("resource directory does not start its section");
            if (_virtSize == 0 || _virtSize > _rawSize) throw Bad("unexpected resource section sizes");
            _root = (int)_secRawPtr;
            Need(_root, 16);
        }

        /// <summary>The checksum stored in the PE header.</summary>
        public uint HeaderChecksum => U32(_checksumOff);

        /// <summary>
        /// The PE checksum over the current bytes, as CheckSumMappedFile computes it: 16-bit
        /// one's-complement-style sum of the file with the checksum field treated as zero, plus
        /// the file length.
        /// </summary>
        public uint ComputeChecksum()
        {
            ulong sum = 0;
            int len = _b.Length;
            for (int i = 0; i < len; i += 2)
            {
                if (i == _checksumOff || i == _checksumOff + 2) continue;
                uint word = _b[i] | (i + 1 < len ? (uint)_b[i + 1] << 8 : 0u);
                sum += word;
                sum = (sum & 0xFFFF) + (sum >> 16);
            }
            sum = (sum & 0xFFFF) + (sum >> 16);
            return (uint)sum + (uint)len;
        }

        /// <summary>Data entries (language, file offset of the entry) for a numeric type and id.</summary>
        public List<(ushort Lang, int Entry)> Find(int type, int id)
        {
            var result = new List<(ushort, int)>();
            int? typeDir = Sub(_root, (uint)type);
            if (typeDir is null) return result;
            int? nameDir = Sub(typeDir.Value, (uint)id);
            if (nameDir is null) return result;
            foreach (var (lang, value) in IdEntries(nameDir.Value))
            {
                if ((value & 0x80000000) != 0) throw Bad($"type {type} id {id}: expected data, found a directory");
                result.Add(((ushort)lang, DirAt(value)));
            }
            return result;
        }

        public byte[] Read(int entry)
        {
            uint rva = U32(entry), size = U32(entry + 4);
            return _b.AsSpan(RvaToOff(rva, size), (int)size).ToArray();
        }

        public void Replace(int entry, byte[] data, string label)
        {
            uint rva = U32(entry), size = U32(entry + 4);
            if (data.Length <= size)
            {
                int off = RvaToOff(rva, size);
                data.CopyTo(_b, off);
                Array.Clear(_b, off + data.Length, (int)size - data.Length);
                Changes.Add(($"{label} data, in place", off, (int)size));
            }
            else
            {
                uint start = Align(_virtSize, 8);
                uint end = checked(start + (uint)data.Length);
                uint raw = Align(end, _fileAlign);
                if (raw > _rawSize)
                {
                    Array.Resize(ref _b, checked((int)(_secRawPtr + raw)));   // new bytes are zero
                    _rawSize = raw;
                }
                int off = (int)(_secRawPtr + start);
                data.CopyTo(_b, off);
                _virtSize = end;
                _grew = true;
                WriteU32(entry, _secVa + start);
                Changes.Add(($"{label} data, appended", off, data.Length));
            }
            WriteU32(entry + 4, (uint)data.Length);
            Changes.Add(($"{label} data entry", entry, 8));
        }

        /// <summary>
        /// Replaces one icon group, in every language it exists in. Each existing slot (child
        /// RT_ICON) gets the new frame closest in size, so the group keeps its child IDs and all
        /// of its sizes (16/24/32 included). Returns false if the group does not exist.
        /// </summary>
        public bool ReplaceIconGroup(int groupId, IReadOnlyList<(byte w, byte h, ushort bc, byte[] data)> frames)
        {
            if (frames.Count == 0) throw new ArgumentException("No icon frames.", nameof(frames));
            var groups = Find(RT_GROUP_ICON, groupId);
            if (groups.Count == 0) return false;

            foreach (var (lang, entry) in groups)
            {
                var children = ParseGroup(Read(entry), groupId);
                var pairs = children.Select(c => (c.Id, Frame: frames[BestFrame(frames, c.Size)])).ToList();
                foreach (var (id, frame) in pairs)
                {
                    var icons = Find(RT_ICON, id);
                    if (icons.Count == 0) throw Bad($"group {groupId} refers to missing icon {id}");
                    foreach (var (_, iconEntry) in icons) Replace(iconEntry, frame.data, $"RT_ICON {id}");
                }
                Replace(entry, BuildGroup(pairs), $"RT_GROUP_ICON {groupId} lang {lang}");
            }
            return true;
        }

        /// <summary>Writes the size fields if the section grew, then the checksum. Returns the file bytes.</summary>
        public byte[] Save()
        {
            if (_grew)
            {
                WriteU32(_secHdr + 8, _virtSize);
                Changes.Add(("section VirtualSize", _secHdr + 8, 4));
                WriteU32(_secHdr + 16, _rawSize);
                Changes.Add(("section SizeOfRawData", _secHdr + 16, 4));
                if (_virtSize > U32(_resDirSizeOff))
                {
                    WriteU32(_resDirSizeOff, _virtSize);
                    Changes.Add(("resource directory size", _resDirSizeOff, 4));
                }
                uint image = Align(_secVa + _virtSize, _secAlign);
                if (image > U32(_sizeOfImageOff))
                {
                    WriteU32(_sizeOfImageOff, image);
                    Changes.Add(("SizeOfImage", _sizeOfImageOff, 4));
                }
            }
            WriteU32(_checksumOff, ComputeChecksum());
            Changes.Add(("PE checksum", _checksumOff, 4));
            return _b;
        }

        // ── Icon group helpers ───────────────────────────────────────────────────

        public static List<(ushort Id, int Size)> ParseGroup(byte[] g, int groupId)
        {
            if (g.Length < 6 || BinaryPrimitives.ReadUInt16LittleEndian(g.AsSpan(2)) != 1)
                throw Bad($"group {groupId} is not an icon directory");
            int count = BinaryPrimitives.ReadUInt16LittleEndian(g.AsSpan(4));
            if (count == 0 || 6 + count * 14 > g.Length) throw Bad($"group {groupId} directory is truncated");
            var list = new List<(ushort, int)>(count);
            for (int i = 0; i < count; i++)
            {
                int e = 6 + i * 14;
                list.Add((BinaryPrimitives.ReadUInt16LittleEndian(g.AsSpan(e + 12)), g[e] == 0 ? 256 : g[e]));
            }
            return list;
        }

        public static int BestFrame(IReadOnlyList<(byte w, byte h, ushort bc, byte[] data)> frames, int size)
        {
            int best = 0, bestScore = int.MaxValue;
            for (int i = 0; i < frames.Count; i++)
            {
                int s = frames[i].w == 0 ? 256 : frames[i].w;
                // Exact size wins; otherwise the nearest, preferring larger (scaling down looks better).
                int score = s == size ? 0 : s > size ? (s - size) * 2 : (size - s) * 2 + 1;
                if (score < bestScore) { best = i; bestScore = score; }
            }
            return best;
        }

        public static byte[] BuildGroup(List<(ushort Id, (byte w, byte h, ushort bc, byte[] data) Frame)> pairs)
        {
            using var ms = new MemoryStream();
            using var w = new BinaryWriter(ms);
            w.Write((ushort)0); w.Write((ushort)1); w.Write((ushort)pairs.Count);
            foreach (var (id, f) in pairs)
            {
                w.Write(f.w); w.Write(f.h); w.Write((byte)0); w.Write((byte)0);
                w.Write((ushort)1); w.Write(f.bc); w.Write((uint)f.data.Length); w.Write(id);
            }
            return ms.ToArray();
        }

        // ── Resource tree ────────────────────────────────────────────────────────

        private IEnumerable<(uint Id, uint Value)> IdEntries(int dir)
        {
            int named = U16(dir + 12), ids = U16(dir + 14);
            Need(dir + 16, (named + ids) * 8);
            for (int i = 0; i < named + ids; i++)
            {
                int e = dir + 16 + i * 8;
                uint name = U32(e);
                if ((name & 0x80000000) != 0) continue;   // string-named entry; icons use numeric ids
                yield return (name, U32(e + 4));
            }
        }

        private int? Sub(int dir, uint id)
        {
            foreach (var (eid, value) in IdEntries(dir))
            {
                if (eid != id) continue;
                if ((value & 0x80000000) == 0) throw Bad($"id {id}: expected a directory, found data");
                return DirAt(value & 0x7FFFFFFF);
            }
            return null;
        }

        /// <summary>A directory or data entry inside the resource section, by offset from its start.</summary>
        private int DirAt(uint rel)
        {
            long off = (long)_root + rel;
            if (off + 16 > (long)_secRawPtr + _rawSize) throw Bad("resource table points outside its section");
            return (int)off;
        }

        private int RvaToOff(uint rva, uint len)
        {
            long off = (long)rva - _secVa + _secRawPtr;
            if (rva < _secVa || off + len > (long)_secRawPtr + _rawSize || off + len > _b.Length)
                throw Bad("resource data lies outside its section");
            return (int)off;
        }

        // ── Primitives ───────────────────────────────────────────────────────────

        private void Need(int off, int len)
        {
            if (off < 0 || len < 0 || (long)off + len > _b.Length) throw Bad("file is truncated or malformed");
        }

        private ushort U16(int off) { Need(off, 2); return BinaryPrimitives.ReadUInt16LittleEndian(_b.AsSpan(off)); }
        private uint U32(int off) { Need(off, 4); return BinaryPrimitives.ReadUInt32LittleEndian(_b.AsSpan(off)); }
        private void WriteU32(int off, uint v) { Need(off, 4); BinaryPrimitives.WriteUInt32LittleEndian(_b.AsSpan(off), v); }

        private static uint Align(uint v, uint a) => checked((v + a - 1) & ~(a - 1));
        private static bool IsPow2(uint v) => v != 0 && (v & (v - 1)) == 0;
        private static InvalidDataException Bad(string why) => new($"Not a patchable resource file: {why}.");
    }
}
