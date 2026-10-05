#nullable enable
namespace SignalFish.Client.Adapters.Fusion.Editor
{
    using System;
    using System.Linq;
    using UnityEditor;
    using UnityEditor.Build;
    using UnityEditor.Compilation;

    /// <summary>
    /// Detects Photon Fusion 2 and toggles the <c>SIGNALFISH_FUSION</c>
    /// scripting define across every build target. Fusion ships as an
    /// asset — precompiled assemblies under <c>Assets/Photon/Fusion</c>,
    /// not a UPM package the Package Manager knows — so this detector
    /// is the define's sole owner: the adapter asmdef carries no
    /// versionDefines pin, and without the define the package compiles
    /// to nothing.
    ///
    /// The probe is the loaded <c>Fusion.Runtime</c> assembly, by
    /// <see cref="AppDomain"/> name rather than
    /// <see cref="CompilationPipeline"/>: the Fusion runtime arrives as
    /// precompiled plugin DLLs, which the compilation pipeline does not
    /// list. A fresh import still needs one editor compile (domain
    /// reload) before the assembly is loaded and the adapter lights up
    /// (the standard cost of define detectors).
    /// </summary>
    [InitializeOnLoad]
    internal static class SignalFishFusionDefineDetector
    {
        private const string Define = "SIGNALFISH_FUSION";

        /// <summary>The adapter pins Fusion's runtime assembly name.</summary>
        private const string FusionAssemblyName = "Fusion.Runtime";

        private const double RescanIntervalSeconds = 5.0;

        private static double _nextRescanTime;

        static SignalFishFusionDefineDetector()
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
            Apply(FusionIsPresent());
        }

        private static bool FusionIsPresent()
        {
            return AppDomain
                .CurrentDomain.GetAssemblies()
                .Any(assembly =>
                    string.Equals(
                        assembly.GetName().Name,
                        FusionAssemblyName,
                        StringComparison.Ordinal
                    )
                );
        }

        private static void Apply(bool fusionPresent)
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
                    string updated = Toggle(current, fusionPresent);
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

        private static string Toggle(string defines, bool fusionPresent)
        {
            string[] parts = defines
                .Split(';')
                .Select(part => part.Trim())
                .Where(part => part.Length > 0)
                .Where(part => part != Define)
                .ToArray();
            if (!fusionPresent)
            {
                /*
                    The define without Fusion would dangle the adapter's
                    Fusion.Runtime reference; removing it keeps the
                    assembly skipped.
                */
                return string.Join(";", parts);
            }

            return string.Join(";", parts.Concat(new[] { Define }));
        }
    }
}
