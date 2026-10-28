using System.Runtime.InteropServices;
using System.Security.Cryptography;

internal static class IconChecks
{
    delegate bool EnumName(nint module, nint type, nint name, nint parameter);
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)] static extern nint LoadLibraryEx(string file, nint reserved, uint flags);
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode)] static extern bool EnumResourceNames(nint module, nint type, EnumName callback, nint parameter);
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode)] static extern nint FindResource(nint module, nint name, nint type);
    [DllImport("kernel32.dll")] static extern nint LoadResource(nint module, nint resource);
    [DllImport("kernel32.dll")] static extern nint LockResource(nint resource);
    [DllImport("kernel32.dll")] static extern uint SizeofResource(nint module, nint resource);
    [DllImport("kernel32.dll")] static extern bool FreeLibrary(nint module);
    public static void Verify(string exe)
    {
        var module = LoadLibraryEx(exe, 0, 2); if (module == 0) throw new Exception("Cannot read native PE resources");
        try
        {
            byte[] Read(nint name, nint type)
            {
                var resource = FindResource(module,name,type); if (resource == 0) throw new Exception("Missing PE icon");
                var bytes = new byte[SizeofResource(module,resource)]; Marshal.Copy(LockResource(LoadResource(module,resource)),bytes,0,bytes.Length); return bytes;
            }
            var groups = new List<byte[]>(); EnumName callback = (m,t,n,p) => { groups.Add(Read(n,t)); return true; };
            EnumResourceNames(module,14,callback,0); GC.KeepAlive(callback);
            if (groups.Count != 1 || BitConverter.ToUInt16(groups[0],4) != 4) throw new Exception("Expected exactly four native icon sizes");
            var sizes = new List<int>(); var group = groups[0];
            for (int i=0; i<4; i++)
            {
                int at=6+14*i, size=group[at] == 0 ? 256 : group[at]; sizes.Add(size);
                var data=Read(BitConverter.ToUInt16(group,at+12),3);
                var png=File.ReadAllBytes($"artifacts/global-browser-preview/brand-candidates/zonelink-anomaly-{size}.png");
                if (!SHA256.HashData(data).SequenceEqual(SHA256.HashData(png))) throw new Exception("PE icon differs from selected anomaly: " + size + "; resource bytes=" + data.Length + "; first bytes=" + Convert.ToHexString(data[..8]) + "; expected bytes=" + png.Length);
            }
            if (!sizes.Order().SequenceEqual(new[]{16,32,48,256})) throw new Exception("Wrong native sizes");
            Console.WriteLine("PASS: native EXE icon 16/32/48/256; each PE image hash matches selected anomaly PNG. EXE was read, not executed.");
        }
        finally { FreeLibrary(module); }
    }
}
