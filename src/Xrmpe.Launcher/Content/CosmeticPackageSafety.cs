using System.Buffers.Binary;
using System.Text;
using System.Text.Json;

namespace Xrmpe.Launcher.Content;

// A non-reference hash is usable only after the pinned local builder has produced
// it, and only while its archive contains exactly the expected cosmetic assets.
public static class CosmeticPackageSafety
{
    private static readonly Lazy<HashSet<string>> ExpectedNames = new(() =>
    {
        using var stream = typeof(CosmeticPackageSafety).Assembly.GetManifestResourceStream("Xrmpe.Launcher.CosmeticCostumes.json")
            ?? throw new InvalidDataException("Нет списка разрешённых моделей повязок.");
        using var doc = JsonDocument.Parse(stream);
        var names = doc.RootElement.EnumerateArray()
            .Select(x => x.GetProperty("model").GetString()!.ToLowerInvariant()).ToHashSet(StringComparer.Ordinal);
        names.UnionWith(new[]
        {
            "meshes\\actors\\stalker_mp\\stalker_neutral_1_face_1.ogf",
            "meshes\\actors\\stalker_mp\\stalker_neutral_1_mask.ogf",
            "meshes\\actors\\stalker_mp\\stalker_neutral_1.ogf",
            "textures\\act\\act_mp_tdm_green_1.dds"
        });
        if (names.Count != 34) throw new InvalidDataException("Неверный список разрешённых моделей повязок.");
        return names;
    });

    private static uint U32(ReadOnlySpan<byte> bytes, int offset)
    {
        if (offset < 0 || offset > bytes.Length - 4) throw new InvalidDataException("Обрезан косметический DB.");
        return BinaryPrimitives.ReadUInt32LittleEndian(bytes[offset..]);
    }

    private static ushort U16(ReadOnlySpan<byte> bytes, int offset)
    {
        if (offset < 0 || offset > bytes.Length - 2) throw new InvalidDataException("Обрезан каталог косметического DB.");
        return BinaryPrimitives.ReadUInt16LittleEndian(bytes[offset..]);
    }

    private static uint Crc32(ReadOnlySpan<byte> bytes)
    {
        uint crc = 0xFFFFFFFF;
        foreach (var b in bytes)
        {
            crc ^= b;
            for (var bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0xEDB88320u : 0u);
        }
        return ~crc;
    }

    private static ReadOnlySpan<byte> Chunk(ReadOnlySpan<byte> bytes, uint wanted, bool last = false)
    {
        ReadOnlySpan<byte> found = default;
        for (var offset = 0; offset < bytes.Length;)
        {
            var id = U32(bytes, offset); var size = U32(bytes, offset + 4);
            if (size > bytes.Length - offset - 8) throw new InvalidDataException("Обрезан блок модели повязок.");
            if (id == wanted || last) found = bytes.Slice(offset + 8, (int)size);
            offset += 8 + (int)size;
        }
        if (found.IsEmpty) throw new InvalidDataException("Не найдена поверхность повязки в модели.");
        return found;
    }

    public static void Verify(string path)
    {
        ContentPaths.NoLinks(path);
        var info = new FileInfo(path);
        if (!info.Exists || info.Length is < 1024 or > 50 * 1024 * 1024)
            throw new InvalidDataException("Неверный размер косметического DB.");
        var bytes = File.ReadAllBytes(path);
        ReadOnlySpan<byte> db = bytes;
        int pos = 0, dataStart = -1, dataEnd = -1, tocStart = -1, tocEnd = -1;
        var chunks = 0;
        while (pos < db.Length)
        {
            var id = U32(db, pos); var length = U32(db, pos + 4);
            if (length > db.Length - pos - 8) throw new InvalidDataException("Повреждён блок косметического DB.");
            var begin = pos + 8; var end = begin + (int)length;
            if (id == 666 && chunks == 0)
            {
                var header = Encoding.ASCII.GetString(db[begin..end]);
                if (!header.Contains("entry_point = $fs_root$\\gamedata\\", StringComparison.Ordinal))
                    throw new InvalidDataException("Неверная точка подключения косметического DB.");
            }
            else if (id == 0 && chunks == 1) { dataStart = begin; dataEnd = end; }
            else if (id == 1 && chunks == 2) { tocStart = begin; tocEnd = end; }
            else throw new InvalidDataException("В косметическом DB есть посторонний блок.");
            pos = end; chunks++;
        }
        if (chunks != 3 || dataStart < 0 || tocStart < 0 || tocEnd - tocStart > 8192)
            throw new InvalidDataException("Неверная структура косметического DB.");
        var found = new HashSet<string>(StringComparer.Ordinal);
        pos = tocStart;
        while (pos < tocEnd)
        {
            var record = U16(db, pos);
            if (record < 16 || record > 512 || record > tocEnd - pos - 2)
                throw new InvalidDataException("Повреждена запись косметического DB.");
            var real = U32(db, pos + 2); var packed = U32(db, pos + 6); var crc = U32(db, pos + 10);
            var name = Encoding.ASCII.GetString(db.Slice(pos + 14, record - 16)).ToLowerInvariant();
            var offset = U32(db, pos + 2 + record - 4);
            if (!ExpectedNames.Value.Contains(name) || !found.Add(name) || real != packed ||
                packed is 0 or > 8 * 1024 * 1024 || offset < dataStart || offset > dataEnd || packed > dataEnd - offset)
                throw new InvalidDataException("Косметический DB содержит посторонний или повреждённый ресурс.");
            var payload = db.Slice((int)offset, (int)packed);
            if (Crc32(payload) != crc) throw new InvalidDataException("CRC ресурса косметического DB не совпадает.");
            if (name.EndsWith(".dds", StringComparison.Ordinal))
            {
                if (!payload.StartsWith("DDS "u8)) throw new InvalidDataException("Повреждена текстура косметического DB.");
            }
            else
            {
                if (U32(payload, 0) != 1 || U32(payload, 4) > payload.Length - 8)
                    throw new InvalidDataException("Повреждена модель косметического DB.");
                var child = Chunk(Chunk(payload, 9), 0, last: true);
                var material = Encoding.ASCII.GetString(Chunk(child, 2)).Split('\0');
                if (material.Length < 2 || material[0] != "act\\act_mp_tdm_green_1" || material[1] != "models\\selflightl")
                    throw new InvalidDataException("В косметическом DB обнаружен неподходящий материал или текстура модели.");
            }
            pos += record + 2;
        }
        if (!found.SetEquals(ExpectedNames.Value))
            throw new InvalidDataException("Косметический DB содержит не все 33 модели и текстуру.");
    }
}
