#nullable enable
namespace SignalFish.Client.Adapters.SteamworksNet.Editor
{
    using System;
    using System.Linq;
    using UnityEditor;
    using UnityEditor.Build;
    using UnityEditor.Compilation;

    /// <summary>
    /// Detects Steamworks.NET and toggles the
    /// <c>SIGNALFISH_STEAMWORKSNET</c> scripting define across every
    /// build target. The adapter asmdef's <c>versionDefines</c> key off
    /// Steamworks.NET's UPM package and remain the primary path; this
    /// detector only fills the vendored gap, where Steamworks.NET is a
    /// directory of sources no version define can see. Both mechanisms
    /// pin the same define, so whichever fires first (or both) lands
    /// the same contract.
    ///
    /// The probe is the UPM package marker or the compiled
    /// <c>com.rlabrecque.steamworks.net</c> assembly, so a fresh
    /// install needs one editor compile before the adapter lights up
    /// (the standard cost of define detectors).
    /// </summary>
    [InitializeOnLoad]
    internal static class SignalFishSteamworksNetDefineDetector
    {
        private const string Define = "SIGNALFISH_STEAMWORKSNET";

        /// <summary>The adapter pins Steamworks.NET's UPM package name.</summary>
        private const string SteamworksPackageName = "com.rlabrecque.steamworks.net";

        /// <summary>The adapter pins Steamworks.NET's assembly name.</summary>
        private const string SteamworksAssemblyName = "com.rlabrecque.steamworks.net";

        private const double RescanIntervalSeconds = 5.0;

        private static double _nextRescanTime;

        static SignalFishSteamworksNetDefineDetector()
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
            if (PackageInfo.FindForAssetPath($"Packages/{SteamworksPackageName}") != null)
            {
                return true;
            }

            return CompilationPipeline
                .GetAssemblies()
                .Any(assembly =>
                    string.Equals(assembly.name, SteamworksAssemblyName, StringComparison.Ordinal)
                );
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
                    The define without Steamworks.NET would dangle the
                    adapter's com.rlabrecque.steamworks.net reference;
                    removing it keeps the assembly skipped.
                */
                return string.Join(";", parts);
            }

            return string.Join(";", parts.Concat(new[] { Define }));
        }
    }
}
