#nullable enable
namespace SignalFish.Client.Adapters.Pun2.Editor
{
    using System;
    using System.Linq;
    using UnityEditor;
    using UnityEditor.Build;
    using UnityEditor.Compilation;

    /// <summary>
    /// Detects Photon PUN2 and toggles the <c>SIGNALFISH_PUN2</c>
    /// scripting define across every build target. PUN2 ships as an
    /// asset (no UPM package), so this detector is the define's sole
    /// owner — the adapter asmdef carries no versionDefines pin, and
    /// without the define the package compiles to nothing.
    ///
    /// The probe is the compiled <c>PhotonUnityNetworking</c> assembly,
    /// so a fresh import needs one editor compile before the adapter
    /// lights up (the standard cost of define detectors).
    /// </summary>
    [InitializeOnLoad]
    internal static class SignalFishPun2DefineDetector
    {
        private const string Define = "SIGNALFISH_PUN2";

        /// <summary>The adapter pins PUN2's core assembly name.</summary>
        private const string PunAssemblyName = "PhotonUnityNetworking";

        private const double RescanIntervalSeconds = 5.0;

        private static double _nextRescanTime;

        static SignalFishPun2DefineDetector()
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
            Apply(PunIsPresent());
        }

        private static bool PunIsPresent()
        {
            return CompilationPipeline
                .GetAssemblies()
                .Any(assembly =>
                    string.Equals(assembly.name, PunAssemblyName, StringComparison.Ordinal)
                );
        }

        private static void Apply(bool punPresent)
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
                    string updated = Toggle(current, punPresent);
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

        private static string Toggle(string defines, bool punPresent)
        {
            string[] parts = defines
                .Split(';')
                .Select(part => part.Trim())
                .Where(part => part.Length > 0)
                .Where(part => part != Define)
                .ToArray();
            if (!punPresent)
            {
                /*
                    The define without PUN2 would dangle the adapter's
                    PhotonUnityNetworking reference; removing it keeps the
                    assembly skipped.
                */
                return string.Join(";", parts);
            }

            return string.Join(";", parts.Concat(new[] { Define }));
        }
    }
}
