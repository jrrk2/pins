#region "copyright"

/*
    Copyright � 2016 - 2026 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors

    This file is part of N.I.N.A. - Nighttime Imaging 'N' Astronomy.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;

namespace NINA.Core.Utility {

    public static class DllLoader {

        [DllImport("libdl.so.2", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr dlopen(string filename, int flags);

        [DllImport("libdl.so.2", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr dlerror();

        [DllImport("libSystem.dylib", EntryPoint = "dlopen", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr dlopenMac(string filename, int flags);

        [DllImport("libSystem.dylib", EntryPoint = "dlerror", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr dlerrorMac();

        // dlopen flags; RTLD_GLOBAL differs between glibc and macOS
        private const int RTLD_NOW = 2;
        private static readonly int RTLD_GLOBAL = OperatingSystem.IsMacOS() ? 0x8 : 0x100;

        /// <summary>
        /// The folder under External that holds this platform's libraries, by .NET runtime identifier: linux-x64, osx-arm64, win-x64 and so on.
        /// </summary>
        public static string PlatformFolder {
            get {
                var os = OperatingSystem.IsMacOS() ? "osx" : OperatingSystem.IsWindows() ? "win" : "linux";
                return $"{os}-{RuntimeInformation.ProcessArchitecture.ToString().ToLower()}";
            }
        }

        /// <summary>
        /// This platform's name for a library the code names as on Linux: libnovas_c.so is libnovas_c.dylib on macOS and libnovas_c.dll on Windows.
        /// </summary>
        public static string NativeFileName(string linuxName) {
            if (!linuxName.EndsWith(".so", StringComparison.Ordinal)) {
                return linuxName;
            }
            return OperatingSystem.IsMacOS() ? Path.ChangeExtension(linuxName, ".dylib")
                : OperatingSystem.IsWindows() ? Path.ChangeExtension(linuxName, ".dll")
                : linuxName;
        }

        private static object lockobj = new object();
        private static System.Collections.Generic.HashSet<string> loadedDlls = new System.Collections.Generic.HashSet<string>();
        private static Dictionary<string, string> libraryPathMap = new Dictionary<string, string>(); // Maps library name to full path
        private static bool _resolverRegistered = false;

        public static void LoadDll(string dllSubPath) {
            lock (lockobj) {
                // Check if already loaded
                if (loadedDlls.Contains(dllSubPath)) {
                    return;
                }

                var requested = dllSubPath;

                // Add extension if not present
                if (!Path.HasExtension(dllSubPath)) {
                    dllSubPath = Path.ChangeExtension(dllSubPath, ".so");
                }
                // P/Invoke asks for the library by its Linux name, which is how the resolver below finds it on every platform
                var importName = Path.GetFileName(dllSubPath);
                dllSubPath = Path.Combine(Path.GetDirectoryName(dllSubPath) ?? string.Empty, NativeFileName(importName));

                // Try bundled library first
                var path = Path.Combine(System.AppDomain.CurrentDomain.BaseDirectory, "External", PlatformFolder, dllSubPath);
                if (File.Exists(path)) {
                    Logger.Info($"DllLoader: Loading bundled library: {path}");
                    if (LoadDllFromAbsolutePath(path)) {
                        loadedDlls.Add(requested);
                        // Track the library by filename for P/Invoke resolution
                        libraryPathMap.TryAdd(importName, path);
                        libraryPathMap.TryAdd(Path.GetFileName(path), path);
                        EnsureResolverRegistered();
                        return;
                    }
                }

                // Fallback to system libraries (using LD_LIBRARY_PATH)
                var libraryName = Path.GetFileName(dllSubPath);
                Logger.Info($"DllLoader: Trying system library via LD_LIBRARY_PATH: {libraryName}");

                if (LoadDllFromAbsolutePath(libraryName, global: true)) {
                    loadedDlls.Add(requested);
                    EnsureResolverRegistered();
                }
            }
        }

        private static void EnsureResolverRegistered() {
            lock (lockobj) {
                if (!_resolverRegistered) {
                    try {
                        // Register on NINA.Equipment assembly so all vendor SDKs can use it
                        var equipmentAssembly = AppDomain.CurrentDomain.GetAssemblies()
                            .FirstOrDefault(a => a.GetName().Name == "NINA.Equipment");

                        if (equipmentAssembly != null) {
                            NativeLibrary.SetDllImportResolver(equipmentAssembly, ResolveLibrary);
                            Logger.Debug("DllLoader: Registered DllImportResolver on NINA.Equipment assembly");
                        } else {
                            // Fallback: register on this assembly
                            NativeLibrary.SetDllImportResolver(typeof(DllLoader).Assembly, ResolveLibrary);
                            Logger.Debug("DllLoader: Registered DllImportResolver on NINA.Core assembly");
                        }
                    } catch (Exception ex) {
                        Logger.Warning($"DllLoader: Failed to register resolver: {ex.Message}");
                    }
                    _resolverRegistered = true;
                }
            }
        }

        private static IntPtr ResolveLibrary(string libraryName, Assembly assembly, DllImportSearchPath? searchPath) {
            lock (lockobj) {
                // Check if we have a tracked path for this library
                if (libraryPathMap.TryGetValue(libraryName, out var fullPath)) {
                    Logger.Debug($"DllLoader: Resolving {libraryName} to {fullPath}");
                    if (NativeLibrary.TryLoad(fullPath, out var handle)) {
                        return handle;
                    }
                }
            }
            return IntPtr.Zero;
        }

        public static bool LoadDllFromAbsolutePath(string dllPath, bool global = false) {
            lock (lockobj) {
                IntPtr handle = IntPtr.Zero;

                int flags = RTLD_NOW;
                if (global) {
                    flags |= RTLD_GLOBAL;
                }

                if (OperatingSystem.IsWindows()) {
                    if (NativeLibrary.TryLoad(dllPath, out _)) {
                        return true;
                    }
                    Logger.Error($"DllLoader failed to load library {dllPath}");
                    return false;
                }

                handle = OperatingSystem.IsMacOS() ? dlopenMac(dllPath, flags) : dlopen(dllPath, flags);
                if (handle == IntPtr.Zero) {
                    var errorPtr = OperatingSystem.IsMacOS() ? dlerrorMac() : dlerror();
                    var errorMessage = errorPtr != IntPtr.Zero ? Marshal.PtrToStringAnsi(errorPtr) : "Unknown error";
                    var message = $"DllLoader failed to load library {dllPath}: {errorMessage}";
                    Logger.Error(message);
                    return false;
                }
                return true;
            }
        }

        public static FileVersionInfo DllVersion(string dllSubPath) {
            // Add extension if not present
            if (!Path.HasExtension(dllSubPath)) {
                dllSubPath = Path.ChangeExtension(dllSubPath, ".so");
            }
            dllSubPath = Path.Combine(Path.GetDirectoryName(dllSubPath) ?? string.Empty, NativeFileName(Path.GetFileName(dllSubPath)));

            var path = Path.Combine(System.AppDomain.CurrentDomain.BaseDirectory, "External", PlatformFolder, dllSubPath);

            // On Unix-like systems, FileVersionInfo might not work well with native libraries
            // Return a minimal version info if the file exists
            if (File.Exists(path)) {
                return FileVersionInfo.GetVersionInfo(path);
            }
            return null;
        }

        public static bool IsX86() {
            return !Environment.Is64BitProcess;
        }

        public static string GetProcessorArchitecture() {
            var arch = RuntimeInformation.ProcessArchitecture;

            return arch switch {
                Architecture.X86 => "x86",
                Architecture.X64 => "x64",
                Architecture.Arm => "arm",
                Architecture.Arm64 => "arm64",
                _ => "unknown"
            };
        }
    }
}
