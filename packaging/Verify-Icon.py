"""Read PE resources without executing the launcher; verify the native multi-size icon."""
import ctypes
from ctypes import wintypes
from pathlib import Path
import struct
import sys

exe = str(Path(sys.argv[1]).resolve())
kernel = ctypes.WinDLL("kernel32", use_last_error=True)
kernel.LoadLibraryExW.argtypes = [wintypes.LPCWSTR, wintypes.HANDLE, wintypes.DWORD]
kernel.LoadLibraryExW.restype = wintypes.HMODULE
kernel.FreeLibrary.argtypes = [wintypes.HMODULE]
module = kernel.LoadLibraryExW(exe, None, 2)
if not module:
    raise ctypes.WinError(ctypes.get_last_error())
try:
    groups = []
    callback_type = ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HMODULE, ctypes.c_void_p, ctypes.c_void_p, ctypes.c_ssize_t)
    @callback_type
    def callback(handle, resource_type, name, param):
        groups.append(name)
        return True
    kernel.EnumResourceNamesW.argtypes = [wintypes.HMODULE, ctypes.c_void_p, callback_type, ctypes.c_ssize_t]
    kernel.EnumResourceNamesW(module, 14, callback, 0)
    assert groups, "EXE has no RT_GROUP_ICON resource"
    kernel.FindResourceW.argtypes = [wintypes.HMODULE, ctypes.c_void_p, ctypes.c_void_p]
    kernel.FindResourceW.restype = wintypes.HANDLE
    resource = kernel.FindResourceW(module, groups[0], 14)
    kernel.LoadResource.argtypes = [wintypes.HMODULE, wintypes.HANDLE]
    kernel.LoadResource.restype = wintypes.HANDLE
    loaded = kernel.LoadResource(module, resource)
    kernel.LockResource.argtypes = [wintypes.HANDLE]
    kernel.LockResource.restype = ctypes.c_void_p
    pointer = kernel.LockResource(loaded)
    header = ctypes.string_at(pointer, 6)
    reserved, resource_type, count = struct.unpack("<HHH", header)
    assert resource_type == 1
    data = ctypes.string_at(pointer, 6 + 14 * count)
    sizes = {(data[6 + 14*i] or 256, data[7 + 14*i] or 256) for i in range(count)}
    assert sizes == {(16, 16), (32, 32), (48, 48), (256, 256)}, sizes
    print("PASS: native EXE icon resources", sorted(sizes))
finally:
    kernel.FreeLibrary(module)
