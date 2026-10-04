#nullable enable
namespace SignalFish.Client.Adapters.FishNet.Editor
{
    using System;
    using System.Linq;
    using UnityEditor;
    using UnityEditor.Build;
    using UnityEditor.Compilation;

    /// <summary>
    /// Detects FishNet installed loose under <c>Assets/</c> and toggles
    /// the <c>SIGNALFISH_FISHNET</c> scripting define across every build
    /// target. The asmdef's <c>versionDefines</c> key off FishNet's UPM
    /// package and remain the primary path; this detector only fills the
    /// vendored gap, where FishNet is a directory of asmdefs no version
    /// define can see. Both mechanisms pin the same define, so whichever
    /// fires first (or both) lands the same contract.
    ///
    /// The probe is the compiled <c>FishNet.Runtime</c> assembly or the
    /// UPM package marker, so a fresh install needs one editor compile
    /// before the adapter lights up (the standard cost of define
    /// detectors).
    /// </summary>
    [InitializeOnLoad]
    internal static class SignalFishFishNetDefineDetector
    {
        private const string Define = "SIGNALFISH_FISHNET";

        /// <summary>The adapter pins FishNet's core assembly name.</summary>
        private const string FishNetAssemblyName = "FishNet.Runtime";

        /// <summary>The adapter pins FishNet's UPM package name.</summary>
        private const string FishNetPackageName = "com.firstgeargames.fishnet";

        private const double RescanIntervalSeconds = 5.0;

        private static double _nextRescanTime;

        static SignalFishFishNetDefineDetector()
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
            Apply(FishNetIsPresent());
        }

        private static bool FishNetIsPresent()
        {
            if (PackageInfo.FindForAssetPath($"Packages/{FishNetPackageName}") != null)
            {
                return true;
            }

            return CompilationPipeline
                .GetAssemblies()
                .Any(assembly =>
                    string.Equals(assembly.name, FishNetAssemblyName, StringComparison.Ordinal)
                );
        }

        private static void Apply(bool fishNetPresent)
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
                    string updated = Toggle(current, fishNetPresent);
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

        private static string Toggle(string defines, bool fishNetPresent)
        {
            string[] parts = defines
                .Split(';')
                .Select(part => part.Trim())
                .Where(part => part.Length > 0)
                .Where(part => part != Define)
                .ToArray();
            if (!fishNetPresent)
            {
                /*
                    The define without FishNet would dangle the adapter's
                    FishNet.Runtime reference; removing it keeps the
                    assembly skipped.
                */
                return string.Join(";", parts);
            }

            return string.Join(";", parts.Concat(new[] { Define }));
        }
    }
}
