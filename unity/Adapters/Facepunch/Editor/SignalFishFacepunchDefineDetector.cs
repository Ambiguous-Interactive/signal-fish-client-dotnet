#nullable enable
namespace SignalFish.Client.Adapters.Facepunch.Editor
{
    using System;
    using System.Linq;
    using UnityEditor;
    using UnityEditor.Build;
    using UnityEditor.Compilation;

    /// <summary>
    /// Detects Facepunch Steamworks and toggles the
    /// <c>SIGNALFISH_FACEPUNCH</c> scripting define across every build
    /// target. Facepunch Steamworks ships as an asset — precompiled
    /// platform assemblies (<c>Facepunch.Steamworks.Win32</c>,
    /// <c>.Win64</c>, <c>.Posix</c>) plus native binaries, not a UPM
    /// package the Package Manager knows — so this detector is the
    /// define's sole owner (the Mirror/PUN2/Fusion pattern; there is no
    /// versionDefines path to share the job with).
    ///
    /// The probe is the compiled Facepunch assembly, so a fresh install
    /// needs one editor compile before the adapter lights up (the
    /// standard cost of define detectors).
    /// </summary>
    [InitializeOnLoad]
    internal static class SignalFishFacepunchDefineDetector
    {
        private const string Define = "SIGNALFISH_FACEPUNCH";

        private const double RescanIntervalSeconds = 5.0;

        /// <summary>The adapter pins Facepunch Steamworks' assembly names (one per platform).</summary>
        private static readonly string[] SteamworksAssemblyNames =
        {
            "Facepunch.Steamworks.Win32",
            "Facepunch.Steamworks.Win64",
            "Facepunch.Steamworks.Posix",
        };

        private static double _nextRescanTime;

        static SignalFishFacepunchDefineDetector()
        {
            EditorApplication.update += Rescan;
        }

        private static void Rescan()
        {
            if (EditorApplication.timeSinceStartup < _nextRescanTime)
            {
                return;
            }

            _nextRescanTime = EditorApplication.timeSinceStartup + RescanIntervalSeconds;
            Apply(SteamworksIsPresent());
        }

        private static bool SteamworksIsPresent()
        {
            return CompilationPipeline
                .GetAssemblies()
                .Any(assembly => Array.IndexOf(SteamworksAssemblyNames, assembly.name) >= 0);
        }

        private static void Apply(bool steamworksPresent)
        {
            /*
                Valid build target groups vary by Unity version and license;
                an invalid one throws and is skipped rather than failing the
                whole pass.
            */
            foreach (BuildTargetGroup group in Enum.GetValues(typeof(BuildTargetGroup)))
            {
                if (group == BuildTargetGroup.Unknown)
                {
                    continue;
                }

                try
                {
                    NamedBuildTarget target = NamedBuildTarget.FromBuildTargetGroup(group);
                    string current = PlayerSettings.GetScriptingDefineSymbols(target);
                    string updated = Toggle(current, steamworksPresent);
                    if (updated != current)
                    {
                        PlayerSettings.SetScriptingDefineSymbols(target, updated);
                    }
                }
                catch (ArgumentException)
                {
                    // Not a valid scripting group on this Unity version.
                }
                catch (NotSupportedException)
                {
                    // Not a valid scripting group on this Unity version.
                }
            }
        }

        private static string Toggle(string defines, bool steamworksPresent)
        {
            string[] parts = defines
                .Split(';')
                .Select(part => part.Trim())
                .Where(part => part.Length > 0)
                .Where(part => part != Define)
                .ToArray();
            if (!steamworksPresent)
            {
                /*
                    The define without Facepunch Steamworks would dangle the
                    adapter's Facepunch assembly references; removing it keeps
                    the assembly skipped.
                */
                return string.Join(";", parts);
            }

            return string.Join(";", parts.Concat(new[] { Define }));
        }
    }
}
