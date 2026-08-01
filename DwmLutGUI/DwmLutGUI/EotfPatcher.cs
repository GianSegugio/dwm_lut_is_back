using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace DwmLutGUI
{
    /// <summary>
    /// Patches DWM's SDR-to-scRGB conversion shaders so SDR content is mapped into the HDR
    /// composition space with a pure gamma curve instead of the piecewise sRGB curve, which
    /// otherwise raises near-blacks and washes SDR content out in HDR mode.
    ///
    /// The sRGB constants inside the shader bytecode are rewritten so the piecewise curve
    /// collapses to a plain power law:
    ///     breakpoint 0.04045   -> 0   (the linear toe is never taken)
    ///     offset     0.055     -> 0
    ///     1/1.055 (0.94786733) -> 1
    ///     exponent   2.4       -> gamma
    ///
    /// WHY THIS RUNS FROM THE GUI RATHER THAN THE INJECTED DLL:
    /// DWM builds its pixel shader objects from these blobs during startup; once those objects
    /// exist, patching the bytecode has no effect. The patch therefore has to land in the window
    /// between DWM starting and DWM creating its shaders. Injecting a DLL to do it is a race
    /// against that window. Patching from outside lets us suspend DWM first, which closes the
    /// race completely - and suspending is only safe from outside, because forcing a LoadLibrary
    /// into a suspended process risks deadlocking on the loader lock.
    ///
    /// Writes go to the process's private copy of the mapped image, so dwmcore.dll on disk is
    /// never modified and the patch disappears when DWM restarts.
    ///
    /// The DXBC checksum routine below is a C# port of AMD's CalculateDXBCChecksum
    /// (GPUOpen common-src-ShaderUtils, MIT), which is itself derived from the RSA Data
    /// Security, Inc. MD5 Message Digest Algorithm. See LICENSE-THIRD-PARTY.
    /// </summary>
    internal static class EotfPatcher
    {
        // Blobs larger than this are treated as containers that other blobs nest inside.
        private const int ContainerMinSize = 8192;
        private const int MaxSites = 32;
        private const int DxbcHeaderSize = 32;   // 'DXBC' + 16-byte checksum + reserved + size + chunkCount

        /// <summary>
        /// The SDR-to-scRGB conversion shaders, identified by their DXBC checksum rather than by
        /// offset. Unchanged across 21H2 (22000) through the 26H2 preview (26100.8935).
        /// NOTE: dwmcore ships duplicate copies of some of these - every copy must be patched.
        /// </summary>
        private static readonly byte[][] TargetHashes =
        {
            new byte[] {0x96,0xe6,0xd1,0x58,0x92,0x55,0xec,0xcd,0x1d,0xd7,0xd4,0xdb,0xec,0x54,0xd2,0x85},
            new byte[] {0x21,0x26,0xb0,0x37,0xc1,0xa2,0xfb,0xdd,0xe3,0x55,0xb6,0xe6,0xdd,0x9c,0xaf,0x3c},
            new byte[] {0x2c,0x89,0x26,0xff,0xe2,0x29,0xf0,0x5d,0x96,0x7c,0x72,0x66,0x8d,0xc3,0xad,0xdb},
            new byte[] {0xf6,0x93,0xbf,0xbb,0xaf,0x24,0xb3,0xd9,0x36,0x63,0x54,0xbe,0x88,0x98,0xa7,0xf5}
        };

        private static readonly float[] SrgbConstants = { 2.4f, 0.04045f, 0.055f, 0.94786733f };

        #region P/Invoke

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool ReadProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress,
            [Out] byte[] lpBuffer, int dwSize, out IntPtr lpNumberOfBytesRead);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool WriteProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress,
            byte[] lpBuffer, int nSize, out IntPtr lpNumberOfBytesWritten);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool VirtualProtectEx(IntPtr hProcess, IntPtr lpAddress, IntPtr dwSize,
            uint flNewProtect, out uint lpflOldProtect);

        [DllImport("ntdll.dll")]
        private static extern int NtSuspendProcess(IntPtr processHandle);

        [DllImport("ntdll.dll")]
        private static extern int NtResumeProcess(IntPtr processHandle);

        private const uint PAGE_READWRITE = 0x04;

        #endregion

        #region DXBC checksum (port of AMD's CalculateDXBCChecksum)

        private const int HashOffset = 0x14;   // checksum covers the blob from offset 0x14 onwards

        private static readonly uint[] K = new uint[64];
        private static readonly int[] Shift =
        {
            7,12,17,22, 7,12,17,22, 7,12,17,22, 7,12,17,22,
            5, 9,14,20, 5, 9,14,20, 5, 9,14,20, 5, 9,14,20,
            4,11,16,23, 4,11,16,23, 4,11,16,23, 4,11,16,23,
            6,10,15,21, 6,10,15,21, 6,10,15,21, 6,10,15,21
        };
        private static readonly byte[] Padding = new byte[64];

        static EotfPatcher()
        {
            for (var i = 0; i < 64; i++) K[i] = (uint)(Math.Abs(Math.Sin(i + 1.0)) * 4294967296.0);
            Padding[0] = 0x80;
        }

        private static uint Rol(uint x, int c) => (x << c) | (x >> (32 - c));

        private static void Transform(uint[] h, byte[] buf, int off)
        {
            uint a = h[0], b = h[1], c = h[2], d = h[3];
            uint A = a, B = b, C = c, D = d;

            var m = new uint[16];
            for (var i = 0; i < 16; i++) m[i] = BitConverter.ToUInt32(buf, off + i * 4);

            for (var i = 0; i < 64; i++)
            {
                uint f;
                int g;
                if (i < 16) { f = (B & C) | (~B & D); g = i; }
                else if (i < 32) { f = (D & B) | (~D & C); g = (5 * i + 1) % 16; }
                else if (i < 48) { f = B ^ C ^ D; g = (3 * i + 5) % 16; }
                else { f = C ^ (B | ~D); g = 7 * i % 16; }

                f = f + A + K[i] + m[g];
                var oldB = B;
                A = D; D = C; C = B;
                B = oldB + Rol(f, Shift[i]);
            }

            h[0] = a + A; h[1] = b + B; h[2] = c + C; h[3] = d + D;
        }

        /// <summary>Computes the DXBC checksum of the blob at <paramref name="off"/>.</summary>
        private static byte[] ComputeChecksum(byte[] img, int off, int size)
        {
            var n = size - HashOffset;
            var bits = (uint)(n * 8);
            var h = new[] { 0x67452301u, 0xefcdab89u, 0x98badcfeu, 0x10325476u };

            var baseOff = off + HashOffset;
            var full = n & ~63;
            for (var i = 0; i < full; i += 64) Transform(h, img, baseOff + i);

            var tailLen = n - full;
            var padLen = 64 - tailLen;

            if (tailLen >= 56)
            {
                var blk = new byte[64];
                Buffer.BlockCopy(img, baseOff + full, blk, 0, tailLen);
                Buffer.BlockCopy(Padding, 0, blk, tailLen, padLen);
                Transform(h, blk, 0);

                var blk2 = new byte[64];
                Buffer.BlockCopy(BitConverter.GetBytes(bits), 0, blk2, 0, 4);
                Buffer.BlockCopy(BitConverter.GetBytes((bits >> 2) | 1), 0, blk2, 60, 4);
                Transform(h, blk2, 0);
            }
            else
            {
                var blk = new byte[64];
                Buffer.BlockCopy(BitConverter.GetBytes(bits), 0, blk, 0, 4);
                Buffer.BlockCopy(img, baseOff + full, blk, 4, tailLen);
                Buffer.BlockCopy(Padding, 0, blk, 4 + tailLen, 56 - tailLen);
                Buffer.BlockCopy(BitConverter.GetBytes((bits >> 2) | 1), 0, blk, 60, 4);
                Transform(h, blk, 0);
            }

            var result = new byte[16];
            for (var i = 0; i < 4; i++) Buffer.BlockCopy(BitConverter.GetBytes(h[i]), 0, result, i * 4, 4);
            return result;
        }

        #endregion

        #region Blob discovery

        private static bool IsBlob(byte[] img, int off, out int size)
        {
            size = 0;
            if (off + DxbcHeaderSize > img.Length) return false;
            if (img[off] != 'D' || img[off + 1] != 'X' || img[off + 2] != 'B' || img[off + 3] != 'C') return false;
            if (BitConverter.ToUInt32(img, off + 20) != 1) return false;   // reserved is always 1

            var sz = BitConverter.ToUInt32(img, off + 24);
            if (sz < DxbcHeaderSize || sz > 0x100000) return false;
            if (off + (long)sz > img.Length) return false;

            size = (int)sz;
            return true;
        }

        private static bool ChecksumMatchesStored(byte[] img, int off, int size)
        {
            var computed = ComputeChecksum(img, off, size);
            for (var i = 0; i < 16; i++)
                if (computed[i] != img[off + 4 + i]) return false;
            return true;
        }

        private static bool IsTarget(byte[] img, int off)
        {
            foreach (var t in TargetHashes)
            {
                var match = true;
                for (var i = 0; i < 16; i++)
                {
                    if (img[off + 4 + i] != t[i]) { match = false; break; }
                }
                if (match) return true;
            }
            return false;
        }

        /// <summary>
        /// Verifies the checksum implementation reproduces stored checksums on untouched blobs.
        /// If this fails we must not patch anything, so a broken implementation is a clean no-op
        /// rather than a corrupted compositor.
        /// </summary>
        private static bool SelfTest(byte[] img, int wanted = 8)
        {
            var tested = 0;
            var i = 0;
            while (i + DxbcHeaderSize < img.Length && tested < wanted)
            {
                if (!IsBlob(img, i, out var size)) { i++; continue; }
                if (size <= ContainerMinSize)
                {
                    if (!ChecksumMatchesStored(img, i, size)) return false;
                    tested++;
                }
                i += size;
            }
            return tested > 0;
        }

        private struct Site
        {
            public int Offset;
            public int Size;
            public int ContainerOffset;   // -1 when not nested
            public int ContainerSize;
        }

        private static List<Site> FindSites(byte[] img)
        {
            var sites = new List<Site>();
            var bigOff = -1;
            var bigSize = 0;
            var i = 0;

            while (i + DxbcHeaderSize < img.Length && sites.Count < MaxSites)
            {
                if (!IsBlob(img, i, out var size)) { i++; continue; }

                if (bigOff >= 0 && i >= bigOff + bigSize) { bigOff = -1; bigSize = 0; }

                if (IsTarget(img, i))
                {
                    sites.Add(new Site
                    {
                        Offset = i,
                        Size = size,
                        ContainerOffset = bigOff,
                        ContainerSize = bigSize
                    });
                    i += size;
                    continue;
                }

                if (bigOff < 0 && size > ContainerMinSize)
                {
                    bigOff = i;
                    bigSize = size;
                    i++;                 // descend into the container
                    continue;
                }

                i += size;
            }

            return sites;
        }

        #endregion

        private static int PatchConstant(byte[] img, int start, int len, float from, float to)
        {
            var needle = new byte[12];
            var repl = new byte[12];
            var f = BitConverter.GetBytes(from);
            var t = BitConverter.GetBytes(to);
            for (var k = 0; k < 3; k++)
            {
                Buffer.BlockCopy(f, 0, needle, k * 4, 4);
                Buffer.BlockCopy(t, 0, repl, k * 4, 4);
            }

            var hits = 0;
            for (var i = start; i + 12 <= start + len; i++)
            {
                var match = true;
                for (var j = 0; j < 12; j++)
                {
                    if (img[i + j] != needle[j]) { match = false; break; }
                }
                if (!match) continue;

                Buffer.BlockCopy(repl, 0, img, i, 12);
                hits++;
                i += 11;
            }
            return hits;
        }

        /// <summary>
        /// Suspends <paramref name="dwm"/>, patches every SDR-to-scRGB shader in its mapped
        /// dwmcore.dll, then resumes it. Returns the number of shader sites patched
        /// (0 = nothing changed, which is safe - typically because DWM is already patched).
        ///
        /// Call this on a freshly restarted DWM: the patch only affects shaders created
        /// afterwards.
        /// </summary>
        public static int PatchProcess(Process dwm, float gamma)
        {
            GuiDiag.Log("  [patch] dwm pid=" + dwm.Id + " gamma=" + gamma.ToString("0.00"));

            IntPtr moduleBase;
            int moduleSize;
            string modulePath = null;
            try
            {
                moduleBase = IntPtr.Zero;
                moduleSize = 0;
                foreach (ProcessModule m in dwm.Modules)
                {
                    if (!string.Equals(m.ModuleName, "dwmcore.dll", StringComparison.OrdinalIgnoreCase)) continue;
                    moduleBase = m.BaseAddress;
                    moduleSize = m.ModuleMemorySize;
                    modulePath = m.FileName;
                    break;
                }
            }
            catch (Exception ex)
            {
                GuiDiag.LogError("PatchProcess/enumerate modules (pid=" + dwm.Id + ")", ex);
                return 0;
            }

            if (moduleBase == IntPtr.Zero || moduleSize <= 0)
            {
                GuiDiag.Log("  [patch] dwmcore.dll not found in pid=" + dwm.Id + " -> skipping");
                return 0;
            }

            GuiDiag.Log("  [patch] dwmcore base=0x" + moduleBase.ToInt64().ToString("X") +
                        " size=" + moduleSize + " path=" + (modulePath ?? "?"));

            IntPtr handle;
            try
            {
                handle = dwm.Handle;
            }
            catch (Exception ex)
            {
                GuiDiag.LogError("PatchProcess/open handle (pid=" + dwm.Id + ")", ex);
                return 0;
            }

            var suspended = false;
            try
            {
                // Suspending before the read/patch/write cycle is what removes the race against
                // DWM creating its pixel shaders.
                var suspendResult = NtSuspendProcess(handle);
                suspended = suspendResult == 0;
                GuiDiag.Log("  [patch] NtSuspendProcess -> 0x" + suspendResult.ToString("X") +
                            (suspended ? " (suspended)" : " (NOT suspended - patch will race shader creation)"));

                var img = new byte[moduleSize];
                if (!ReadProcessMemory(handle, moduleBase, img, moduleSize, out var read) ||
                    read.ToInt64() < DxbcHeaderSize)
                {
                    GuiDiag.LogWin32("  [patch] ReadProcessMemory");
                    return 0;
                }
                GuiDiag.Log("  [patch] read " + read.ToInt64() + " bytes of mapped image");

                if (!SelfTest(img))
                {
                    GuiDiag.Log("  [patch] CHECKSUM SELF-TEST FAILED -> refusing to patch");
                    return 0;
                }
                GuiDiag.Log("  [patch] checksum self-test ok");

                var sites = FindSites(img);
                GuiDiag.Log("  [patch] found " + sites.Count + " target shader site(s)");
                foreach (var st in sites)
                {
                    GuiDiag.Log("      site @0x" + st.Offset.ToString("X") + " size=" + st.Size +
                                " container=" + (st.ContainerOffset >= 0
                                    ? "0x" + st.ContainerOffset.ToString("X") + " size=" + st.ContainerSize
                                    : "none"));
                }
                if (sites.Count == 0)
                {
                    GuiDiag.Log("  [patch] nothing to do (already patched, or hashes not present on this build)");
                    return 0;
                }

                var patched = new float[] { gamma, 0.0f, 0.0f, 1.0f };

                // Regions to write back: the container when nested, otherwise the shader itself.
                var regions = new Dictionary<int, int>();
                var patchedSites = 0;

                foreach (var site in sites)
                {
                    var body = site.Offset + DxbcHeaderSize;
                    var bodyLen = site.Size - DxbcHeaderSize;

                    var hits = 0;
                    for (var c = 0; c < SrgbConstants.Length; c++)
                        hits += PatchConstant(img, body, bodyLen, SrgbConstants[c], patched[c]);

                    GuiDiag.Log("      site @0x" + site.Offset.ToString("X") + ": " + hits + " constant triple(s) rewritten");
                    if (hits == 0) continue;

                    var sum = ComputeChecksum(img, site.Offset, site.Size);
                    Buffer.BlockCopy(sum, 0, img, site.Offset + 4, 16);
                    patchedSites++;

                    if (site.ContainerOffset >= 0) regions[site.ContainerOffset] = site.ContainerSize;
                    else regions[site.Offset] = site.Size;
                }

                if (patchedSites == 0)
                {
                    GuiDiag.Log("  [patch] no site actually contained the sRGB constants -> nothing written");
                    return 0;
                }

                // Containers embed the shaders, so their checksums have to be recomputed too.
                foreach (var kv in regions)
                {
                    if (!IsBlob(img, kv.Key, out var rsize)) continue;
                    var sum = ComputeChecksum(img, kv.Key, rsize);
                    Buffer.BlockCopy(sum, 0, img, kv.Key + 4, 16);
                }

                // Write each modified region back. These pages are read-only in the mapped image,
                // so they need to be made writable first; the write then lands in this process's
                // private copy and never touches dwmcore.dll on disk.
                var written = 0;
                foreach (var kv in regions)
                {
                    var addr = new IntPtr(moduleBase.ToInt64() + kv.Key);
                    var buf = new byte[kv.Value];
                    Buffer.BlockCopy(img, kv.Key, buf, 0, kv.Value);

                    if (!VirtualProtectEx(handle, addr, (IntPtr)kv.Value, PAGE_READWRITE, out var oldProtect))
                    {
                        GuiDiag.LogWin32("      VirtualProtectEx @0x" + kv.Key.ToString("X"));
                        continue;
                    }

                    if (WriteProcessMemory(handle, addr, buf, buf.Length, out _))
                    {
                        written++;
                    }
                    else
                    {
                        GuiDiag.LogWin32("      WriteProcessMemory @0x" + kv.Key.ToString("X"));
                    }

                    VirtualProtectEx(handle, addr, (IntPtr)kv.Value, oldProtect, out _);
                }

                GuiDiag.Log("  [patch] wrote " + written + "/" + regions.Count + " region(s), " +
                            patchedSites + " site(s) patched");
                return written > 0 ? patchedSites : 0;
            }
            catch (Exception ex)
            {
                GuiDiag.LogError("PatchProcess (pid=" + dwm.Id + ")", ex);
                return 0;
            }
            finally
            {
                // Must always run - leaving DWM suspended would freeze the desktop.
                if (suspended)
                {
                    var r = NtResumeProcess(handle);
                    GuiDiag.Log("  [patch] NtResumeProcess -> 0x" + r.ToString("X") +
                                (r == 0 ? "" : "  *** DWM MAY STILL BE SUSPENDED ***"));
                }
            }
        }
    }
}
