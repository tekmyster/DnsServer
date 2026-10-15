using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;

namespace DnsServerCore.Dhcp
{
    // Flushing a file does not commit the directory entry created by rename on Linux.
    // The authority barrier must be durable before publishing a new snapshot or ACK.
    internal static class AllocationDurability
    {
        [DllImport("libc", EntryPoint = "open", SetLastError = true)]
        static extern int Open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);
        [DllImport("libc", EntryPoint = "fsync", SetLastError = true)]
        static extern int Sync(int descriptor);
        [DllImport("libc", EntryPoint = "close", SetLastError = true)]
        static extern int Close(int descriptor);

        internal static void SyncDirectory(string file)
        {
            if (!OperatingSystem.IsLinux()) return;
            string directory = Path.GetDirectoryName(Path.GetFullPath(file));
            int descriptor = Open(directory, 0x10000 | 0x80000); // O_DIRECTORY | O_CLOEXEC, read-only
            if (descriptor < 0) throw new IOException("Cannot open the allocation directory for a durable commit.", new Win32Exception(Marshal.GetLastPInvokeError()));
            try
            {
                while (Sync(descriptor) != 0)
                {
                    int error = Marshal.GetLastPInvokeError();
                    if (error == 4) continue; // EINTR
                    throw new IOException("Cannot commit the allocation directory; DHCP authority is withheld.", new Win32Exception(error));
                }
            }
            finally { Close(descriptor); }
        }
    }
}
